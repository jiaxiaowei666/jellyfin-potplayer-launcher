# jellyfin-potplayer-launcher

[![build-and-test](https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher/actions/workflows/ci.yml/badge.svg)](https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows-blue)
![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11.x-00A4DC)

在 Jellyfin 网页上点一下，就用**本机 PotPlayer** 播放（不转码、不走网络流），并且**把播放进度回传给 Jellyfin**——续播、播放次数、继续观看都能正常用。

> English: play Jellyfin media in your **local PotPlayer** (no transcoding), with **resume from the server-side position** and **playback progress reported back to Jellyfin**. See [English summary](#english-summary) / [full English README](README.en.md).

---

## 特性

- ▶ **详情页一键调用本地 PotPlayer**：直接用磁盘路径播放，画质无损、不吃服务器 CPU
- ⏯ **续播**：从 Jellyfin 记录的位置继续（插件给 PotPlayer 传 `/seek=HH:MM:SS`）
- 📈 **进度回传**：播放中每 15 秒上报一次"正在播放"，关闭播放器后回传最终位置 → Jellyfin 的"继续观看 / 播放次数 / 已观看"都正常
- 🔐 **不硬编码密钥**：油猴脚本从页面 `ApiClient` 会话里取当前登录用户的 token，每个用户都以**自己的身份**记录
- 🛡 **仅本机可达**：监听器只绑 `127.0.0.1`，带 Origin 白名单 + 每次启动随机 token 校验
- ♻️ **随服务器启停**：作为 Jellyfin 插件（`IHostedService`）运行，不用额外挂一个常驻进程

## 工作原理

```
Jellyfin Web (油猴脚本注入的 ▶ PotPlayer 按钮)
   │ ① 从页面 ApiClient 取当前登录用户的 token / userId
   │ ② 请求 /Items/{id}?fields=Path,MediaSources,RunTimeTicks,UserData 拿到磁盘路径和续播位置
   ▼
GET  http://127.0.0.1:13579/token      ← 握手, 取本次启动的临时 token
POST http://127.0.0.1:13579/play       ← {path, apiKey, userId, itemId, startSec, totalSec}
   ▼
插件 (在 Jellyfin 进程内, HttpListener 监听 13579)
   │ ③ 校验 Origin / 临时 token / 用 apiKey 问 Jellyfin 核对 userId / 文件与播放器是否存在
   ▼
Process.Start("PotPlayerMini64.exe" "<文件路径>" "/seek=00:05:00")
   │ ④ 播放中每 15s POST Sessions/Playing/Progress
   ▼
播放器退出 → POST Sessions/Playing/Stopped (PositionTicks=实际播放秒数)
```

## 环境要求

| 项目 | 要求 |
|---|---|
| 系统 | **Windows**（插件用 `HttpListener` + 直接拉起 PotPlayer 窗口，播放器与 Jellyfin 必须同一台机器） |
| Jellyfin | **10.11.x**（开发/验证于 10.11.11；10.10 及更早未适配，`IServerEntryPoint` 等 API 已变化） |
| 构建 | .NET SDK 9 或更高（目标框架 `net9.0`）+ Jellyfin 服务端的官方程序集 |
| 浏览器 | 支持 Tampermonkey / Violentmonkey；用 `http://localhost:8096` 或 `http://127.0.0.1:8096` 打开 Jellyfin 网页 |
| 播放器 | PotPlayer（`PotPlayerMini64.exe`） |

浏览器插件：[Tampermonkey](https://www.tampermonkey.net/)（Chrome 需要打开"开发者模式"）。

## 安装

> 本仓库是**单仓库双子目录**：`plugin/` 是 Jellyfin 插件（服务端），`userscript/` 是网页按钮脚本（浏览器端）。两边要一起装。

### 第一步：安装油猴脚本（浏览器端）

1. 装好 Tampermonkey（Chrome 需在 `chrome://extensions` 打开开发者模式）。
2. 打开下面这个链接安装脚本（把 `jiaxiaowei666` 换成你的 GitHub 用户名）：

   ```
   https://raw.githubusercontent.com/jiaxiaowei666/jellyfin-potplayer-launcher/main/userscript/jellyfin-potplayer-button.user.js
   ```

   Tampermonkey 会自动弹出安装页；也可以新建脚本后把文件内容整段粘进去。
3. 刷新 Jellyfin 页面。**不需要**在脚本里填 API 密钥——登录状态就够了。

### 第二步：安装 Jellyfin 插件（服务端）

**方式 A：一键脚本（推荐）**

```powershell
git clone https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher.git
cd jellyfin-potplayer-launcher\plugin\deploy
powershell -ExecutionPolicy Bypass -File .\install.ps1
# Jellyfin 数据目录不在默认位置时:
powershell -ExecutionPolicy Bypass -File .\install.ps1 -DataDir "D:\JellyfinData"
```

**方式 B：手动构建**

```powershell
cd plugin
dotnet build -c Release
Copy-Item .\bin\Release\net9.0\Jellyfin.Plugin.PotPlayerLauncher.dll `
  "C:\ProgramData\Jellyfin\Server\plugins\PotPlayerLauncher\" -Force
# meta.json 必须存在(见 plugin/deploy/meta.json), 用无 BOM 的 UTF-8 保存
```

Jellyfin 装在别处时指定服务端目录（构建要用它的 DLL）：

```powershell
dotnet build -c Release -p:JellyfinDir="D:\Jellyfin\Server"
# 或者完全不依赖本地安装(走 NuGet, 需联网):
dotnet build -c Release -p:EnableJellyfinDlls=true
```

**重启 Jellyfin。** 手动启动时**务必带数据目录参数**，否则会用一个全新的空数据目录：

```powershell
Stop-Process -Name jellyfin -Force
Start-Process "C:\Program Files\Jellyfin\Server\jellyfin.exe" -ArgumentList '--datadir "C:\ProgramData\Jellyfin\Server"'
```

**验证**：日志出现 `PotPlayerLauncher listening on http://127.0.0.1:13579/`；浏览器打开 <http://127.0.0.1:13579/token> 应返回 `{"ok":true,"token":"...","version":2}`。

## 使用

1. 在 Jellyfin 网页打开任意影片/剧集**详情页**。
2. 官方"播放"按钮旁会出现 **▶ PotPlayer**，点它。
3. PotPlayer 直接以本地文件方式打开；有续播位置时会自动跳到该位置。
4. 看完关闭播放器 → 进度自动写回 Jellyfin，网页上能看到"继续观看"。

## 配置

| 配置项 | 位置 | 默认 | 说明 |
|---|---|---|---|
| 播放器路径 | `config\PotPlayerLauncher.json` 的 `PotPlayerPath`，或环境变量 `POTPLAYER_PATH` | `C:\Program Files (x86)\app\PotPlayer\PotPlayerMini64.exe` | 环境变量优先；都无效时用默认值 |
| 监听端口 | 环境变量 `POTPLAYER_LAUNCHER_PORT` | `13579` | 改了端口，油猴脚本里的 `LISTENER` 要同步改 |
| 本次临时 token | `plugins\PotPlayerLauncher\listener-token.txt` | 每次启动随机 | 仅供排查/手工调用，Jellyfin 重启即变 |
| 探测到旧协议时的回退 | 油猴脚本自动处理 | — | 插件没有 `/token` 时自动退回旧接口（无进度回传） |

```json
// C:\ProgramData\Jellyfin\Server\config\PotPlayerLauncher.json
{
  "PotPlayerPath": "C:\\Program Files\\DAUM\\PotPlayer\\PotPlayerMini64.exe"
}
```

## 怎么确认"续播"是 Jellyfin 造成的

PotPlayer 自己也会记住每个文件的播放位置，两者**互相独立**：

| | PotPlayer 自己的记忆 | Jellyfin 的续播位置 |
|---|---|---|
| 存在哪 | PotPlayer 目录下的播放记录，按**文件路径** | Jellyfin 数据库 `UserData.PlaybackPositionTicks`，按**条目+用户** |
| 谁让它跳转 | PotPlayer 启动后读自己的记忆 | 插件启动时传命令行参数 `/seek=HH:MM:SS` |
| 网页上看得到吗 | 看不到 | 详情页的"继续观看 / 已观看 N 分钟"就是它 |

判据（插件日志里一眼可见）：

```
PotPlayerLauncher launched PotPlayer for "..." (pid=5044  startSec=300 seek="/seek=00:05:00")
PotPlayerLauncher launched PotPlayer for "..." (pid=19816 startSec=0   seek="<none>")
```

`startSec` 就是 Jellyfin 记录的位置。**PotPlayer 自己的记忆永远不会变成命令行参数**，所以看到 `seek="/seek=..."` 就说明这次跳转是 Jellyfin 数据驱动的。

## 故障排查

| 现象 | 排查方向 |
|---|---|
| 详情页没有按钮 | 脚本没启用 / 网页不是 `http://localhost:8096` 或 `http://127.0.0.1:8096`（脚本 `@match` 只覆盖这两个）；看浏览器控制台有无 `[PotPlayer]` 日志 |
| 提示"无法连接本地监听器" | 插件没加载或端口不同；浏览器直接访问 <http://127.0.0.1:13579/token> 应返回 JSON |
| `{"ok":false,"error":"invalid token"}` | 缓存里的临时 token 过期（Jellyfin 重启过），刷新页面即可 |
| `{"ok":false,"error":"invalid jellyfin token"}` | 页面会话失效，重新登录 |
| `{"ok":false,"error":"userId mismatch"}` | 页面会话属于别的用户，或脚本里填了错误的兜底 userId |
| 日志警告 `API key without a user context` | 用了控制台生成的 API 密钥：能播放，但**进度不会写入用户数据**。不要用它做兜底，刷新页面让脚本用会话 token |
| 播放完进度没回传 | 日志搜 `reported progress`；播放不足 20 秒不回传是设计行为 |
| 进度回传了但位置没变 | 同上那条 API 密钥警告；或看日志有没有 `Playback stopped reported by app ...` |
| 中文路径失败 | 确认用的是本仓库版本（从原始 QueryString 手动 `UnescapeDataString`，HttpListener 自带的 `QueryString` 会按 GBK 错解） |
| 浏览器 CORS 报错 | 监听器只放行 `localhost` / `127.0.0.1` 来源（任意端口）；用局域网 IP 打开 Jellyfin 网页会失败 |
| 端口 13579 未监听 | 日志搜 `failed to bind`，检查端口占用 |
| 启动后变成初始化向导 | 重启时忘了 `--datadir` 参数 |

## 已知限制

- **仅 Windows**：`HttpListener` + 拉起桌面播放器 + PotPlayer 路径都按 Windows 处理。
- **仅同机**：Jellyfin 服务器与播放器必须在同一台机器（插件直接打开本地磁盘路径）。
- **进度按"进程存活时长"估算**：插件读不到 PotPlayer 内部进度，用"起播位置 + 进程存活秒数"作为回传位置，**暂停期间也会计时**，所以暂停多的话进度会偏高。播放不足 20 秒不回传。
- **只支持 PotPlayer**：没有 mpv/VLC/MPC 的适配（需要读取播放器内部进度的话，可参考 mpv 的 IPC 方案）。
- **多用户**：任何登录用户都能用，各自以自己的身份记录；但没有管理员权限的用户拿不到文件路径时按钮会失败。
- **局域网访问**：用 `http://<局域网IP>:8096` 打开网页时，浏览器会因 Origin 不在白名单而拒绝请求。

## 安全说明

- 监听器只绑 `127.0.0.1`，局域网其他设备无法访问。
- Origin 白名单之外的请求直接 403，且不下发 CORS 头。
- `/play` 需要 (a) 本次启动的临时 token（仅同源本机页面能通过 `/token` 取到）**和** (b) 一个真实有效的 Jellyfin token，且该 token 必须能看到请求声明的 `userId`。
- 已知取舍：本机任何能提供 `localhost` 页面的程序都能拿到临时 token，但它同时还需要一个有效的 Jellyfin token 才能播放。
- 校验用 `/Users`（而不是 `/Users/Me`）：控制台生成的 API 密钥没有用户上下文，调 `/Users/Me` 会返回 400。

## 目录结构

```
.
├── docs/
│   └── PROTOCOL.md                  # 脚本 ↔ 插件 的协议契约(唯一事实来源)
├── plugin/                          # Jellyfin 服务端插件 (C# / net9.0)
│   ├── PotPlayerLauncher.csproj
│   ├── Directory.Build.props         # JellyfinDir / EnableJellyfinDlls 与程序集引用
│   ├── PotPlayerPlugin.cs           # 插件注册 (BasePlugin + SetAttributes/SetId)
│   ├── PluginServiceRegistrator.cs   # 注册托管服务与 HttpClient
│   ├── PotPlayerListener.cs          # 核心: 监听 13579 / 鉴权 / 启动播放器 / 回传进度
│   ├── tests/                        # 协议行为测试(41 项, 假播放器记录器, 无需测试框架)
│   ├── deploy/
│   │   ├── install.ps1               # 一键构建并安装
│   │   └── meta.json                 # 手工安装所需的插件元数据
│   └── README.md                     # 插件实现细节与测试说明
├── userscript/
│   ├── jellyfin-potplayer-button.user.js
│   └── README.md
├── LICENSE
└── README.md / README.en.md
```

## 测试

```powershell
cd plugin/tests
.\run-tests.ps1          # 60 项检查, 约 1 分钟, 不会弹出真实播放器
```

覆盖协议契约（握手、鉴权、Origin 白名单、CORS 预检、参数校验、旧接口兼容、中文路径、端口冲突降级、播放位置探针及其降级路径）。
测试用 `.cmd` 记录器替换播放器，因此能断言插件真正传给播放器的命令行参数（例如续播的 `/seek=00:05:00`）。

没装 Jellyfin 的机器（或 CI）加一个参数即可改用 NuGet 包构建：

```powershell
.\run-tests.ps1 -EnableJellyfinDlls
```

> **脚本引擎**：`.ps1` 在检测到 PowerShell 7 时会自动用 `pwsh` 重跑自己（7 能正确读取无 BOM 的 UTF-8，报错信息也清楚得多）；
> 没装 7 也能跑——脚本刻意只用 ASCII，所以在 Windows PowerShell 5.1 下不会乱码。两条路径都实测过。

## 持续集成

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) 在两个平台上跑：

| Job | 平台 | 内容 |
|---|---|---|
| `test` | windows-latest | 构建插件 + 跑完整测试套件（41 项），并上传插件 DLL 作为 artifact |
| `build-linux` | ubuntu-latest | 只验证编译，尽早发现宿主 API 变化 / 目标框架漂移（运行期依赖 Windows，所以不跑测试） |

两个 job 都用 `-p:EnableJellyfinDlls=true`，即从 NuGet 拉 Jellyfin 包，不依赖 runner 上装了 Jellyfin。
CI 能防住的典型问题：Jellyfin 升级后宿主 API 变了导致插件编译失败——会在你自己的仓库里先炸，而不是等用户报错。

## 从源码构建

```powershell
git clone https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher.git
cd jellyfin-potplayer-launcher\plugin
dotnet build -c Release
# 产物: bin\Release\net9.0\Jellyfin.Plugin.PotPlayerLauncher.dll
```

需要 .NET SDK 9+。构建依赖 Jellyfin 服务端程序集，默认从 `C:\Program Files\Jellyfin\Server\` 读取，可用 `-p:JellyfinDir=...` 覆盖，或 `-p:EnableJellyfinDlls=true` 改用 NuGet 包（版本须与目标服务器一致）。

## 贡献

欢迎 Issue / PR。提交前请确认：

- `cd plugin && dotnet build -c Release` 无警告无错误；
- `cd plugin/tests && .\run-tests.ps1` 输出 `OK`；
- 如果改了协议（`/token`、`/play` 的字段、状态码、错误标识），**先改 [`docs/PROTOCOL.md`](docs/PROTOCOL.md)**，再同步实现与测试；
- 面向用户的改动请在 [`CHANGELOG.md`](CHANGELOG.md) 的 `Unreleased` 下记一行。

版本号规则、三个版本号（协议 / 插件 / 脚本）的关系、以及完整发布流程见 [PROTOCOL.md 第 8 节](docs/PROTOCOL.md#8-版本协商)。

## License

[MIT](LICENSE)

---

## English summary

**What it is** — A Jellyfin plugin + Tampermonkey userscript pair that plays your media in the **local PotPlayer** on the same machine as the server (no transcoding), with **server-side resume** and **playback progress reported back to Jellyfin**.

**How it works** — The userscript injects a ▶ PotPlayer button on the item detail page. On click it reads the current session token from the page's `ApiClient`, fetches the item's `Path` / resume position, then calls the plugin's local endpoint (`POST http://127.0.0.1:13579/play`). The plugin validates the origin, a per-startup token and the Jellyfin token, launches `PotPlayerMini64.exe "<path>" /seek=HH:MM:SS`, and reports `Sessions/Playing/Progress` every 15 s plus a final `Sessions/Playing/Stopped` when the player exits.

**Requirements** — Windows; Jellyfin 10.11.x; .NET SDK 9+ to build; Tampermonkey; PotPlayer. The server and the player must be on the same machine.

**Install** — Install `userscript/jellyfin-potplayer-button.user.js` in Tampermonkey, then build and deploy the plugin with `plugin/deploy/install.ps1` (or manually drop the DLL plus `meta.json` into `<datadir>/plugins/PotPlayerLauncher/`) and restart Jellyfin **with** its `--datadir` argument.

**Limitations** — Windows only; same-machine only; progress is estimated from the player process lifetime (pausing still counts, so it can over-report) and is skipped for playback shorter than 20 s; PotPlayer only.

**Note** — Use a **browser session token**, not a console-generated API key: API keys have no user context, so Jellyfin will not persist progress for them (the plugin logs a warning if it sees one).

Full English documentation: [README.en.md](README.en.md)
