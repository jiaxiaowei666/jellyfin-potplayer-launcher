# Jellyfin Local PotPlayer Button (userscript)

在 Jellyfin 网页详情页注入一个 **▶ PotPlayer** 按钮：本地播放，并且**把播放进度回传给 Jellyfin**（续播、播放次数、继续观看都正常）。

> 这是 [jellyfin-potplayer-launcher](../README.md) 的浏览器端部分，需要配合 `plugin/` 里的 Jellyfin 插件一起使用。

## 安装

安装 [Tampermonkey](https://www.tampermonkey.net/)（Chrome 需要打开"开发者模式"），然后打开：

```
https://raw.githubusercontent.com/jiaxiaowei666/jellyfin-potplayer-launcher/main/userscript/jellyfin-potplayer-button.user.js
```

Tampermonkey 会弹出安装页。也可以新建脚本后把文件内容整段粘进去。

## 使用

1. 用 `http://localhost:8096` 或 `http://127.0.0.1:8096` 打开 Jellyfin 并登录。
2. 进入任意影片/剧集详情页，官方"播放"按钮旁会出现 **▶ PotPlayer**。
3. 点击 → 本机 PotPlayer 打开该文件；有续播位置时自动跳到该位置。
4. 关闭播放器 → 进度自动写回 Jellyfin。

## 配置

脚本顶部只有三处需要关心的配置：

```js
// 监听器地址（插件用 POTPLAYER_LAUNCHER_PORT 改端口时这里要同步）
var LISTENER = "http://127.0.0.1:13579";

// 正常情况下留空：token / userId 运行时从页面 ApiClient 会话里自动获取。
// 兜底只能填"浏览器会话 token"，不要填控制台生成的 API 密钥 ——
// API 密钥没有用户上下文，播放能启动，但进度不会写进用户数据。
var API_KEY_FALLBACK = "";
var USER_ID_FALLBACK = "";

// 回传进度时是否同时更新"最后播放时间"
var UPDATE_LAST_PLAYED_DATE = true;
```

工作流程（为什么不需要填密钥）：

1. 从页面 `ApiClient.accessToken()` / `getCurrentUserId()` 取当前登录会话；
2. `GET /token` 从插件换来本次启动的临时 token（Jellyfin 重启会变，脚本会自动重新握手）；
3. 取条目信息（`Path` / `MediaSources` / `RunTimeTicks` / `UserData.PlaybackPositionTicks`）；
4. `POST /play` 把路径、位置和会话 token 一起交给插件，由插件启动播放器并回传进度。

## 兼容性

- 只匹配 `http://localhost:8096/*` 和 `http://127.0.0.1:8096/*`。端口不是 8096、或用局域网 IP 访问时，需要自行修改 `@match`（局域网 IP 还会被插件的 Origin 白名单拒绝）。
- 允许非 8096 端口的本机来源，但脚本的 `@match` 得你自己加上。
- 插件是旧版本（没有 `/token` 接口）时，脚本会自动退回旧的 `GET /play?path=` 方式：能播放，但没有进度回传，会提示"旧版插件"。

## 测试

```powershell
cd userscript/tests
.\run-tests.ps1        # 9 项检查; 首次会自动把 jsdom 装进 node_modules(已 gitignore)
```

用真实 DOM（jsdom）加载本脚本，覆盖 Jellyfin 客户端路由下的按钮行为，重点是：

- **详情页 DOM 复用**：点"更多类似"切到另一个条目时按钮必须跟着换（这是曾经的 bug —— 旧按钮残留导致必须刷新页面才出现）
- 同一条目重绘不会产生重复按钮
- 容器整体重绘后由 MutationObserver 补回
- 离开详情页后按钮被移除

需要 Node.js 与 npm；CI 里也跑（`.github/workflows/ci.yml` 的 `userscript tests (jsdom)`）。

## 排错

打开浏览器控制台，脚本的日志都以 `[PotPlayer]` 开头：

| 现象 | 原因 |
|---|---|
| 没有按钮 | 不在详情页 / 脚本没启用 / 页面地址不匹配 `@match` |
| 提示"无法连接本地监听器" | 插件没加载或端口不同；直接访问 `http://127.0.0.1:13579/token` 验证 |
| `invalid token` | 缓存的临时 token 过期，刷新页面即可 |
| `invalid jellyfin token` | 页面会话失效，重新登录 |
| `userId mismatch` | 页面会话属于别的用户，或 `USER_ID_FALLBACK` 填错了 |
| 播放能开始但进度不更新 | 大概率传的是 API 密钥而不是会话 token，看插件日志是否有 `API key without a user context` 警告 |

## License

[MIT](../LICENSE)
