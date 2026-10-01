using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PotPlayerLauncher;

/// <summary>
/// 监听 http://127.0.0.1:13579/, 收到播放请求后用 PotPlayer 打开该文件,
/// 并在播放器退出后把播放进度回传给 Jellyfin.
/// 作为 IHostedService 运行, 生命周期与 Jellyfin 服务器完全同步.
/// </summary>
public sealed class PotPlayerListener : IHostedService, IDisposable
{
    private const string DefaultPotPlayerPath = @"C:\Program Files (x86)\app\PotPlayer\PotPlayerMini64.exe";
    private const string DefaultPrefix = "http://127.0.0.1:13579/";
    private const string TokenFileName = "listener-token.txt";

    /// <summary>播放器退出后, 回传进度的最小播放秒数. 阈值以下视为误点, 不回传.</summary>
    private const int MinReportSeconds = 20;

    /// <summary>播放中向 Jellyfin 发送心跳 (Sessions/Playing/Progress) 的间隔.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly Regex PathRegex = new("[?&]path=([^&]+)", RegexOptions.Compiled);

    /// <summary>只放行本机 Jellyfin 页面作为 Origin (任意端口).</summary>
    private static readonly Regex LocalOriginRegex = new(
        @"^http://(localhost|127\.0\.0\.1)(:\d+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger<PotPlayerListener> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApplicationPaths _applicationPaths;
    private readonly CancellationTokenSource _stoppingCts = new();

    /// <summary>每次服务器启动随机生成, 只有同源的本机页面能通过 /token 取到.</summary>
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private HttpListener? _listener;
    private Task? _loopTask;
    private string _prefix = DefaultPrefix;

    public PotPlayerListener(
        ILogger<PotPlayerListener> logger,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _applicationPaths = applicationPaths;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _prefix = ResolvePrefix();
        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);

        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            // 端口被占用(例如旧的 listener.ps1 还在跑)时记录并放弃, 不影响 Jellyfin 启动
            _logger.LogError(ex, "PotPlayerLauncher failed to bind {Prefix}. Is another process already listening on port 13579?", _prefix);
            return Task.CompletedTask;
        }

        WriteTokenFile();
        _logger.LogInformation("PotPlayerLauncher listening on {Prefix}", _prefix);
        _loopTask = Task.Run(() => LoopAsync(_listener, _stoppingCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _stoppingCts.CancelAsync().ConfigureAwait(false);
            _listener?.Stop();
            if (_loopTask is not null)
            {
                await _loopTask.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 关闭阶段的异常一律忽略
        }
    }

    private async Task LoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PotPlayerListener accept error");
                continue;
            }

            _ = Task.Run(() => Handle(ctx), CancellationToken.None);
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        try
        {
            var origin = req.Headers["Origin"];
            var originAllowed = string.IsNullOrEmpty(origin) || LocalOriginRegex.IsMatch(origin);

            if (originAllowed && !string.IsNullOrEmpty(origin))
            {
                res.Headers.Add("Access-Control-Allow-Origin", origin);
                res.Headers.Add("Vary", "Origin");
            }

            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Emby-Token, X-PotPlayer-Token");
            res.Headers.Add("Access-Control-Max-Age", "600");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = originAllowed ? 204 : 403;
                res.Close();
                return;
            }

            if (!originAllowed)
            {
                _logger.LogWarning("PotPlayerLauncher rejected request from disallowed origin {Origin}", origin);
                WriteJson(res, 403, "{\"ok\":false,\"error\":\"origin not allowed\"}");
                return;
            }

            var path = req.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant();

            // 握手: 仅同源页面可取到本次启动的临时 token
            if (path == "/token")
            {
                WriteJson(res, 200, "{\"ok\":true,\"token\":\"" + _token + "\",\"version\":2}");
                return;
            }

            if (path == "/play" && req.HttpMethod == "POST")
            {
                HandlePlay(req, res);
                return;
            }

