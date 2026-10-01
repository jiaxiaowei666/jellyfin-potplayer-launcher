## 安装 / Install

需要 **Windows** + **Jellyfin 10.11.x** + 本机 PotPlayer。

1. **浏览器端**：装好 [Tampermonkey](https://www.tampermonkey.net/) 后打开下面的链接安装脚本

   ```
   https://raw.githubusercontent.com/jiaxiaowei666/jellyfin-potplayer-launcher/main/userscript/jellyfin-potplayer-button.user.js
   ```

2. **服务端**：把本 Release 的两个文件放进 Jellyfin 插件目录（`<datadir>` 默认是 `C:\ProgramData\Jellyfin\Server`）

   ```
   <datadir>\plugins\PotPlayerLauncher\Jellyfin.Plugin.PotPlayerLauncher.dll
   <datadir>\plugins\PotPlayerLauncher\meta.json
   ```

   `meta.json` 必须与 DLL 同目录，且用无 BOM 的 UTF-8 保存（否则 Jellyfin 反序列化失败）。

3. **重启 Jellyfin**，手动启动时务必带数据目录参数，否则会用一个全新的空数据目录：

   ```powershell
   Stop-Process -Name jellyfin -Force
   Start-Process "C:\Program Files\Jellyfin\Server\jellyfin.exe" -ArgumentList '--datadir "C:\ProgramData\Jellyfin\Server"'
   ```

4. **验证**：日志出现 `PotPlayerLauncher listening on http://127.0.0.1:13579/`；浏览器打开 <http://127.0.0.1:13579/token> 应返回 `{"ok":true,...}`。然后在任意影片详情页点 **▶ PotPlayer**。

完整说明见 [README](https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher#readme)。
插件装在非默认位置时，用环境变量 `POTPLAYER_PATH` 或 `config\PotPlayerLauncher.json` 指定播放器路径。

## 本版变化

面向开发与可维护性：**没有协议变更**（协议仍是 `version: 2`），补上了契约文档、自动化测试与 CI，并修掉两个行为问题。

### 新增

- `docs/PROTOCOL.md`：脚本 ↔ 插件 的协议契约（端点、字段、状态码、错误标识、进度回传规则、版本协商），作为唯一事实来源
- `plugin/tests/`：41 项协议行为测试。真起 `HttpListener` 发真实 HTTP 请求，播放器被替换成 `.cmd` 记录器，因此可以断言插件实际传给播放器的参数，且不会弹出真实播放器
- CI：Windows 上构建 + 跑全部测试并上传插件 DLL；Linux 上只验证编译，用于尽早发现宿主 API 变化
- `run-tests.ps1 -EnableJellyfinDlls`：没装 Jellyfin 的机器/CI 可改用 NuGet 包构建
- `install.ps1 -Package`：一条命令产出本 Release 的上传资产与 SHA256 校验和

### 修复

- **校验顺序**：`player not found` 原本排在"拿 apiKey 去问 Jellyfin"之后，配置有问题时要先浪费一次网络往返才报错。现在本地检查（文件 → 播放器）全部前置
- **配置被静默忽略**：`PotPlayerPath` / `POTPLAYER_PATH` 写错时，原本会悄悄退回内置默认值，报出误导性的"默认路径不存在"。现在解析与存在性检查分离，日志会明确指出配置指向了不存在的文件

### 变更

- 版本号统一到 `1.0.0` 起步（此前 `X-Emby-Authorization` 的客户端版本号与脚本 `@version` 不一致）
- README 拆成"中文为主 + English summary"与完整英文版，补上环境要求、已知限制、安全说明

## 校验和 / Checksums

见附件 `SHA256SUMS.txt`（由 CI 在 tag 上构建时生成，因此与附件 DLL 逐字节对应）：

```powershell
Get-FileHash .\Jellyfin.Plugin.PotPlayerLauncher.dll -Algorithm SHA256
# 与 SHA256SUMS.txt 里的值比对
```

> DLL 内含构建时的提交戳（`ProductVersion` 形如 `1.1.0+<commit>`），所以只有 CI 在 tag 上那次
> 构建才与附件完全一致。**不要**拿本机自行构建的 DLL 去比对附件校验和。

## 已知限制

仅 Windows；Jellyfin 与播放器必须同机；进度按播放器进程存活时长估算（暂停也会计时，可能偏高），播放不足 20 秒不上报；仅支持 PotPlayer。

## English summary

Windows-only Jellyfin 10.11.x plugin + Tampermonkey userscript that plays media in your **local PotPlayer** (no transcoding), resumes from the server-side position, and reports playback progress back to Jellyfin.

This release is about maintainability — **no protocol change** (still `version: 2`): a written protocol contract, 41 behaviour checks that run against a real `HttpListener` with the player replaced by a `.cmd` recorder, CI on Windows (build + tests) and Linux (compile only), and two fixes: local checks (file, player) now run before the Jellyfin round-trip, and a mistyped `PotPlayerPath` / `POTPLAYER_PATH` is no longer silently replaced by the built-in default.

**Install**: drop the attached `Jellyfin.Plugin.PotPlayerLauncher.dll` and `meta.json` into `<datadir>\plugins\PotPlayerLauncher\`, install the userscript from the repo, restart Jellyfin **with** `--datadir`.
