// ==UserScript==
// @name         Jellyfin Local PotPlayer Button
// @name:zh-CN   Jellyfin 本地 PotPlayer 按钮
// @namespace    https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher
// @version      1.1.0
// @description  Play in local PotPlayer from the Jellyfin web UI: resume from the server-side position and report playback progress back (no transcoding).
// @description:zh-CN 在 Jellyfin 详情页添加「PotPlayer」按钮：本地 PotPlayer 播放，支持续播定位，并把播放进度回传给 Jellyfin（无转码）
// @author       Xiaowei Jia
// @match        http://localhost:8096/*
// @match        http://127.0.0.1:8096/*
// @grant        unsafeWindow
// @license      MIT
// ==/UserScript==

(function () {
    // ============ 配置 ============
    // 监听器地址(默认 13579;插件用 POTPLAYER_LAUNCHER_PORT 改端口时这里要同步)
    var LISTENER = "http://127.0.0.1:13579";

    // 注入按钮的 id; 按钮上会记 dataset.itemId 以便识别它属于哪个条目
    var BUTTON_ID = "local-play-btn";

    // 生效的 token / UserId 运行时从页面 ApiClient 会话里取, 不再硬编码。
    // 注意: 这里兜底必须填"浏览器会话 token"(登录后设备列表里那一串), 不要填控制台生成的 API 密钥 ——
    // API 密钥没有用户上下文, 播放能启动, 但进度不会写进用户数据(插件日志会给出警告)。
    // 正常情况下留空即可。
    var API_KEY_FALLBACK = "";
    var USER_ID_FALLBACK = "";

    // 相对播放器退出后回传进度, 这里只控制"列表里的播放次数/最后播放时间"是否更新
    var UPDATE_LAST_PLAYED_DATE = true;

    // 由监听器查到的服务器版本号(1.x = 旧版监听器, 无进度回传)
    var listenerVersion = 0;
    var listenerChecked = false;

    // 每次 Jellyfin 重启后监听器会换 token, 这里缓存运行时拿到的那个
    var listenerToken = null;

    var observer = null;
    var debounceTimer = null;
    var lastHash = "";

    // ============ 小提示 ============
    function toast(msg, ms) {
        var el = document.createElement("div");
        el.textContent = msg;
        el.style.cssText =
            "position:fixed;left:50%;bottom:32px;transform:translateX(-50%);" +
            "z-index:99999;background:rgba(20,20,25,.92);color:#fff;" +
            "padding:10px 16px;border-radius:8px;font-size:13px;" +
            "box-shadow:0 4px 18px rgba(0,0,0,.45);pointer-events:none;";
        document.body.appendChild(el);
        setTimeout(function () { el.remove(); }, ms || 4000);
    }

    function warn(msg) { console.warn("[PotPlayer] " + msg); }

    // ============ 从页面 ApiClient 取会话信息(替代硬编码 API Key) ============
    function getApiClient() {
        var w = (typeof unsafeWindow !== "undefined" && unsafeWindow) ? unsafeWindow : window;
        return w.ApiClient || null;
    }

    function readAccessToken(api) {
        try {
            if (typeof api.accessToken === "function") {
                var t = api.accessToken();
                if (t) return t;
            }
        } catch (e) { /* 某些版本没有该方法 */ }
        var info = api._serverInfo || {};
        return info.AccessToken || info.accessToken || null;
    }

    function readUserId(api) {
        try {
            if (typeof api.getCurrentUserId === "function") {
                var id = api.getCurrentUserId();
                if (id) return id;
            }
        } catch (e) { /* ignore */ }
        var info = api._serverInfo || {};
        return info.UserId || (api._currentUser && api._currentUser.Id) || null;
    }

    function readAppName(api) {
        var name = null;
        try {
            if (typeof api.appName === "function") name = api.appName();
        } catch (e) { /* ignore */ }
        if (!name) {
            var info = api._serverInfo || {};
            name = info.Client || null;
        }
        if (!name) return "Jellyfin Web";
        // X-Emby-Authorization 里不能出现引号
        return String(name).replace(/"/g, "'");
    }

    function hasSession() {
        var api = getApiClient();
        return !!(api && readAccessToken(api));
    }

    function authQuery() {
        var api = getApiClient();
        var key = api ? readAccessToken(api) : null;
        var uid = api ? readUserId(api) : null;
        if (!key) key = API_KEY_FALLBACK;
        if (!uid) uid = USER_ID_FALLBACK;
        return { key: key, uid: uid, app: api ? readAppName(api) : "Jellyfin Web" };
    }

    // ============ Jellyfin API 请求头(用会话里的 token) ============
    function jellyfinHeaders(auth) {
        var parts = [
            'MediaBrowser Client="' + auth.app + '"',
            'Device="' + (navigator.platform || "Browser") + '"',
            'DeviceId="jellyfin-potplayer-button"',
            'Version="1.0.0"',
            'Token="' + auth.key + '"'
        ];
        var headers = { "X-Emby-Authorization": parts.join(", ") };
        headers["X-Emby-Token"] = auth.key;
        return headers;
    }

    // ============ 监听器 token 握手 ============
    async function fetchLocalToken(force) {
        if (listenerToken && !force) return listenerToken;
        var r = await fetch(LISTENER + "/token", { method: "GET" });
        if (!r.ok) throw new Error("token HTTP " + r.status);
        var data = await r.json();
        if (!data.ok || !data.token) throw new Error("listener refused handshake");
        listenerToken = data.token;
        return listenerToken;
    }

    // ============ 兼容旧版监听器: 直接探测是否支持 /token ============
    async function detectListener() {
        try {
            var r = await fetch(LISTENER + "/token", { method: "GET" });
            if (r.ok) {
                var data = await r.json();
                if (data.ok && data.token) {
                    listenerToken = data.token;
                    listenerVersion = Number(data.version || 1);
                }
            }
        } catch (e) {
            // 监听器未运行或旧版没有 /token, 由点击时再提示
        }
        listenerChecked = true;
    }

    // ============ 调本地监听器(带 token 重试一次) ============
    async function postToListener(auth, item, path, token) {
        async function once(tk) {
            return fetch(LISTENER + "/play", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "X-PotPlayer-Token": tk
                },
                body: JSON.stringify({
                    path: path,
                    apiKey: auth.key,
                    userId: auth.uid,
                    itemId: item.Id,
                    mediaSourceId: item.MediaSourceId || null,
                    startSec: item.StartSec || 0,
                    totalSec: item.TotalSec || 0
                })
            });
        }

        var tk = token || await fetchLocalToken();
        var resp = await once(tk);
        if (resp.status === 401) {
            // Jellyfin 重启后 token 变了, 重新握手再试一次
            tk = await fetchLocalToken(true);
            resp = await once(tk);
        }
        return resp;
    }

    // 旧版监听器(GET /play?path=), 无进度回传、无 token
    async function legacyPlay(path) {
        return fetch(LISTENER + "/play?path=" + encodeURIComponent(path));
    }

    // ============ 读取条目信息(路径 / 续播位置 / 时长) ============
    async function fetchItemInfo(itemId) {
        var auth = authQuery();
        if (!auth.key || !auth.uid) {
            throw new Error("拿不到当前登录会话的 API Key，请刷新页面重试");
        }

        var url = "/Items/" + itemId +
            "?fields=Path,MediaSources,RunTimeTicks,UserData" +
            "&api_key=" + encodeURIComponent(auth.key) +
            "&userId=" + encodeURIComponent(auth.uid);

        var r = await fetch(url, { headers: jellyfinHeaders(auth) });
        if (!r.ok) throw new Error("读取条目信息失败 HTTP " + r.status);

        var item = await r.json();
        var userData = item.UserData || {};
        var ticks = userData.PlaybackPositionTicks || 0;
        var runtimeTicks = item.RunTimeTicks || 0;
        var source = {};

        if (Array.isArray(item.MediaSources) && item.MediaSources.length) {
            for (var i = 0; i < item.MediaSources.length; i++) {
                if (item.MediaSources[i] && item.MediaSources[i].Path) {
                    source = item.MediaSources[i];
                    break;
                }
            }
            if (!source.Path && !item.Path) source = item.MediaSources[0] || {};
        }

        return {
            Id: itemId,
            Path: item.Path || source.Path || null,
            MediaSourceId: source.Id || null,
            StartSec: Math.floor(ticks / 10000000),
            TotalSec: Math.floor(runtimeTicks / 10000000)
        };
    }

    // ============ 兼容模式: 只把播放次数 +1 ============
    async function bumpPlayCount(auth, itemId) {
        var url = "/UserItems/" + itemId + "/UserData" +
            "?api_key=" + encodeURIComponent(auth.key) +
            "&userId=" + encodeURIComponent(auth.uid);

        try {
            var r = await fetch(url, { headers: jellyfinHeaders(auth) });
            if (!r.ok) {
                warn("读取播放数据失败, HTTP " + r.status);
                return false;
            }
            var data = await r.json();
            var current = (typeof data.PlayCount === "number") ? data.PlayCount
                : (typeof data.playCount === "number") ? data.playCount : 0;

            var body = { playCount: current + 1 };
            if (UPDATE_LAST_PLAYED_DATE) body.lastPlayedDate = new Date().toISOString();

            var w = await fetch(url, {
                method: "POST",
                headers: jellyfinHeaders(auth),
                body: JSON.stringify(body)
            });
            if (!w.ok) {
                warn("写入播放次数失败, HTTP " + w.status);
                return false;
            }
            return true;
        } catch (e) {
            warn("记录播放次数异常: " + e.message);
            return false;
        }
    }

    // ============ 播放逻辑 ============
    async function playLocal(id) {
        var auth = authQuery();
        if (!auth.key || !auth.uid) {
            alert("无法获取当前登录会话的 API Key。\n请确认已登录 Jellyfin 后刷新页面。");
            return;
        }

        var handshakeOk = await fetchLocalToken().then(function () { return true; }, function (e) {
            warn("token 握手失败: " + e.message);
            return false;
        });
        if (!handshakeOk) {
            alert("无法连接本地监听器 " + LISTENER + "\n请确认 Jellyfin 里的 PotPlayer Launcher 插件正在运行。");
            return;
        }

        var item;
        try {
            item = await fetchItemInfo(id);
        } catch (e) {
            alert(e.message);
            return;
        }

        if (!item.Path) {
            alert("Jellyfin 返回数据中没有 Path 字段\n(需要管理员权限才能读取文件路径)");
            return;
        }

        var resp;
        try {
            resp = await postToListener(auth, item, item.Path, listenerToken);
        } catch (e) {
            alert("请求监听器失败，请确认插件已加载\n" + e.message);
            return;
        }

        if (resp.status === 404 || resp.status === 405) {
            // 旧版监听器: 退回 GET 方式, 但没有进度回传
            warn("监听器为旧版本, 退回兼容模式(无进度回传)");
            try {
                resp = await legacyPlay(item.Path);
            } catch (e) {
                alert("请求监听器失败\n" + e.message);
                return;
            }
            if (resp.ok) {
                await bumpPlayCount(auth, id);
                toast("已用 PotPlayer 打开(旧版插件: 本次不记录进度)");
            } else {
                alert("监听器返回错误 HTTP " + resp.status);
            }
            return;
        }

        var result = null;
        try { result = await resp.json(); } catch (e) { /* 非 JSON 响应 */ }

        if (!resp.ok || !result || !result.ok) {
            var reason = (result && result.error) ? result.error : ("HTTP " + resp.status);
            alert("监听器拒绝播放: " + reason);
            return;
        }

        var title = item.Path.split(/[\\/]/).pop();
        if (item.StartSec > 30) {
            toast("PotPlayer 已启动，从 " + formatTime(item.StartSec) + " 续播\n关闭播放器后会把进度回传给 Jellyfin");
        } else {
            toast("PotPlayer 已启动\n关闭播放器后会把进度回传给 Jellyfin");
        }
        console.log("[PotPlayer] 开始播放 " + title + " (pid=" + (result.pid || "?") + ", startSec=" + item.StartSec + ")");
    }

    function formatTime(sec) {
        var s = Math.max(0, Math.floor(sec));
        var h = Math.floor(s / 3600);
        var m = Math.floor((s % 3600) / 60);
        var ss = s % 60;
        function pad(n) { return (n < 10 ? "0" : "") + n; }
        return (h > 0 ? h + ":" : "") + pad(m) + ":" + pad(ss);
    }

    // ============ 注入按钮 ============
    function addButton() {
        if (!/^#!?\/details/.test(location.hash)) {
            removeButton();
            return;
        }

        var q = location.hash.split("?")[1];
        if (!q) return;
        var id = new URLSearchParams(q).get("id");
        if (!id) return;

        // Jellyfin 的详情页会复用同一套 DOM: 点"更多类似"时它把容器内容换掉,
        // .btnPlay 等元素本身仍是同一个, 所以上一次注入的按钮不会自动消失。
        // 只判断"按钮存在"就会一直复用一个已经过时的按钮(位置/条目都不对) ——
        // 这就是"导航到新条目后按钮不出现, 刷新才有"的原因。
        // 因此这里既检查存在性, 也检查它属于哪个条目。
        var existing = document.getElementById(BUTTON_ID);
        if (existing && existing.dataset.itemId === id && existing.isConnected !== false && existing.parentNode) {
            return;
        }

        // 过时的按钮(换了条目/被移出文档)先清掉; 顺带清理可能重复的同类按钮
        removeButton();

        var playBtn = document.querySelector(".mainDetailButtons .btnPlay")
                   || document.querySelector(".detailButtons .btnPlay");
        if (!playBtn) return;

        var btn = document.createElement("button");
        btn.id = BUTTON_ID;
        btn.dataset.itemId = id;
        btn.className = playBtn.className + " emby-button";
        btn.title = "使用 PotPlayer 播放（支持续播与进度回传）";
        btn.style.cssText =
            "margin-left:8px;" +
            "display:inline-flex;" +
            "align-items:center;" +
            "cursor:pointer;";
        btn.innerHTML =
            '<svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" style="vertical-align:middle;margin-right:4px;">' +
            '<path d="M8 5v14l11-7z"/></svg>' +
            '<span>PotPlayer</span>';

        btn.addEventListener("click", function () {
            playLocal(id);
        });

        playBtn.parentNode.insertBefore(btn, playBtn.nextSibling);
    }

    function removeButton() {
        var btn = document.getElementById(BUTTON_ID);
        if (btn) btn.remove();
    }

    function debounce(fn, delay) {
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(fn, delay);
    }

    // ============ MutationObserver + 路由 ============
    function startObserver() {
        if (observer) observer.disconnect();
        observer = new MutationObserver(function () {
            debounce(addButton, 100);
        });
        observer.observe(document.body, { childList: true, subtree: true });
    }

    function onHashChange() {
        var newHash = location.hash;
        if (newHash !== lastHash) {
            lastHash = newHash;
            debounce(addButton, 50);
        }
    }

    // 同一个详情页里换条目(点"更多类似"、选集卡片等)时 hash 可能完全不变(#/details?id=xxx 只换 id),
    // 所以除了 MutationObserver, 也监听 history 的 popstate, 多一条触发路径。
    function onPopState() {
        debounce(addButton, 50);
    }

    function init() {
        // 页面会话可能稍后就绪, 先探测一次监听器, 每次进详情页再确认
        detectListener();

        startObserver();
        window.addEventListener("hashchange", onHashChange);
        window.addEventListener("popstate", onPopState);
        addButton();

        setInterval(function () {
            if (/^#!?\/details/.test(location.hash)) {
                // 按钮不存在、或还挂着上一个条目的 id(详情页 DOM 复用), 都要重新注入
                var btn = document.getElementById(BUTTON_ID);
                var q = location.hash.split("?")[1];
                var id = q ? new URLSearchParams(q).get("id") : null;
                if (!btn || (id && btn.dataset.itemId !== id)) addButton();
            }
            if (!hasSession()) return;
            if (!listenerChecked) detectListener();
        }, 2000);
    }

    init();
})();