            // 兼容旧的 GET /play?path=... 形式(仍要求 token)
            if (path == "/play" && req.HttpMethod == "GET")
            {
                HandleLegacyPlay(req, res);
                return;
            }

            WriteJson(res, 404, "{\"ok\":false,\"error\":\"not found\"}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PotPlayerLauncher request handling failed");
            try
            {
                WriteJson(res, 500, "{\"ok\":false,\"error\":\"internal error\"}");
            }
            catch
            {
                // 响应已关闭时忽略
            }
        }
    }

    /// <summary>POST /play, JSON body: path / token? / apiKey / userId / itemId / mediaSourceId / startSec / totalSec.</summary>
    private void HandlePlay(HttpListenerRequest req, HttpListenerResponse res)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
        {
            body = reader.ReadToEnd();
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (Exception)
        {
            WriteJson(res, 400, "{\"ok\":false,\"error\":\"invalid json\"}");
            return;
        }

        var token = req.Headers["X-PotPlayer-Token"] ?? GetString(root, "token");
        if (!IsTokenValid(token))
        {
            _logger.LogWarning("PotPlayerLauncher: rejected request with invalid token");
            WriteJson(res, 401, "{\"ok\":false,\"error\":\"invalid token\"}");
            return;
        }

        var path = GetString(root, "path");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _logger.LogWarning("PotPlayerLauncher rejected request. path={Path} fileExists=false", path ?? "<null>");
            WriteJson(res, 403, "{\"ok\":false,\"error\":\"file not found\"}");
            return;
        }

        var playerPath = ResolvePlayerPath();
        if (!File.Exists(playerPath))
        {
            _logger.LogWarning("PotPlayerLauncher rejected request. playerExists=false player={Player}", playerPath);
            WriteJson(res, 403, "{\"ok\":false,\"error\":\"player not found\"}");
            return;
        }

        var apiKey = GetString(root, "apiKey");
        var userId = GetString(root, "userId");
        var itemId = GetString(root, "itemId");
        var mediaSourceId = GetString(root, "mediaSourceId");
        var startSec = GetLong(root, "startSec") ?? 0;
        var totalSec = GetLong(root, "totalSec") ?? 0;

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(userId))
        {
            WriteJson(res, 400, "{\"ok\":false,\"error\":\"apiKey/userId required\"}");
            return;
        }

        // 关键校验: token 必须真实有效, 且 token 能看到的用户里包含请求声明的 userId.
        var (tokenUserId, visibleUserIds) = InspectTokenAsync(apiKey).GetAwaiter().GetResult();
        if (visibleUserIds is null)
        {
            _logger.LogWarning("PotPlayerLauncher: Jellyfin rejected the supplied token");
            WriteJson(res, 401, "{\"ok\":false,\"error\":\"invalid jellyfin token\"}");
            return;
        }

        if (!visibleUserIds.Contains(userId))
        {
            _logger.LogWarning("PotPlayerLauncher: userId mismatch, claimed={Claimed} visible={Visible}", userId, string.Join(",", visibleUserIds));
            WriteJson(res, 403, "{\"ok\":false,\"error\":\"userId mismatch\"}");
            return;
        }

        if (tokenUserId is null)
        {
            // 管理员 API Key 没有用户上下文, Jellyfin 不会把这次播放写进用户数据
            _logger.LogWarning(
                "PotPlayerLauncher: the supplied token is an API key without a user context; "
                + "PotPlayer will open, but this playback's progress will NOT be saved to user data. "
                + "Use a browser session token (the userscript does this automatically).");
        }

        // 起播定位: 只有"有续播位置且未接近片尾"时才 /seek= 跳转, 否则从头播放
        var canResume = startSec > 30 && (totalSec <= 0 || startSec < totalSec - 30);
        if (!canResume)
        {
            startSec = 0;
        }

        var psi = new ProcessStartInfo
        {
            FileName = playerPath,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(path);
        string? seekArg = null;
        if (startSec > 0)
        {
            seekArg = "/seek=" + TimeSpan.FromSeconds(startSec).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            psi.ArgumentList.Add(seekArg);
        }

        Process? player;
        try
        {
            player = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PotPlayerLauncher failed to start player {Player}", playerPath);
            WriteJson(res, 500, "{\"ok\":false,\"error\":\"failed to start player\"}");
            return;
        }

        _logger.LogInformation(
            "PotPlayerLauncher launched PotPlayer for {Path} (pid={Pid} startSec={StartSec} seek={Seek})",
            path,
            player?.Id,
            startSec,
            seekArg ?? "<none>");

        // 先响应浏览器, 再在后台跟踪播放器生命周期并回传进度
        WriteJson(res, 200, "{\"ok\":true,\"pid\":" + (player?.Id ?? 0) + "}");

        _ = Task.Run(() => TrackPlaybackAsync(player, new PlaybackReport
        {
            // 上面的 apiKey/userId 非空校验已通过, 这里用 ! 消除可空性警告
            ApiKey = apiKey!,
            UserId = userId!,
            ItemId = itemId ?? string.Empty,
            MediaSourceId = mediaSourceId,
            StartSec = startSec,
            TotalSec = totalSec,
            FilePath = path
        }));
    }

    /// <summary>旧接口 GET /play?path=..., 保留但同样要求 token (放在 X-PotPlayer-Token 头里).</summary>
    private void HandleLegacyPlay(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!IsTokenValid(req.Headers["X-PotPlayer-Token"]))
        {
            _logger.LogWarning("PotPlayerLauncher: rejected legacy request with invalid token");
            WriteJson(res, 401, "{\"ok\":false,\"error\":\"invalid token\"}");
            return;
        }

        string? path = null;
        var match = PathRegex.Match(req.Url!.Query);
        if (match.Success)
        {
            path = Uri.UnescapeDataString(match.Groups[1].Value);
        }

        var playerPath = ResolvePlayerPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !File.Exists(playerPath))
        {
            _logger.LogWarning(
                "PotPlayerLauncher rejected legacy request. path={Path} fileExists={FileExists} playerExists={PlayerExists}",
                path ?? "<null>",
                string.IsNullOrWhiteSpace(path) ? "n/a" : File.Exists(path).ToString(),
                File.Exists(playerPath));
            WriteJson(res, 403, "{\"ok\":false,\"error\":\"file or player not found\"}");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = playerPath,
            Arguments = "\"" + path + "\"",
            UseShellExecute = true
        };
        using (Process.Start(psi))
        {
        }

        _logger.LogInformation("PotPlayerLauncher launched PotPlayer (legacy request) for {Path}", path);
        WriteJson(res, 200, "{\"ok\":true}");
    }

    /// <summary>
    /// 等待播放器退出, 期间按心跳上报进度, 退出后回传最终位置.
    /// 无法读取播放器内部进度, 因此用"进程存活时长"作为播放位置 (暂停会略偏低).
    /// </summary>
    private async Task TrackPlaybackAsync(Process? player, PlaybackReport info)
    {
        if (player is null)
        {
            return;
        }

        var startedAt = DateTime.UtcNow;
        var playSessionId = Guid.NewGuid().ToString("N");
        var stopped = false;

        try
        {
            await ReportAsync(info, "Playing", info.StartSec, playSessionId).ConfigureAwait(false);

            while (!player.HasExited)
            {
                try
                {
                    using var beatCts = new CancellationTokenSource(HeartbeatInterval);
                    await player.WaitForExitAsync(beatCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 心跳超时 = 播放器仍在播放
                }

                if (player.HasExited)
                {
                    break;
                }

                var position = (long)(DateTime.UtcNow - startedAt).TotalSeconds + info.StartSec;
                await ReportAsync(info, "Progress", position, playSessionId).ConfigureAwait(false);
            }

            stopped = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PotPlayerLauncher playback tracking failed for {Item}", info.ItemId);
        }
        finally
        {
            var elapsed = (long)(DateTime.UtcNow - startedAt).TotalSeconds;
            var position = info.StartSec + elapsed;

            if (stopped && elapsed >= MinReportSeconds)
            {
                await ReportAsync(info, "Stopped", position, playSessionId).ConfigureAwait(false);
            }
            else if (stopped)
            {
                _logger.LogInformation(
                    "PotPlayerLauncher: playback too short to report ({Elapsed}s < {Min}s) for {Item}",
                    elapsed,
                    MinReportSeconds,
                    info.ItemId);
            }

            player.Dispose();
        }
    }

    private async Task ReportAsync(PlaybackReport info, string kind, long positionSec, string playSessionId)
    {
        if (string.IsNullOrWhiteSpace(info.ItemId))
        {
            return;
        }

        if (positionSec < 0)
        {
            positionSec = 0;
        }

        // 不要超过媒体自身时长, 否则 Jellyfin 会把条目判为已看完
        if (info.TotalSec > 0 && positionSec > info.TotalSec - 10)
        {
            positionSec = Math.Max(0, info.TotalSec - 10);
        }

        var payload = new Dictionary<string, object?>
        {
            ["ItemId"] = info.ItemId,
            ["MediaSourceId"] = string.IsNullOrWhiteSpace(info.MediaSourceId) ? null : info.MediaSourceId,
            ["PlaySessionId"] = playSessionId,
            ["PositionTicks"] = positionSec * 10_000_000L,
            ["CanSeek"] = true,
            ["IsPaused"] = false,
            ["IsMuted"] = false,
            ["PlayMethod"] = "DirectStream",
            ["RepeatMode"] = "RepeatNone"
        };

        if (kind == "Progress")
        {
            payload["EventName"] = "timeupdate";
        }

        var url = kind switch
        {
            "Playing" => "Sessions/Playing",
            "Progress" => "Sessions/Playing/Progress",
            _ => "Sessions/Playing/Stopped"
        };

        try
        {
            var client = _httpClientFactory.CreateClient("PotPlayerLauncher");
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Emby-Token", info.ApiKey);
            request.Headers.TryAddWithoutValidation(
                "X-Emby-Authorization",
                "MediaBrowser Client=\"PotPlayerLauncher\", Device=\"PotPlayerLauncher\", DeviceId=\"potplayer-launcher\", Version=\"1.0.0\"");

            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "PotPlayerLauncher: {Kind} report for {Item} returned HTTP {Status}",
                    kind,
                    info.ItemId,
                    (int)response.StatusCode);
                return;
            }

            if (kind == "Stopped")
            {
                _logger.LogInformation(
                    "PotPlayerLauncher: reported progress for {File} position={Position}s (HTTP {Status})",
                    Path.GetFileName(info.FilePath),
                    positionSec,
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PotPlayerLauncher: {Kind} report failed for {Item}", kind, info.ItemId);
        }
    }

    /// <summary>
    /// 校验 Jellyfin token, 返回 (token 所属用户, token 能看到的用户集合); 无效返回 (null, null).
    /// 优先 /Users/Me: 浏览器会话 token 会返回当前用户, 于是能确认"这个 token 有用户上下文"
    /// (只有这种 token 的播放进度才会写进用户数据)。
    /// 管理员在控制台生成的 API Key 没有用户上下文, /Users/Me 会 400, 此时退回 /Users —— 它的身份
    /// 是管理员, 能返回全部用户, 可以用来核对 userId, 但用它回传的进度不会落到用户数据上。
    /// 调用方是同步等待(Handle 为同步方法), 所以这里单独用短超时的 client, 避免长时间占用监听线程.
    /// </summary>
    private async Task<(string? TokenUserId, HashSet<string>? VisibleUserIds)> InspectTokenAsync(string apiKey)
    {
        try
        {
            var factory = _httpClientFactory.CreateClient("PotPlayerLauncher");
            using var client = new HttpClient
            {
                BaseAddress = factory.BaseAddress,
                Timeout = TimeSpan.FromSeconds(5)
            };

            using var meResponse = await SendAsync(client, "Users/Me", apiKey).ConfigureAwait(false);
            if (meResponse.StatusCode == HttpStatusCode.OK)
            {
                var meJson = await meResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var meDoc = JsonDocument.Parse(meJson);
                var meId = meDoc.RootElement.TryGetProperty("Id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrEmpty(meId))
                {
                    return (null, null);
                }

                return (meId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { meId });
            }

            if (meResponse.StatusCode != HttpStatusCode.BadRequest)
            {
                _logger.LogWarning(
                    "PotPlayerLauncher: token check returned HTTP {Status}",
                    (int)meResponse.StatusCode);
                return (null, null);
            }

            using var usersResponse = await SendAsync(client, "Users", apiKey).ConfigureAwait(false);
            if (!usersResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "PotPlayerLauncher: token check returned HTTP {Status}",
                    (int)usersResponse.StatusCode);
                return (null, null);
            }

            var usersJson = await usersResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var usersDoc = JsonDocument.Parse(usersJson);
            if (usersDoc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (null, null);
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in usersDoc.RootElement.EnumerateArray())
            {
                if (user.TryGetProperty("Id", out var value) && value.GetString() is { } userValue)
                {
                    ids.Add(userValue);
                }
            }

            return (null, ids.Count > 0 ? ids : null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PotPlayerLauncher: token validation failed");
            return (null, null);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("X-Emby-Token", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private bool IsTokenValid(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(_token));
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static long? GetLong(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static void WriteJson(HttpListenerResponse res, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentType = "application/json; charset=utf-8";
        res.StatusCode = status;
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.Close();
    }

    /// <summary>
    /// 把本次启动的临时 token 写到插件数据目录, 供本机排查/手工调用使用.
    /// </summary>
    private void WriteTokenFile()
    {
        try
        {
            var dir = Path.Combine(_applicationPaths.PluginsPath, "PotPlayerLauncher");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, TokenFileName), _token, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PotPlayerLauncher: could not write token file");
        }
    }

    /// <summary>
    /// 端口默认 13579; 可用环境变量 POTPLAYER_LAUNCHER_PORT 覆盖.
    /// </summary>
    private string ResolvePrefix()
    {
        var port = 13579;
        var raw = Environment.GetEnvironmentVariable("POTPLAYER_LAUNCHER_PORT");
        if (int.TryParse(raw, out var parsed) && parsed is > 0 and < 65536)
        {
            port = parsed;
        }

        return $"http://127.0.0.1:{port}/";
    }

    /// <summary>
    /// 允许通过 config/PotPlayerLauncher.json (PotPlayerPath) 或环境变量 POTPLAYER_PATH 覆盖播放器路径.
    /// </summary>
    private string ResolvePlayerPath()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("POTPLAYER_PATH");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            {
                return env;
            }
        }
        catch
        {
            // 环境变量不可用时忽略
        }

        try
        {
            var cfgPath = Path.Combine(_applicationPaths.ConfigurationDirectoryPath, "PotPlayerLauncher.json");
            if (!File.Exists(cfgPath))
            {
                cfgPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Jellyfin", "Server", "config", "PotPlayerLauncher.json");
            }

            if (File.Exists(cfgPath))
            {
                var json = File.ReadAllText(cfgPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("PotPlayerPath", out var value))
                {
                    var cfgPlayer = value.GetString();
                    if (!string.IsNullOrWhiteSpace(cfgPlayer) && File.Exists(cfgPlayer))
                    {
                        return cfgPlayer;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PotPlayerLauncher: could not read config, using default player path");
        }

        return DefaultPotPlayerPath;
    }

    public void Dispose()
    {
        try
        {
            _stoppingCts.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private sealed class PlaybackReport
    {
        public string ApiKey { get; init; } = string.Empty;

        public string UserId { get; init; } = string.Empty;

        public string ItemId { get; init; } = string.Empty;

        public string? MediaSourceId { get; init; }

        public long StartSec { get; init; }

        public long TotalSec { get; init; }

        public string FilePath { get; init; } = string.Empty;
    }
}
