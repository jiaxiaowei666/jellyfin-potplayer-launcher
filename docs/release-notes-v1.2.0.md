## 本版变化 / What's new

**精确进度**：不再靠"播放器进程活了多久"估算，改为每 2 秒向 PotPlayer 窗口索取真实播放位置。

Exact progress: the plugin now reads the real playback position from PotPlayer's window every 2 seconds instead of estimating it from the player process lifetime.

### 改进

- **暂停不再让进度偏高**：此前用"起播位置 + 进程存活时长"估算，暂停期间也在计时；现在用播放器报告的实时位置
- **强杀/崩溃也能回传到最后一次读数**（最多滞后 2 秒），而不是丢弃整段进度
- 探针读不到时（换成别的播放器、权限不足、窗口类名变化）自动退回估算，日志会说明用的是哪条路径
- 实测：关闭播放器前窗口读到 343.5 秒，插件回传 343 秒，Jellyfin 记录 `Stopped at "343000" ms`

### 修复

- 测试用的假播放器之前会瞬间退出，导致"播放中"心跳这条路径实际上从未被覆盖；现在会存活一段时间，心跳与探针路径都被真实跑到
- 测试不再占用/打扰本机 8096 上的真实 Jellyfin（改用自建桩服务器 + 自由端口）

### 兼容性

- **协议未变**（仍是 `version: 2`），字段与状态码没有任何变化
- 油猴脚本无需更新
- 插件版本 `1.2.0.0`

## 安装 / Install

1. 把附件的两个文件放进 Jellyfin 插件目录（`<datadir>` 默认 `C:\ProgramData\Jellyfin\Server`）：

   ```
   <datadir>\plugins\PotPlayerLauncher\Jellyfin.Plugin.PotPlayerLauncher.dll
   <datadir>\plugins\PotPlayerLauncher\meta.json
   ```

2. 重启 Jellyfin（手动启动时务必带 `--datadir`）：

   ```powershell
   Stop-Process -Name jellyfin -Force
   Start-Process "C:\Program Files\Jellyfin\Server\jellyfin.exe" -ArgumentList '--datadir "C:\ProgramData\Jellyfin\Server"'
   ```

3. 验证：日志出现 `PotPlayerLauncher listening on http://127.0.0.1:13579/`，浏览器打开 <http://127.0.0.1:13579/token> 返回 `{"ok":true,...}`

浏览器端脚本（若还没装）：见
[userscript](https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher/blob/main/userscript/jellyfin-potplayer-button.user.js)，
在 Tampermonkey 里安装后刷新 Jellyfin 页面。

## 已知限制

仅 Windows；Jellyfin 与播放器必须同机；仅支持 PotPlayer；播放不足 20 秒不回传；进度位置被夹在"总时长 − 10 秒"以内以避免误判为已看完。

## 校验和 / Checksums

见附件 `SHA256SUMS.txt`（由 CI 在 tag 上构建时生成，与附件 DLL 同源）：

```powershell
Get-FileHash .\Jellyfin.Plugin.PotPlayerLauncher.dll -Algorithm SHA256
```
