# 通信协议契约（userscript ↔ plugin）

本文件是油猴脚本与 Jellyfin 插件之间**唯一的接口事实来源**。

- 服务地址：`http://127.0.0.1:13579/`（端口可用环境变量 `POTPLAYER_LAUNCHER_PORT` 覆盖；改了端口脚本里的 `LISTENER` 必须同步）
- 只监听回环地址，局域网不可达
- 所有请求由**浏览器页面**发出；`Origin` 必须是 `http://localhost:<任意端口>` 或 `http://127.0.0.1:<任意端口>`，其他来源一律 `403` 且不下发 CORS 头
- 响应一律为 JSON，判断成功要看 **HTTP 状态码**，`error` 字段是给程序读的英文标识符（面向用户的文案由脚本自己组织）

**改动规则**：任何字段/状态码/语义变更，都要同时改本文件、`plugin/` 实现、`userscript/` 实现，并在提交信息里写明。协议版本见下文"版本协商"。

---

## 1. `GET /token` — 握手

脚本在每次 Jellyfin 启动后（或收到 `401` 后）调用，换取**本次启动有效**的临时凭证。

**请求**：无参数，无 body。

**响应 `200`**

```json
{ "ok": true, "token": "A46E5ACB568369C929DAC793EEF41B59299FC8658AA353CD", "version": 2 }
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `ok` | bool | 固定 `true` |
| `token` | string | 48 位十六进制。**每次 Jellyfin 启动随机重新生成**，不落盘为长期凭证（会写到 `plugins/PotPlayerLauncher/listener-token.txt` 仅供本机排查） |
| `version` | int | 协议版本，见"版本协商" |

**响应 `403`**：`Origin` 不在白名单内。脚本不会遇到（同源必然在白名单里），外来页面会。

---

## 2. `POST /play` — 播放

**请求头**

| 头 | 值 | 必需 |
|---|---|---|
| `Content-Type` | `application/json` | 是（会触发预检） |
| `X-PotPlayer-Token` | 第 1 步拿到的 `token` | 是 |

**请求体**

| 字段 | 类型 | 必需 | 说明 |
|---|---|---|---|
| `path` | string | 是 | 服务器视角的**磁盘绝对路径**。插件要求 `File.Exists` 通过，否则 `403` |
| `apiKey` | string | 是 | 当前登录会话的 Jellyfin token。**必须是会话 token**，不是控制台 API 密钥（见第 4 节） |
| `userId` | string | 是 | 该 token 所属用户的 id，必须能被 token "看到" |
| `itemId` | string | 否 | Jellyfin 条目 id。**为空则跳过全部进度回传** |
| `mediaSourceId` | string \| null | 否 | 多版本条目指定版本；单版本可省略 |
| `startSec` | int | 否 | 续播位置（秒）。`0` 或省略 = 从头播放 |
| `totalSec` | int | 否 | 媒体总时长（秒）。`0` 或省略 = 未知，插件不做上限夹取 |

```json
{
  "path": "D:/Movies/测试影片 (2024)/测试影片.mkv",
  "apiKey": "<session token>",
  "userId": "<user id>",
  "itemId": "<item id>",
  "mediaSourceId": "<media source id>",
  "startSec": 300,
  "totalSec": 745
}
```

**响应**

| 状态码 | body | 何时 |
|---|---|---|
| `200` | `{"ok":true,"pid":12345}` | 播放器已启动。`pid` 供排查用 |
| `400` | `{"ok":false,"error":"invalid json"}` | body 不是合法 JSON |
| `400` | `{"ok":false,"error":"apiKey/userId required"}` | 缺 `apiKey` 或 `userId` |
| `401` | `{"ok":false,"error":"invalid token"}` | `X-PotPlayer-Token` 缺失或不匹配本次启动的 token |
| `401` | `{"ok":false,"error":"invalid jellyfin token"}` | 拿 `apiKey` 去问 Jellyfin 失败（token 无效/过期/服务不可达） |
| `403` | `{"ok":false,"error":"origin not allowed"}` | `Origin` 不在白名单 |
| `403` | `{"ok":false,"error":"file not found"}` | `path` 为空或文件不存在 |
| `403` | `{"ok":false,"error":"player not found"}` | 播放器 exe 不存在（检查 `POTPLAYER_PATH` / 配置文件 / 默认值） |
| `403` | `{"ok":false,"error":"userId mismatch"}` | 该 token 看不到请求声明的 `userId` |
| `404` | `{"ok":false,"error":"not found"}` | 未知路径 |
| `500` | `{"ok":false,"error":"failed to start player"}` | `Process.Start` 抛异常 |
| `500` | `{"ok":false,"error":"internal error"}` | 未预期异常 |

**校验顺序**（排查时按这个顺序看日志）：`Origin` → 路径识别 → token → 文件存在 → 播放器存在 → `apiKey`/`userId` 非空 → Jellyfin token 有效 → `userId` 匹配 → 启动播放器。

**脚本侧约定**：收到 `404`/`405` 视为"插件是旧版本"，退回第 5 节的旧接口；收到 `401` 视为"临时 token 过期"，重新握手后**重试一次**。

---

## 3. 预检

浏览器发 `X-PotPlayer-Token` 会触发 CORS 预检，插件必须应答（脚本不需要手写）：

```
OPTIONS /play
Origin: http://127.0.0.1:8096
Access-Control-Request-Method: POST
Access-Control-Request-Headers: content-type,x-potplayer-token
```

```
HTTP/1.1 204 No Content
Access-Control-Allow-Origin: http://127.0.0.1:8096
Access-Control-Allow-Methods: GET, POST, OPTIONS
Access-Control-Allow-Headers: Content-Type, X-Emby-Token, X-PotPlayer-Token
Access-Control-Max-Age: 600
```

新增自定义请求头时，必须同步更新 `Access-Control-Allow-Headers`，否则浏览器会直接拦掉请求。

---

## 4. 鉴权模型（为什么必须用会话 token）

插件对 `apiKey` 做两步校验：

1. `GET /Users/Me`（带 `X-Emby-Token: <apiKey>`）——成功说明这是**带用户上下文的会话 token**；
2. 失败（`400`）则退回 `GET /Users`——管理员 API 密钥能通过这里，但没有用户上下文；
3. 校验请求声明的 `userId` 必须落在 token 可见的用户集合里，否则 `403 userId mismatch`。

**关键差异**：Jellyfin 只把播放进度写进**带用户上下文的 token** 对应的用户数据。

| `apiKey` 类型 | `/Users/Me` | 能核对 userId | 进度入库 |
|---|---|---|---|
| 浏览器会话 token（`ApiClient.accessToken()`） | `200` | ✅ | ✅ |
| 控制台生成的 API 密钥 | `400` | ✅（退回 `/Users`） | ❌ 插件只打 `WRN`，播放照常 |

所以：**脚本正常路径永远用会话 token；脚本里那两处 `*_FALLBACK` 不要填 API 密钥**。

---

## 5. 旧接口（兼容保留）

插件仍接受第一版协议，供旧脚本使用；**新脚本只在探测到 `/token` 返回 404/405 时才走这里**。

```
GET /play?path=<urlencoded absolute path>
Header: X-PotPlayer-Token: <token>
```

- 语义：只启动播放器，**不回传进度**，不做 Jellyfin token 校验
- 响应：`200 {"ok":true}` / `401 invalid token` / `403 file or player not found`
- 中文路径：插件从原始 query 手动 `Uri.UnescapeDataString` 解码（`HttpListener.QueryString` 会按 GBK 错解，不能用）

---

## 6. 进度回传（插件 → Jellyfin，脚本不参与）

脚本只管把 `itemId` / `mediaSourceId` / `startSec` / `totalSec` 交给插件，回传全部由插件完成，目标地址取自宿主的 `GetApiUrlForLocalAccess()`。

| 时机 | 请求 | 说明 |
|---|---|---|
| 播放器启动后 | `POST Sessions/Playing` | 建立会话，Jellyfin 上显示"正在播放" |
| 播放中每 `15s` | `POST Sessions/Playing/Progress` | 心跳，`EventName=timeupdate` |
| 播放器退出后 | `POST Sessions/Playing/Stopped` | 写最终位置，触发"继续观看"、播放次数、最后播放时间 |

body 字段：`ItemId`、`MediaSourceId`、`PlaySessionId`（插件为每次播放生成）、`PositionTicks`、`CanSeek`、`IsPaused`、`IsMuted`、`PlayMethod=DirectStream`、`RepeatMode=RepeatNone`；心跳额外带 `EventName=timeupdate`。

**位置取值规则**（改这里要特别小心，它决定条目会不会被判成"已看完"）：

```
pos = startSec + (播放器退出时刻 - 启动时刻).TotalSeconds
pos = max(0, pos)
if (totalSec > 0) pos = min(pos, totalSec - 10)
if (pos 对应的播放时长 < 20s) 不发 Stopped
```

- 暂停期间也在计时 → 暂停多时回传位置偏高（已知取舍）
- `位置 = 总时长 - 10` 是刻意留的安全边界，避免触发 Jellyfin 的"已看完"判定
- 播放不足 20 秒不回传，避免误点污染观看记录

---

## 7. 续播

脚本提供 `startSec = floor(UserData.PlaybackPositionTicks / 10^7)`；插件的判定：

```
canResume = startSec > 30 && (totalSec <= 0 || startSec < totalSec - 30)
```

成立时给播放器加 `/seek=HH:MM:SS`（`ProcessStartInfo.ArgumentList`，不做字符串拼接）。启动日志会打印实际参数，可用来区分续播来源：

```
PotPlayerLauncher launched PotPlayer for "..." (pid=5044 startSec=300 seek="/seek=00:05:00")
```

PotPlayer 自身的播放记忆与 Jellyfin 的续播位置互相独立，命令行参数只可能来自本插件。

---

## 8. 版本协商

| `version` | 含义 |
|---|---|
| 缺失 / `1` | 旧协议：只有 `GET /play?path=`，无进度回传 |
| `2` | 当前协议：`/token` + `POST /play` + 进度回传 |

约定：

- **加可选字段**：保持 `version` 不变，旧实现忽略未知字段即可
- **改字段语义 / 删字段 / 改状态码**：必须递增 `version`，脚本按自己支持的版本降级
- 脚本目前只用 `version` 区分"新旧监听器"，不做细粒度能力发现；将来加能力可扩展成 `capabilities: []` 数组
