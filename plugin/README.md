# PotPlayer Launcher —— 插件实现说明

这是 [jellyfin-potplayer-launcher](../README.md) 的 **Jellyfin 服务端插件**部分：在 Jellyfin 进程内监听
`http://127.0.0.1:13579/`，收到播放请求后直接调用本机 PotPlayer 以本地方式播放，并在播放器退出后把进度回传给 Jellyfin。
生命周期与 Jellyfin 服务器完全同步。

> 安装步骤、环境要求、常见问题请看仓库根目录的 [README](../README.md)。本文只记录实现细节和开发中踩过的坑。

- 目标框架：`net9.0`
- 适用 Jellyfin：10.11.x（开发/验证于 Windows + 10.11.11）

## 源码结构

| 文件 | 职责 |
|---|---|
| `PotPlayerLauncher.csproj` | 类库定义；引用 Jellyfin 服务端官方 DLL（`Private=false`，不复制到输出），`JellyfinDir`/`EnableJellyfinDlls` 可覆盖 |
| `PotPlayerPlugin.cs` | 插件注册类，继承 `BasePlugin`；必须手动调用 `SetAttributes()` + `SetId()`（见"开发要点"） |
| `PluginServiceRegistrator.cs` | 实现 `IPluginServiceRegistrator`（要求无参构造），注册 `AddHttpClient`（回调 Jellyfin API 用）与监听器托管服务 |
| `PotPlayerListener.cs` | 核心逻辑：`HttpListener` 监听 13579、CORS/token 校验、启动播放器、跟踪进程并回传进度 |
| `deploy/install.ps1` | 构建 + 安装到 Jellyfin 插件目录 |
| `deploy/meta.json` | 手工安装所需的插件元数据模板（`guid` 必须与代码里的 `PluginGuid` 一致） |

## 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/token` | 握手。仅当 `Origin` 是 `http://localhost:<任意端口>` / `http://127.0.0.1:<任意端口>` 时返回 `{"ok":true,"token":"...","version":2}`。token 每次服务器启动随机生成 |
| POST | `/play` | 播放。Body：`path`(必填)、`apiKey`(必填)、`userId`(必填)、`itemId`、`mediaSourceId`、`startSec`、`totalSec`。Header：`X-PotPlayer-Token` |
| GET | `/play?path=` | 旧接口（兼容用，无进度回传），同样要求 `X-PotPlayer-Token` |

返回码：`200` 成功 / `400` 参数不全或 JSON 非法 / `401` 临时 token 或 Jellyfin token 无效 /
`403` Origin 不允许、文件不存在、播放器不存在、userId 与 token 不符 / `404` 未知路径 / `500` 启动播放器失败。

## 进度回传

插件读不到 PotPlayer 内部进度，因此用**播放器进程存活时长 + 起播位置**作为回传位置：

- 启动后立即 `POST Sessions/Playing`
- 播放中每 15 秒 `POST Sessions/Playing/Progress`（Jellyfin 上显示"正在播放"）
- 播放器退出后 `POST Sessions/Playing/Stopped`，`PositionTicks = (起播秒数 + 进程存活秒数) × 10^7`
- 播放不足 20 秒视为误点，不回传
- 位置夹在 `总时长 - 10 秒` 以内，避免被 Jellyfin 判为"已看完"

暂停期间也会计时，所以暂停多时进度会偏高。这是该方案的取舍；要精确进度必须能查询播放器内部状态（例如 mpv 的 IPC）。

### ⚠ 必须用"会话 token"，不要用 API 密钥

Jellyfin 只会把进度写进**带用户上下文的 token** 对应的用户数据。

| 传入 `/play` 的 `apiKey` | `/Users/Me` | 能核对 userId | 进度入库 |
|---|---|---|---|
| 浏览器会话 token（脚本自动取 `ApiClient.accessToken()`） | 200，返回当前用户 | 能 | ✅ |
| 控制台生成的 API 密钥 | 400（无用户上下文） | 能（退回 `/Users`，身份是管理员） | ❌ |

实测：会话 token 走完整流程后条目变成 `pos=40s, Played=False, PlayCount+1, LastPlayedDate=现在`；
API 密钥时 Jellyfin 同样返回 204、日志也有 `Playback stopped ... Stopped at "54000" ms`，但数据库里
`PlaybackPositionTicks` 仍是 0。所以插件检测到 API 密钥时会打 `WRN` 明确提示。

