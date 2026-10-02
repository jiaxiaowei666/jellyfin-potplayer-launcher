using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PotPlayerLauncher;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PotPlayerLauncher.Tests;

/// <summary>
/// 插件行为测试: 真起一个 HttpListener, 用真实 HTTP 请求验证协议契约(docs/PROTOCOL.md)。
///
/// 设计要点:
/// - 播放器被替换成一个 .cmd 记录器, 它把自己的命令行参数写进文件并立即退出, 所以既不会弹出
///   真实播放器, 又能断言插件到底传了什么参数(例如续播的 /seek=)。
/// - 需要"Jellyfin 校验通过"的用例(成功启动、进度回传)依赖真实服务器与真实会话 token,
///   本地跑时自动跳过, 见 SKIP 行。
/// - 故意零 NuGet 依赖, 离线可跑。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _skipped;
    private static int _failed;
    private static string _port = "13599";
    private static string _playerExe = string.Empty;
    private static string _playerArgsFile = string.Empty;
    private static string Base => $"http://127.0.0.1:{_port}";

    /// <summary>桩 Jellyfin 的端口与用户 id(用自由端口, 避免碰用户本机的 8096)。</summary>
    private const int StubPort = 13596;
    private const string StubUserId = "stub-user";

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        _port = Environment.GetEnvironmentVariable("POTPLAYER_LAUNCHER_TEST_PORT") ?? "13599";
        Environment.SetEnvironmentVariable("POTPLAYER_LAUNCHER_PORT", _port);

        var tempRoot = Path.Combine(Path.GetTempPath(), "potplayer-launcher-tests-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new FakePaths(tempRoot);
        var mediaDir = Path.Combine(tempRoot, "media");
        Directory.CreateDirectory(mediaDir);

        var mediaFile = Path.Combine(mediaDir, "sample.mkv");
        await File.WriteAllTextAsync(mediaFile, "dummy");
        var cnFile = Path.Combine(mediaDir, "测试影片 第01集.mkv");
        await File.WriteAllTextAsync(cnFile, "dummy");

        // 假播放器: 记录参数后立刻退出(既不弹窗, 又能断言插件到底传了什么)。
        // 注意: 插件是 Process.Start(FileName=fakePlayer, ArgumentList=[路径, /seek=...]),
        // 直接启动 .cmd 会让 cmd 自己解析命令行(参数不会进 %*), 且空 cmd 会立刻退出,
        // 导致跟踪循环来不及产生心跳。所以这里把"播放器"做成一个 cmd 包装器, 它会
        //   1) 把收到的参数写进 player-args.txt
        //   2) 按 POTPLAYER_TEST_PLAYER_SECONDS 秒(默认 30)后杀掉自己, 模拟"播放器还在播"
        // 这样窗口是一个持久的 cmd.exe, 参数也如实传递。
        var recordFile = Path.Combine(tempRoot, "player-args.txt");
        var fakePlayer = Path.Combine(tempRoot, "fake-player.cmd");
        var aliveSeconds = Environment.GetEnvironmentVariable("POTPLAYER_TEST_PLAYER_SECONDS") ?? "45";
        _playerArgsFile = recordFile;
        // 插件用 Process.Start(FileName=fakePlayer, ArgumentList=[媒体路径, /seek=...]) 启动"播放器",
        // 于是 Windows 会用文件关联运行这个 .cmd。注意: 这种方式下 cmd **不会**把插件给的其他参数
        // 传进 %*(参数被吃掉) —— 这是 Windows 的行为, 测试里有一条用例专门断言它, 免得有人误以为
        // "记录器没写出 /seek" 是插件的 bug。脚本最后 sleep 一段时间, 用来模拟"播放器还在播放"。
        _playerExe = fakePlayer;
        await File.WriteAllTextAsync(
            fakePlayer,
            "@echo off\r\n"
            + "chcp 65001 >nul\r\n"
            + "> \"" + recordFile + "\" echo %*\r\n"
            + "ping -n " + aliveSeconds + " 127.0.0.1 >nul\r\n",
            Encoding.ASCII);

        // 注意: 环境变量在 .NET 里是进程启动时缓存的, SetEnvironmentVariable 不会改变本进程
        // GetEnvironmentVariable 的返回值, 所以测试**不能**靠 POTPLAYER_PATH 指定播放器,
        // 必须用插件每次请求都会重新读取的配置文件。
        var configFile = Path.Combine(paths.ConfigurationDirectoryPath, "PotPlayerLauncher.json");
        async Task UsePlayer(string playerPath) =>
            await File.WriteAllTextAsync(
                configFile,
                JsonSerializer.Serialize(new { PotPlayerPath = playerPath }),
                Encoding.UTF8);
        await UsePlayer(_playerExe);

        // 只有在显式给出服务器和会话 token 时才跑需要"真 Jellyfin 校验通过"的用例
        var liveBaseUrl = Environment.GetEnvironmentVariable("POTPLAYER_TEST_JELLYFIN_URL");
        var liveToken = Environment.GetEnvironmentVariable("POTPLAYER_TEST_USER_TOKEN");
        var liveUserId = Environment.GetEnvironmentVariable("POTPLAYER_TEST_USER_ID");
        var liveReady = !string.IsNullOrWhiteSpace(liveBaseUrl)
            && !string.IsNullOrWhiteSpace(liveToken)
            && !string.IsNullOrWhiteSpace(liveUserId);

        Console.WriteLine($"临时数据目录 : {tempRoot}");
        Console.WriteLine($"监听端口     : {_port}");
        Console.WriteLine($"假播放器     : {fakePlayer}");
        Console.WriteLine($"联机用例     : {(liveReady ? "启用 (" + liveBaseUrl + ")" : "跳过 (未设置 POTPLAYER_TEST_JELLYFIN_URL / _USER_TOKEN / _USER_ID)")}");

        PotPlayerListener? listener = null;
        JellyfinStub? stub = null;
        try
        {
            Section("1. 注册与启动");
            var services = new ServiceCollection();
            services.AddSingleton<ILogger<PotPlayerListener>>(NullLogger<PotPlayerListener>.Instance);
            services.AddSingleton<IApplicationPaths>(paths);
            services.AddHttpClient();

            // 不传 host: RegisterServices 只做 AddHttpClient + AddHostedService, 不调用 host 的方法
            new PluginServiceRegistrator().RegisterServices(services, null!);

            // 直接检查注册描述符, 这样测试工程不需要构造 DI 容器
            var hostedDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IHostedService));
            Check(hostedDescriptor is not null, "注册了 IHostedService");
            Check(
                hostedDescriptor?.ImplementationType == typeof(PotPlayerListener),
                $"注册的是 PotPlayerListener (实际 {hostedDescriptor?.ImplementationType?.Name ?? "无"})");

            // 全程使用桩 Jellyfin: 插件的回传地址由宿主注入(HttpClientFactory), 测试里指向桩,
            // 这样既不会去敲用户本机真 Jellyfin 的 8096, 也能断言回传内容。
            stub = JellyfinStub.TryStart(StubUserId, StubPort);
            Check(stub is not null, $"在端口 {StubPort} 起桩 Jellyfin(避免打扰本机真 Jellyfin)");
            if (stub is null)
            {
                return Report();
            }

            // 直接构造监听器, 依赖与 Jellyfin 运行时一致
            var httpFactory = new SimpleHttpClientFactory($"http://127.0.0.1:{StubPort}/");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            listener = new PotPlayerListener(NullLogger<PotPlayerListener>.Instance, httpFactory, paths);
            await listener.StartAsync(CancellationToken.None);
            await Task.Delay(400);

            Section("2. GET /token 握手");
            var tokenResp = await client.GetAsync(Base + "/token");
            var tokenBody = await tokenResp.Content.ReadAsStringAsync();
            Check(tokenResp.StatusCode == HttpStatusCode.OK, $"返回 200 (实际 {(int)tokenResp.StatusCode})");

            string? token = null;
            var version = 0;
            try
            {
                using var doc = JsonDocument.Parse(tokenBody);
                token = doc.RootElement.GetProperty("token").GetString();
                version = doc.RootElement.GetProperty("version").GetInt32();
            }
            catch (Exception ex)
            {
                Check(false, $"响应是合法 JSON: {ex.Message}");
            }

            Check(!string.IsNullOrWhiteSpace(token), "token 非空");
            Check(version == 2, $"version == 2 (实际 {version})");

            var tokenFile = Path.Combine(paths.PluginsPath, "PotPlayerLauncher", "listener-token.txt");
            Check(File.Exists(tokenFile), "listener-token.txt 已写入插件目录");
            if (File.Exists(tokenFile))
            {
                Check((await File.ReadAllTextAsync(tokenFile)).Trim() == token, "文件中的 token 与握手返回一致");
            }

            Section("3. CORS 与 Origin 白名单");
            using (var good = new HttpRequestMessage(HttpMethod.Get, Base + "/token"))
            {
                good.Headers.TryAddWithoutValidation("Origin", "http://localhost:8096");
                using var resp = await client.SendAsync(good);
                var acao = resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? string.Join(",", v) : "";
                Check(resp.StatusCode == HttpStatusCode.OK, "本机 Origin 放行");
                Check(acao == "http://localhost:8096", $"回显 Origin (实际 '{acao}')");
            }

            using (var otherPort = new HttpRequestMessage(HttpMethod.Get, Base + "/token"))
            {
                otherPort.Headers.TryAddWithoutValidation("Origin", "http://127.0.0.1:5000");
                using var resp = await client.SendAsync(otherPort);
                Check(resp.StatusCode == HttpStatusCode.OK, "本机其他端口也放行");
            }

            using (var evil = new HttpRequestMessage(HttpMethod.Get, Base + "/token"))
            {
                evil.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");
                using var resp = await client.SendAsync(evil);
                Check(resp.StatusCode == HttpStatusCode.Forbidden, $"外来 Origin 拒绝 (实际 {(int)resp.StatusCode})");
                Check(!resp.Headers.Contains("Access-Control-Allow-Origin"), "外来 Origin 不下发 CORS 头");
            }

            using (var lan = new HttpRequestMessage(HttpMethod.Get, Base + "/token"))
            {
                lan.Headers.TryAddWithoutValidation("Origin", "http://192.168.1.10:8096");
                using var resp = await client.SendAsync(lan);
                Check(resp.StatusCode == HttpStatusCode.Forbidden, "局域网 IP 的 Origin 拒绝");
            }

            Section("4. OPTIONS 预检");
            using (var pre = new HttpRequestMessage(HttpMethod.Options, Base + "/play"))
            {
                pre.Headers.TryAddWithoutValidation("Origin", "http://127.0.0.1:8096");
                pre.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type,x-potplayer-token");
                using var resp = await client.SendAsync(pre);
                var allow = resp.Headers.TryGetValues("Access-Control-Allow-Headers", out var v) ? string.Join(",", v) : "";
                Check(resp.StatusCode == HttpStatusCode.NoContent, $"返回 204 (实际 {(int)resp.StatusCode})");
                Check(allow.Contains("X-PotPlayer-Token", StringComparison.OrdinalIgnoreCase), "允许 X-PotPlayer-Token");
                Check(allow.Contains("Content-Type", StringComparison.OrdinalIgnoreCase), "允许 Content-Type");
            }

            Section("5. POST /play 参数校验与鉴权");
            var validBody = Body(mediaFile, "definitely-not-a-real-token", "some-user-id");

            var missingToken = await Post(client, null, validBody);
            Check(missingToken.Status == 401, $"缺临时 token -> 401 (实际 {missingToken.Status})");
            Check(missingToken.Body.Contains("invalid token"), "错误标识为 invalid token");

            var wrongToken = await Post(client, "deadbeef", validBody);
            Check(wrongToken.Status == 401, "错误临时 token -> 401");

            var badJson = await Post(client, token, "{ this is not json");
            Check(badJson.Status == 400, $"非法 JSON -> 400 (实际 {badJson.Status})");
            Check(badJson.Body.Contains("invalid json"), "错误标识为 invalid json");

            var missingFile = await Post(client, token, Body(Path.Combine(tempRoot, "nope.mkv"), "k", "u"));
            Check(missingFile.Status == 403, $"文件不存在 -> 403 (实际 {missingFile.Status})");
            Check(missingFile.Body.Contains("file not found"), "错误标识为 file not found");

            var noKey = await Post(client, token, $$"""{"path":{{Json(mediaFile)}},"userId":"u"}""");
            Check(noKey.Status == 400, $"缺 apiKey -> 400 (实际 {noKey.Status})");
            Check(noKey.Body.Contains("apiKey/userId required"), "错误标识为 apiKey/userId required");

            var noUser = await Post(client, token, $$"""{"path":{{Json(mediaFile)}},"apiKey":"k"}""");
            Check(noUser.Status == 400, "缺 userId -> 400");

            // 播放器不存在要在 Jellyfin 校验之前被拦下(本地检查优先于网络往返)
            await UsePlayer(Path.Combine(tempRoot, "no-such-player.exe"));
            var noPlayer = await Post(client, token, validBody);
            Check(noPlayer.Status == 403, $"播放器不存在 -> 403 (实际 {noPlayer.Status})");
            Check(noPlayer.Body.Contains("player not found"), $"错误标识为 player not found (实际 {noPlayer.Body})");
            await UsePlayer(_playerExe);

            // 本机若没有 Jellyfin(或 token 无效), 必须拒绝且不得启动播放器
            File.Delete(recordFile);
            var rejected = await Post(client, token, validBody);
            if (rejected.Status == 401)
            {
                Check(rejected.Body.Contains("invalid jellyfin token"), "错误标识为 invalid jellyfin token");
            }
            else
            {
                // 本机确实有 Jellyfin 接受了这个 token, 那 userId 必然对不上(伪造的)
                Check(rejected.Status == 403, $"本机 Jellyfin 可用时 -> 403 userId mismatch (实际 {rejected.Status} {rejected.Body})");
                Check(rejected.Body.Contains("userId mismatch"), "错误标识为 userId mismatch");
            }

            Check(!File.Exists(recordFile), "被拒绝时没有启动播放器");

            Section("6. GET /play 旧接口");
            var legacyNoToken = await client.GetAsync(Base + "/play?path=" + Uri.EscapeDataString(mediaFile));
            Check(legacyNoToken.StatusCode == HttpStatusCode.Unauthorized, $"缺 token -> 401 (实际 {(int)legacyNoToken.StatusCode})");

            using (var legacy = new HttpRequestMessage(HttpMethod.Get, Base + "/play?path=" + Uri.EscapeDataString(mediaFile)))
            {
                legacy.Headers.TryAddWithoutValidation("X-PotPlayer-Token", token);
                using var resp = await client.SendAsync(legacy);
                Check(resp.StatusCode == HttpStatusCode.OK, $"带 token + 真实文件 -> 200 (实际 {(int)resp.StatusCode})");
            }

            await WaitForFile(recordFile);
            Check(File.Exists(recordFile), "旧接口确实启动了播放器(记录器写出了参数)");
            if (File.Exists(recordFile))
            {
                var recorded = (await File.ReadAllTextAsync(recordFile)).Trim();
                Check(recorded.Contains("sample.mkv", StringComparison.OrdinalIgnoreCase), $"播放器收到的参数含文件路径 (实际 '{recorded}')");
                Check(!recorded.Contains("/seek=", StringComparison.OrdinalIgnoreCase), "旧接口不传 /seek");
            }

            using (var legacyCn = new HttpRequestMessage(HttpMethod.Get, Base + "/play?path=" + Uri.EscapeDataString(cnFile)))
            {
                legacyCn.Headers.TryAddWithoutValidation("X-PotPlayer-Token", token);
                using var resp = await client.SendAsync(legacyCn);
                Check(resp.StatusCode == HttpStatusCode.OK, $"中文路径 -> 200 (实际 {(int)resp.StatusCode})");
            }

            // 记录器是另一个进程, 上一次写的内容还在文件里, 所以这里等"内容里出现中文"而不是等文件存在
            var cnRecorded = await WaitForFileContent(recordFile, "测试影片");
            Check(cnRecorded, $"中文路径没有乱码 (实际 '{(File.Exists(recordFile) ? (await File.ReadAllTextAsync(recordFile)).Trim() : "无文件")}')");

            using (var legacyMissing = new HttpRequestMessage(HttpMethod.Get, Base + "/play?path=" + Uri.EscapeDataString(Path.Combine(tempRoot, "nope.mkv"))))
            {
                legacyMissing.Headers.TryAddWithoutValidation("X-PotPlayer-Token", token);
                using var resp = await client.SendAsync(legacyMissing);
                Check(resp.StatusCode == HttpStatusCode.Forbidden, "旧接口文件不存在 -> 403");
            }

            Section("7. 未知路径");
            var unknown = await client.GetAsync(Base + "/whatever");
            Check(unknown.StatusCode == HttpStatusCode.NotFound, $"未知路径 -> 404 (实际 {(int)unknown.StatusCode})");
            var root = await client.GetAsync(Base + "/");
            Check(root.StatusCode == HttpStatusCode.NotFound, $"根路径 -> 404 (实际 {(int)root.StatusCode})");

            Section("8. 端口冲突降级");
            // 先占住一个端口, 再看监听器是否优雅失败(而不是抛异常)
            var occupied = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            occupied.Start();
            var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
            Environment.SetEnvironmentVariable("POTPLAYER_LAUNCHER_PORT", occupiedPort.ToString());
            var second = new PotPlayerListener(
                NullLogger<PotPlayerListener>.Instance,
                httpFactory,
                paths);
            Exception? startAgain = null;
            try
            {
                await second.StartAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                startAgain = ex;
            }

            Check(startAgain is null, $"端口已被占用时不抛异常 (实际 {startAgain?.GetType().Name ?? "无异常"})");
            await second.StopAsync(CancellationToken.None);
            occupied.Stop();
            Environment.SetEnvironmentVariable("POTPLAYER_LAUNCHER_PORT", _port);

            Section("9. 需要真实 Jellyfin 的用例");
            if (!liveReady)
            {
                Skip("启动播放器 + 续播 /seek= (需要 POTPLAYER_TEST_JELLYFIN_URL / _USER_TOKEN / _USER_ID)");
                Skip("进度回传 Sessions/Playing* (同上)");
            }
            else
            {
                // 插件固定回调本机 8096, 所以联机用例要求被测服务器就在本机同一端口
                var resumeBody = $$"""
                {"path":{{Json(mediaFile)}},"apiKey":{{Json(liveToken!)}},"userId":{{Json(liveUserId!)}},"itemId":"test-item","startSec":300,"totalSec":745}
                """;

                File.Delete(recordFile);
                var launched = await Post(client, token, resumeBody);
                await Task.Delay(500);
                Check(launched.Status == 200, $"会话 token 可播放 -> 200 (实际 {launched.Status} {launched.Body})");
                Check(File.Exists(recordFile), "播放器已启动");
                if (File.Exists(recordFile))
                {
                    var recorded = (await File.ReadAllTextAsync(recordFile)).Trim();
                    Check(recorded.Contains("/seek=00:05:00", StringComparison.OrdinalIgnoreCase), $"续播参数 /seek=00:05:00 (实际 '{recorded}')");
                }
            }

            Section("10. 播放位置策略(纯逻辑)");
            Check(!PlayerPositionPolicy.IsTrustworthy(null, 300, 10), "探针返回 null -> 不可信");
            Check(!PlayerPositionPolicy.IsTrustworthy(-1, 300, 10), "探针返回负数 -> 不可信");
            Check(PlayerPositionPolicy.IsTrustworthy(305, 300, 10), "略超起播点 -> 可信");
            Check(PlayerPositionPolicy.IsTrustworthy(310, 300, 10), "正常读数 -> 可信");
            Check(PlayerPositionPolicy.IsTrustworthy(0, 0, 5), "0 秒 -> 可信");
            Check(
                !PlayerPositionPolicy.IsTrustworthy(300 + 10 + PlayerPositionPolicy.MaxAheadSeconds + 1, 300, 10),
                $"超出上限(可能读到别的实例) -> 不可信 (上限 {PlayerPositionPolicy.MaxAheadSeconds}s)");

            Section("11. 探针读数真实回传(桩 Jellyfin + 假探针)");
            {
                {
                    const string stubToken = "stub-api-key";
                    const string probePort = "13598";
                    Environment.SetEnvironmentVariable("POTPLAYER_LAUNCHER_PORT", probePort);

                    var probeListener = new PotPlayerListener(
                        NullLogger<PotPlayerListener>.Instance,
                        httpFactory,
                        paths,
                        // 真实探针的读数总是紧贴"起播 + 实际播放时长"(每 2 秒读一次, 略快于墙钟),
                        // 所以脚本也按这个量级给值 —— 给 300/800 这种会(正确地)被判为不可信。
                        new ScriptedProbe(new long[] { 5, 7, 12, 14, 17, 19, 22, 24, 27, 29, 32, 34 }));
                    await probeListener.StartAsync(CancellationToken.None);
                    await Task.Delay(400);

                    using var probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var probeToken = JsonDocument.Parse(await probeClient.GetStringAsync($"http://127.0.0.1:{probePort}/token"))
                        .RootElement.GetProperty("token").GetString();

                    async Task<(int Status, string Body)> PostProbe(string json)
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{probePort}/play")
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        };
                        req.Headers.TryAddWithoutValidation("X-PotPlayer-Token", probeToken);
                        using var resp = await probeClient.SendAsync(req);
                        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
                    }

                    var probeBody = $$"""
                    {"path":{{Json(mediaFile)}},"apiKey":{{Json(stubToken)}},"userId":{{Json(StubUserId)}},"itemId":"probe-item","startSec":0,"totalSec":745}
                    """;

                    var playingBefore = stub.CountOf("Playing");
                    var launched = await PostProbe(probeBody);
                    Check(launched.Status == 200, $"桩 Jellyfin 接受 token -> 200 (实际 {launched.Status} {launched.Body})");
                    Check(stub.Requests.Any(r => r.Contains("/Users/Me")), "插件用 /Users/Me 校验了 token");

                    // 上报是异步发的, 等它到达再断言
                    for (var i = 0; i < 20 && stub.CountOf("Playing") == playingBefore; i++)
                    {
                        await Task.Delay(200);
                    }

                    Check(stub.CountOf("Playing") > playingBefore, "收到了 Playing 上报");

                    // 探针每 2 秒读一次, 心跳每 15 秒上报一次。
                    // 到 20 秒时: 探针读数 ~27-29s, 而"存活时长"只有 ~20s —— 两者可区分。
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    var progress = stub.PositionOf("Progress");
                    Check(progress is not null, $"收到了 Progress 心跳 (实际 {progress?.ToString() ?? "没有"})");
                    Check(
                        progress is >= 20 and < 34,
                        $"Progress 用的是探针读数(脚本推进速度快于墙钟), 而不是存活时长(约 20s) (实际 {progress}s)");

                    var elapsedAtKill = 20;

                    // 强杀播放器(模拟崩溃): 仍应回传最后一次探针读数
                    foreach (var cmdProcess in Process.GetProcessesByName("cmd").ToArray())
                    {
                        try
                        {
                            cmdProcess.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            // 已退出
                        }
                    }

                    for (var i = 0; i < 40 && stub.PositionOf("Stopped") is null; i++)
                    {
                        await Task.Delay(500);
                    }

                    var stopped = stub.PositionOf("Stopped");
                    Check(stopped is not null, $"收到 Stopped (实际 {stopped?.ToString() ?? "没有"})");
                    Check(
                        stopped > elapsedAtKill,
                        $"Stopped 用的是探针最后一次读数({stopped}s), 高于存活时长({elapsedAtKill}s) —— 不是估算值");
                    Check(!stub.Requests.Any(r => r.Contains("/Users\"")), "没有多余的用户列表请求");

                    await probeListener.StopAsync(CancellationToken.None);

                    // 降级路径: 探针一直失败(换成别的播放器/权限不足/窗口类名变了)时必须退回估算,
                    // 且要播够 MinReportSeconds(20s) 才会回传
                    const string fallbackPort = "13597";
                    Environment.SetEnvironmentVariable("POTPLAYER_LAUNCHER_PORT", fallbackPort);
                    var fallbackListener = new PotPlayerListener(
                        NullLogger<PotPlayerListener>.Instance,
                        httpFactory,
                        paths,
                        new ScriptedProbe(Array.Empty<long>(), alwaysFail: true));
                    await fallbackListener.StartAsync(CancellationToken.None);
                    await Task.Delay(400);

                    using var fbClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var fbToken = JsonDocument.Parse(await fbClient.GetStringAsync($"http://127.0.0.1:{fallbackPort}/token"))
                        .RootElement.GetProperty("token").GetString();

                    var fbBody = $$"""
                    {"path":{{Json(mediaFile)}},"apiKey":{{Json(stubToken)}},"userId":{{Json(StubUserId)}},"itemId":"fallback-item","startSec":0,"totalSec":745}
                    """;
                    var stoppedBeforeFallback = stub.CountOf("Stopped");
                    using (var fbReq = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{fallbackPort}/play")
                    {
                        Content = new StringContent(fbBody, Encoding.UTF8, "application/json")
                    })
                    {
                        fbReq.Headers.TryAddWithoutValidation("X-PotPlayer-Token", fbToken);
                        using var fbResp = await fbClient.SendAsync(fbReq);
                        Check((int)fbResp.StatusCode == 200, $"探针不可用时仍能播放 -> 200 (实际 {(int)fbResp.StatusCode})");
                    }

                    // 播够 20 秒, 然后强杀 -> 应回传"约等于播放时长"的估算值
                    await Task.Delay(TimeSpan.FromSeconds(22));
                    foreach (var cmdProcess in Process.GetProcessesByName("cmd").ToArray())
                    {
                        try
                        {
                            cmdProcess.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            // 已退出
                        }
                    }

                    for (var i = 0; i < 40 && stub.CountOf("Stopped") == stoppedBeforeFallback; i++)
                    {
                        await Task.Delay(500);
                    }

                    var fbStopped = stub.PositionOf("Stopped");
                    Check(
                        stub.CountOf("Stopped") > stoppedBeforeFallback,
                        $"探针不可用时也回传了 Stopped (次数 {stoppedBeforeFallback} -> {stub.CountOf("Stopped")})");
                    Check(
                        fbStopped is >= 20 and < 40,
                        $"退回估算: 位置≈播放时长(期望 20~40s), 而不是探针值 (实际 {fbStopped}s)");

                    await fallbackListener.StopAsync(CancellationToken.None);
                    Environment.SetEnvironmentVariable("POTPLAYER_LAUNCHER_PORT", _port);
                }
            }
        }
        catch (Exception ex)
        {
            Check(false, $"未预期异常: {ex}");
        }
        finally
        {
            if (listener is not null)
            {
                try { await listener.StopAsync(CancellationToken.None); } catch { /* ignore */ }
            }

            stub?.Dispose();
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* ignore */ }
        }

        return Report();
    }

    // ---------- 辅助 ----------

    /// <summary>
    /// 桩 Jellyfin: 监听 127.0.0.1:8096(插件写死的回传地址), 记录收到的请求并按请求体记下
    /// 回传的位置, 供断言使用。8096 被占用时 TryStart 返回 null, 相关用例跳过。
    /// </summary>
    private sealed class JellyfinStub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<string> _requests = new();
        private readonly List<(string Kind, long PositionSec)> _reports = new();
        private readonly object _lock = new();
        private readonly string _userId;

        private JellyfinStub(string userId, int port)
        {
            _userId = userId;
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public IReadOnlyList<string> Requests
        {
            get { lock (_lock) { return _requests.ToArray(); } }
        }

        public IReadOnlyList<(string Kind, long PositionSec)> Reports
        {
            get { lock (_lock) { return _reports.ToArray(); } }
        }

        /// <summary>
        /// 在指定端口起桩。插件本身用相对地址回调, 回传地址由测试注入的 HttpClientFactory 决定,
        /// 所以桩不需要挤在 8096(那里可能真跑着 Jellyfin)。
        /// </summary>
        public static JellyfinStub? TryStart(string userId, int port)
        {
            try
            {
                return new JellyfinStub(userId, port);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>最近一次该类上报的位置(秒), 没有则 null。</summary>
        public long? PositionOf(string kind)
        {
            lock (_lock)
            {
                for (var i = _reports.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_reports[i].Kind, kind, StringComparison.OrdinalIgnoreCase))
                    {
                        return _reports[i].PositionSec;
                    }
                }
            }

            return null;
        }

        /// <summary>某一类上报出现的次数(用来等待"新的那一次"到达)。</summary>
        public int CountOf(string kind)
        {
            lock (_lock)
            {
                return _reports.Count(r => string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase));
            }
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                body = reader.ReadToEnd();
            }

            lock (_lock)
            {
                _requests.Add($"{ctx.Request.HttpMethod} {path}");

                if (path.EndsWith("/Sessions/Playing", StringComparison.OrdinalIgnoreCase))
                {
                    Record("Playing", body);
                }
                else if (path.Contains("Sessions/Playing/Progress", StringComparison.OrdinalIgnoreCase))
                {
                    Record("Progress", body);
                }
                else if (path.Contains("Sessions/Playing/Stopped", StringComparison.OrdinalIgnoreCase))
                {
                    Record("Stopped", body);
                }
            }

            var payload = path.EndsWith("/Users", StringComparison.OrdinalIgnoreCase)
                ? $"[{{\"Id\":\"{_userId}\",\"Name\":\"tester\"}}]"
                : path.EndsWith("/Users/Me", StringComparison.OrdinalIgnoreCase)
                    ? $"{{\"Id\":\"{_userId}\",\"Name\":\"tester\"}}"
                    : "{}";

            var response = Encoding.UTF8.GetBytes(payload);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = response.Length;
            try
            {
                ctx.Response.OutputStream.Write(response, 0, response.Length);
                ctx.Response.Close();
            }
            catch
            {
                // 客户端已断开
            }
        }

        private void Record(string kind, string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var ticks = doc.RootElement.TryGetProperty("PositionTicks", out var v) ? v.GetInt64() : 0;
                _reports.Add((kind, ticks / 10_000_000));
            }
            catch
            {
                // 无法解析就不记录
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>按脚本给出播放位置(秒)的假探针; 脚本用完后停在最后一个值。</summary>
    private sealed class ScriptedProbe : IPlayerPositionProbe
    {
        private readonly long[] _positions;
        private readonly bool _alwaysFail;
        private int _index;

        public ScriptedProbe(long[] positions, bool alwaysFail = false)
        {
            _positions = positions;
            _alwaysFail = alwaysFail;
        }

        public long? TryGetPositionSeconds(int playerProcessId)
        {
            if (_alwaysFail || _positions.Length == 0)
            {
                return null;
            }

            var index = Math.Min(_index, _positions.Length - 1);
            _index++;
            return _positions[index];
        }
    }

    /// <summary>最小 IHttpClientFactory: 插件用它回调 Jellyfin API。
    /// 不传 baseAddress 时保持相对地址(相对地址在真实运行里由 Jellyfin 宿主的 HttpClient 解析,
    /// 在测试里则会失败, 正好用来验证"校验不通过必须拒绝"); 传了就指向该地址(桩服务器)。</summary>
    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        private readonly string? _baseAddress;

        public SimpleHttpClientFactory(string? baseAddress = null)
        {
            _baseAddress = baseAddress;
        }

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrEmpty(_baseAddress))
            {
                client.BaseAddress = new Uri(_baseAddress);
            }

            return client;
        }
    }

    /// <summary>轮询等待文件出现(记录器是另一个进程, 写完需要一点时间)。</summary>
    private static async Task WaitForFile(string path, int attempts = 20)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(150);
        }
    }

    /// <summary>轮询等待文件内容里出现指定文本(比"等文件存在"可靠: 文件可能还是上一次的内容)。</summary>
    private static async Task<bool> WaitForFileContent(string path, string expected, int attempts = 30)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (File.Exists(path) && (await File.ReadAllTextAsync(path)).Contains(expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // 记录器正在写, 下一轮再读
            }

            await Task.Delay(150);
        }

        return false;
    }

    private static string Body(string path, string apiKey, string userId) =>
        $$"""
        {"path":{{Json(path)}},"apiKey":{{Json(apiKey)}},"userId":{{Json(userId)}},"itemId":"abc","startSec":0,"totalSec":745}
        """;

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static async Task<(int Status, string Body)> Post(HttpClient client, string? token, string json)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/play")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null)
        {
            req.Headers.TryAddWithoutValidation("X-PotPlayer-Token", token);
        }

        using var resp = await client.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }

    private static void Check(bool ok, string what)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  PASS  {what}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  FAIL  {what}");
        }
    }

    private static void Skip(string what)
    {
        _skipped++;
        Console.WriteLine($"  SKIP  {what}");
    }

    private static int Report()
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"{_passed} passed, {_failed} failed, {_skipped} skipped");
        Console.WriteLine(_failed == 0 ? "OK" : "FAILED");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>最小可用的 IApplicationPaths 替身。</summary>
    private sealed class FakePaths : IApplicationPaths
    {
        public FakePaths(string root)
        {
            ProgramDataPath = root;
            PluginsPath = Path.Combine(root, "plugins");
            PluginConfigurationsPath = Path.Combine(PluginsPath, "configurations");
            ConfigurationDirectoryPath = Path.Combine(root, "config");
            WebPath = Path.Combine(root, "web");
            foreach (var dir in new[] { PluginsPath, PluginConfigurationsPath, ConfigurationDirectoryPath, WebPath })
            {
                Directory.CreateDirectory(dir);
            }
        }

        public string ProgramDataPath { get; }

        public string VirtualDataPath => ProgramDataPath;

        public string ProgramSystemPath => ProgramDataPath;

        public string DataPath => ProgramDataPath;

        public string ImageCachePath => ProgramDataPath;

        public string PluginsPath { get; }

        public string PluginConfigurationsPath { get; }

        public string LogDirectoryPath => ProgramDataPath;

        public string ConfigurationDirectoryPath { get; }

        public string SystemConfigurationFilePath => Path.Combine(ConfigurationDirectoryPath, "system.xml");

        public string CachePath => ProgramDataPath;

        public string TempDirectory => Path.GetTempPath();

        public string MetadataPath => ProgramDataPath;

        public string BackupPath => ProgramDataPath;

        public string ItemsByNamePath => ProgramDataPath;

        public string InternalMetadataPath => ProgramDataPath;

        public string TrickplayPath => ProgramDataPath;

        public string FFmpegPath => ProgramDataPath;

        public string BrandingPath => ProgramDataPath;

        public string TranscodePath => ProgramDataPath;

        public string SubtitleFontsPath => ProgramDataPath;

        public string UserConfigurationFilePath => Path.Combine(ConfigurationDirectoryPath, "users");

        public string RootFolderPath => ProgramDataPath;

        public string WebPath { get; }

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }

        public void ReplacedByBackup(string path)
        {
        }

        public void DeleteDirectoryIfEmpty(string path)
        {
        }

        public void AddParts(IEnumerable<IConfigurationFactory> configurationFactories)
        {
        }
    }
}
