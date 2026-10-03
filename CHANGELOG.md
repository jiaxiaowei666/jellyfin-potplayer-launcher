# Changelog

本文件记录所有值得注意的变更。
格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

版本号规则、以及"插件版本 / 脚本版本 / 协议版本"三者的关系见
[`docs/PROTOCOL.md` 的"版本协商"一节](docs/PROTOCOL.md#8-版本协商)。

## [Unreleased]

### Fixed

- **油猴脚本**：从详情页点"更多类似"进入另一个条目时按钮不再消失（此前需要刷新页面才出现）。
  Jellyfin 的详情页复用同一套 DOM——切换条目时只把容器内容换掉，`.btnPlay` 等元素本身没变，
  于是上一次注入的按钮会留在原地。旧代码只判断"按钮是否存在"就跳过注入，结果复用了属于旧条目的按钮。
  现在按钮上记录 `dataset.itemId`，只有"存在 **且** 属于当前条目 **且** 仍在文档中"才跳过；
  另外补了 `popstate` 监听，定时兜底检查也校验条目是否匹配。脚本版本升到 `1.1.0`。

### Changed

- CI 用的 action 升级到当前 major：`checkout` v4→v7、`setup-dotnet` v4→v6、`setup-node` v4→v7、`upload-artifact` v4→v7。这消除了 GitHub 的 "Node.js 20 is deprecated" 警告（原先 `checkout@v4` / `setup-node@v4` 被强制跑在 Node 24 上）。三个 job 均已验证通过。
- `run-tests.ps1` / `install.ps1` 增加脚本引擎守卫：检测到 PowerShell 7 时自动用 `pwsh` 重跑自己（7 能正确读取无 BOM 的 UTF-8，诊断信息也更清楚）；未安装 7 时打印提示并继续在 Windows PowerShell 5.1 下运行。脚本保持纯 ASCII，因此两条路径都不会出现编码乱码。

## [1.2.0] - 2026-10-02

**精确进度**：不再只靠"进程存活时长"估算，改为向 PotPlayer 窗口索取实时播放位置。协议仍为 `version: 2`（字段与状态码未变）。

### Added

- **播放位置探针**：每 2 秒向 PotPlayer 主窗口（窗口类 `PotPlayer64`，`WM_USER(0x400)` + `0x5004`）索取当前时间码，作为进度上报与最终回传的依据
  - 暂停不再让位置增长（此前估算会偏高）
  - 播放器被强杀/崩溃也能回传到最后一次读数（最多滞后 2 秒）
  - 实测：关闭前窗口读到 343.5s，插件回传 343s，与 Jellyfin 记录一致
- `PlayerPositionPolicy`：探针读数的可信度判定（非空、非负、不超过"起播 + 存活时长 + 120s"），与 P/Invoke 分离以便单元测试
- `IPlayerPositionProbe`：探针接口，可注入假实现；探针不可用时自动退回估算
- 测试新增两组用例：策略纯逻辑断言，以及"桩 Jellyfin + 假探针"的端到端上报断言（探针可用与不可用两条路径）

### Changed

- 位置上报改为"最后一次可信的探针读数，否则退回 `startSec + 存活时长`"，日志会区分两者
- 测试全程使用自建桩 Jellyfin（自由端口），不再依赖 8096，避免打扰本机真实服务器

### Fixed

- 测试用假播放器之前会立刻退出，导致"播放中"(heartbeat) 路径根本没被覆盖；现在它会存活一段时间，心跳与探针路径都真实跑到了

## [1.1.0] - 2026-10-01

面向开发与可维护性的一版：**没有协议变更**（协议仍是 `version: 2`），补上了契约文档、自动化测试与 CI，并修掉两个行为问题。

### Added

- `docs/PROTOCOL.md`：脚本 ↔ 插件 的协议契约（端点、字段、状态码、错误标识、进度回传规则、版本协商），作为唯一事实来源
- `plugin/tests/`：41 项协议行为测试。真起 `HttpListener` 发真实 HTTP 请求，播放器被替换成 `.cmd` 记录器（记录自己的命令行后立即退出），因此可以断言插件实际传给播放器的参数，且不会弹出真实播放器
- CI（`.github/workflows/ci.yml`）：Windows 上构建 + 跑全部测试并上传插件 DLL；Linux 上只验证编译，用于尽早发现宿主 API 变化
- `run-tests.ps1 -EnableJellyfinDlls`：没装 Jellyfin 的机器/CI 可改用 NuGet 包构建
- `plugin/Directory.Build.props`：集中管理 Jellyfin 程序集引用、`JellyfinDir` / `EnableJellyfinDlls` 与 NuGet 包版本
- `plugin/deploy/install.ps1`：一键构建并安装（含 `meta.json` 的写入，且不会覆盖已存在的文件）

### Fixed

- **校验顺序**：`player not found` 原本排在"拿 apiKey 去问 Jellyfin"之后，配置有问题时要先浪费一次网络往返才报错。现在本地检查（文件 → 播放器）全部前置，只有本地检查都通过才发起网络请求
- **配置被静默忽略**：`ResolvePlayerPath()` 只在"配置的路径存在"时才返回它，否则悄悄退回内置默认值。结果是 `PotPlayerPath` / `POTPLAYER_PATH` 写错时会被默认值掩盖，报出的是"默认路径不存在"这种误导性错误。现在解析与存在性检查分离，日志会明确提示配置指向了不存在的文件

### Changed

- 版本号统一到 `1.0.0` 起步（此前代码里的 `X-Emby-Authorization` 客户端版本号与脚本 `@version` 不一致）
- README 拆成"中文为主 + English summary"与完整英文版，并补上环境要求、已知限制、安全说明

## [1.0.0] - 2026-10-01

首个公开版本。包含服务端插件与配套油猴脚本，两者一起构成完整功能。

### Added

- Jellyfin 10.11.x 服务端插件（`net9.0`）：在服务器进程内监听 `http://127.0.0.1:13579/`，收到请求后用本机 PotPlayer 以本地文件方式播放，生命周期随服务器启停
- 油猴脚本：在详情页注入 **▶ PotPlayer** 按钮，从页面 `ApiClient` 会话取当前用户的 token（无需硬编码 API Key），并使用该用户在 Jellyfin 中的登录身份
- **续播**：脚本提供条目的续播位置，插件在"位置 > 30 秒且未接近片尾"时给播放器加 `/seek=HH:MM:SS`
- **进度回传**：播放中每 15 秒上报一次 `Sessions/Playing/Progress`，播放器退出后回传 `Sessions/Playing/Stopped`（位置 = 起播位置 + 播放器进程存活时长；不足 20 秒不回传；位置夹在总时长 −10 秒以内，避免被误判为已看完）
- 安全模型：仅绑定回环地址、`Origin` 白名单（`localhost` / `127.0.0.1` 任意端口）、每次启动随机生成的临时 token、以及用 `/Users/Me`（必要时退回 `/Users`）校验传入的 Jellyfin token 并核对 userId
- 配置：`config/PotPlayerLauncher.json` 的 `PotPlayerPath`、环境变量 `POTPLAYER_PATH` / `POTPLAYER_LAUNCHER_PORT`
- 兼容旧协议：保留 `GET /play?path=` 接口（无进度回传），供旧版脚本使用
- MIT 许可证

[Unreleased]: https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher/releases/tag/v1.0.0