## 续播

脚本带上条目的 `UserData.PlaybackPositionTicks`，插件在"位置 > 30 秒且未接近片尾"时给 PotPlayer 加
`/seek=HH:MM:SS`。启动日志会记录实际参数，便于确认跳转来源：

```
PotPlayerLauncher launched PotPlayer for "..." (pid=5044 startSec=300 seek="/seek=00:05:00")
```

## 开发要点（Jellyfin 10.11 的坑）

1. **`IServerEntryPoint` 已被移除**。启动逻辑改用：
   - `IPluginServiceRegistrator.RegisterServices()` 中 `serviceCollection.AddHostedService<T>()`
   - 监听器实现 `IHostedService`（`StartAsync` 绑定端口，`StopAsync` 关闭）
2. **非泛型 `BasePlugin` 不会自动初始化属性**。直接继承它时 `Version`/`AssemblyFilePath` 为 null，
   服务器创建实例时会在 `instance.Version.ToString()` 抛 NRE 并把插件标记为 Malfunctioned。
   解决：构造函数中手动 `SetAttributes(path, dataFolder, version)` + `SetId(guid)`。
3. **插件目录必须有 `meta.json`**（不是 manifest.json），否则服务器按文件夹名自动生成记录，失败后写入
   `status: Malfunctioned`，之后即使修好代码也会一直被禁用。手工安装要自己写一份，`guid` 必须与代码一致。
4. **`/Users/Me` 不接受 API 密钥**（返回 400），校验 token 要先试 `/Users/Me`、失败退回 `/Users`。
5. **HttpListener 的 `QueryString` 会按 GBK 错解中文路径**，需要从原始 `Url.Query` 手动 `Uri.UnescapeDataString`。
6. 引用服务器自带 DLL 而非 NuGet 包，可保证与已安装版本的 API 完全一致；回传进度用
   `IHttpClientFactory`，需要在 `RegisterServices` 里 `AddHttpClient`。
7. **启动参数用 `ProcessStartInfo.ArgumentList` 而不是拼接字符串**，避免路径里的空格和引号被误解析。

## 构建

```powershell
cd plugin
dotnet build -c Release
# 产物: bin\Release\net9.0\Jellyfin.Plugin.PotPlayerLauncher.dll

# Jellyfin 装在别处:
dotnet build -c Release -p:JellyfinDir="D:\Jellyfin\Server"
# 不依赖本地安装(走 NuGet, 版本需与服务器一致):
dotnet build -c Release -p:EnableJellyfinDlls=true
```

需要 .NET SDK 9 或更高（不需要 Visual Studio）。

## 配置

| 配置项 | 位置 | 说明 |
|---|---|---|
| 播放器路径 | `config\PotPlayerLauncher.json` 的 `PotPlayerPath`，或环境变量 `POTPLAYER_PATH` | 环境变量优先；都无效时用内置默认值，修改后重启生效 |
| 监听端口 | 环境变量 `POTPLAYER_LAUNCHER_PORT` | 默认 13579；改了端口油猴脚本的 `LISTENER` 要同步 |
| 临时 token | `plugins\PotPlayerLauncher\listener-token.txt` | 只读参考，每次重启都会变 |

## 已验证行为

本机 Windows + Jellyfin 10.11.11 实测：

| 场景 | 结果 |
|---|---|
| `POST /play` 无 token / 错 token | 401 `invalid token` |
| `GET /token` 本机 Origin / 外来 Origin | 200 / 403（后者不下发 CORS 头） |
| `OPTIONS /play` 预检 | 204，允许 `X-PotPlayer-Token` |
| 真 token + 文件不存在 / Jellyfin token 无效 / userId 不符 | 403 / 401 / 403，错误信息区分明确 |
| 会话 token + 真实影片 | 200 `{"ok":true,"pid":N}`，PotPlayer 打开该文件（中文路径正常） |
| 播放 40 秒后关闭播放器 | `reported progress ... position=40s (HTTP 204)`，条目 `pos=40s, Played=False, PlayCount+1` |
| 播放 3 秒后关闭播放器 | `playback too short to report (3s < 20s)`，不回传 |
| 传 API 密钥 | 能播，但日志警告进度不会入库（实测位置确实未变） |
| 续播参数 | 日志出现 `startSec=300 seek="/seek=00:05:00"` |

## License

[MIT](../LICENSE)
