# jellyfin-potplayer-launcher

[![build-and-test](https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher/actions/workflows/ci.yml/badge.svg)](https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows-blue)
![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11.x-00A4DC)

One click in the Jellyfin web UI plays the media in your **local PotPlayer** on the same machine (no transcoding, no network stream) — and **reports playback progress back to Jellyfin**, so resume, play count and "Continue Watching" all work.

中文文档: [README.md](README.md)

---

## Features

- ▶ **One-click local playback** from the item detail page — PotPlayer opens the file straight from disk: no quality loss, no server CPU usage.
- ⏯ **Resume** from the position Jellyfin remembers (the plugin passes `/seek=HH:MM:SS` to PotPlayer).
- 📈 **Progress reporting** — a `Sessions/Playing/Progress` heartbeat every 15 s while playing, then the final position on player exit, so Jellyfin's resume / play count / watched state stay correct.
- 🔐 **No hardcoded API key** — the userscript reads the current session token from the page's `ApiClient`, so every user records under their own identity.
- 🛡 **Local-only by design** — the listener binds `127.0.0.1` only, with an Origin allowlist and a per-startup random token.
- ♻️ **Lifecycle tied to the server** — runs as a Jellyfin plugin (`IHostedService`); no extra always-on process.

## How it works

```
Jellyfin Web (Tampermonkey-injected ▶ PotPlayer button)
   │ ① read token / userId from the page's ApiClient
   │ ② GET /Items/{id}?fields=Path,MediaSources,RunTimeTicks,UserData for the path + resume position
   ▼
GET  http://127.0.0.1:13579/token      ← handshake, returns this startup's temporary token
POST http://127.0.0.1:13579/play       ← {path, apiKey, userId, itemId, startSec, totalSec}
   ▼
Plugin (inside the Jellyfin process, HttpListener on port 13579)
   │ ③ validate Origin / temporary token / ask Jellyfin to verify the token and userId / check file and player
   ▼
Process.Start("PotPlayerMini64.exe" "<file>" "/seek=00:05:00")
   │ ④ POST Sessions/Playing/Progress every 15 s
   ▼
Player exits → POST Sessions/Playing/Stopped (PositionTicks = played seconds)
```

## Requirements

| Item | Requirement |
|---|---|
| OS | **Windows** (the plugin uses `HttpListener` and launches a desktop player; server and player must be on the same machine) |
| Jellyfin | **10.11.x** (developed and verified on 10.11.11; 10.10 and older are not adapted) |
| Build | .NET SDK 9+ (target framework `net9.0`) plus the Jellyfin server assemblies |
| Browser | Tampermonkey / Violentmonkey, with Jellyfin opened at `http://localhost:8096` or `http://127.0.0.1:8096` |
| Player | PotPlayer (`PotPlayerMini64.exe`) |

## Install

> This repository is a monorepo with two parts: `plugin/` (Jellyfin server plugin) and `userscript/` (browser script). Install both.

### 1. Userscript (browser side)

1. Install Tampermonkey (on Chrome, enable Developer mode in `chrome://extensions`).
2. Open this URL (replace `jiaxiaowei666`):

   ```
   https://raw.githubusercontent.com/jiaxiaowei666/jellyfin-potplayer-launcher/main/userscript/jellyfin-potplayer-button.user.js
   ```

   Tampermonkey will show its install page. You can also paste the file contents into a new script.
3. Reload Jellyfin. **No API key needed** — your login session is enough.

### 2. Plugin (server side)

**Option A — install script (recommended)**

```powershell
git clone https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher.git
cd jellyfin-potplayer-launcher\plugin\deploy
powershell -ExecutionPolicy Bypass -File .\install.ps1
# non-default data directory:
powershell -ExecutionPolicy Bypass -File .\install.ps1 -DataDir "D:\JellyfinData"
```

**Option B — manual build**

```powershell
cd plugin
dotnet build -c Release
Copy-Item .\bin\Release\net9.0\Jellyfin.Plugin.PotPlayerLauncher.dll `
  "C:\ProgramData\Jellyfin\Server\plugins\PotPlayerLauncher\" -Force
# meta.json must exist next to the DLL (see plugin/deploy/meta.json); save it as UTF-8 without BOM
```

If Jellyfin is installed elsewhere, point the build at its assemblies:

```powershell
dotnet build -c Release -p:JellyfinDir="D:\Jellyfin\Server"
# or build against NuGet packages instead (needs network):
dotnet build -c Release -p:EnableJellyfinDlls=true
```

**Restart Jellyfin.** When starting it manually, always pass the data directory, otherwise it creates a brand-new empty one:

```powershell
Stop-Process -Name jellyfin -Force
Start-Process "C:\Program Files\Jellyfin\Server\jellyfin.exe" -ArgumentList '--datadir "C:\ProgramData\Jellyfin\Server"'
```

**Verify**: the log shows `PotPlayerLauncher listening on http://127.0.0.1:13579/`, and <http://127.0.0.1:13579/token> returns `{"ok":true,"token":"...","version":2}`.

## Usage

1. Open an item's **detail page** in the Jellyfin web UI.
2. A **▶ PotPlayer** button appears next to the official Play button — click it.
3. PotPlayer opens the file locally; if Jellyfin has a resume position, playback starts there.
4. Close the player when done — the progress is written back and shows up as "Continue Watching".

## Configuration

| Setting | Where | Default | Notes |
|---|---|---|---|
| Player path | `PotPlayerPath` in `config\PotPlayerLauncher.json`, or env `POTPLAYER_PATH` | `C:\Program Files (x86)\app\PotPlayer\PotPlayerMini64.exe` | Env var wins; falls back to the default |
| Listener port | env `POTPLAYER_LAUNCHER_PORT` | `13579` | If changed, update `LISTENER` in the userscript |
| Temporary token | `plugins\PotPlayerLauncher\listener-token.txt` | random per startup | For debugging/manual calls only; changes on every restart |

```json
// C:\ProgramData\Jellyfin\Server\config\PotPlayerLauncher.json
{
  "PotPlayerPath": "C:\\Program Files\\DAUM\\PotPlayer\\PotPlayerMini64.exe"
}
```

## Is the resume actually Jellyfin's doing?

PotPlayer keeps its own per-file playback position, and the two are independent:

| | PotPlayer's own memory | Jellyfin's resume position |
|---|---|---|
| Stored in | PotPlayer's playback history, keyed by **file path** | Jellyfin DB `UserData.PlaybackPositionTicks`, keyed by **item + user** |
| Who seeks | PotPlayer, after reading its own memory | The plugin, via the `/seek=HH:MM:SS` command-line argument |
| Visible on the web UI | No | Yes — that's the "Continue Watching / watched N minutes" indicator |

The plugin log makes it obvious:

```
PotPlayerLauncher launched PotPlayer for "..." (pid=5044  startSec=300 seek="/seek=00:05:00")
PotPlayerLauncher launched PotPlayer for "..." (pid=19816 startSec=0   seek="<none>")
```

`startSec` comes from Jellyfin's stored position. PotPlayer's own memory can never appear as a command-line argument, so seeing `seek="/seek=..."` proves the seek came from Jellyfin's data.

## Troubleshooting

| Symptom | What to check |
|---|---|
| No button on the detail page | Userscript enabled? Page opened at `http://localhost:8096` or `http://127.0.0.1:8096` (the only `@match` patterns)? Check the browser console for `[PotPlayer]` logs |
| "cannot reach local listener" | Plugin not loaded or different port; open <http://127.0.0.1:13579/token> — it should return JSON |
| `{"ok":false,"error":"invalid token"}` | The cached temporary token is stale (Jellyfin restarted) — reload the page |
| `{"ok":false,"error":"invalid jellyfin token"}` | The page session is gone — log in again |
| `{"ok":false,"error":"userId mismatch"}` | The page session belongs to another user, or the fallback userId is wrong |
| Log warns `API key without a user context` | You passed a console API key: playback works, but **progress is not persisted**. Use the browser session token instead |
| No progress after playback | Search the log for `reported progress`; playback shorter than 20 s is intentionally not reported |
| Reported, but the position did not change | Same API-key warning as above, or check `Playback stopped reported by app ...` in the log |
| Non-ASCII paths fail | Make sure you use this repository's version (it decodes the raw query string; `HttpListener.QueryString` mis-decodes as GBK) |
| CORS error in the browser | The listener only allows `localhost` / `127.0.0.1` origins (any port); opening Jellyfin via a LAN IP will fail |
| Port 13579 not listening | Search the log for `failed to bind` and check for a port conflict |
| Server shows the setup wizard after restart | You forgot the `--datadir` argument |

## Limitations

- **Windows only** — `HttpListener`, launching a desktop player, and the PotPlayer path are all Windows-specific.
- **Same machine only** — the plugin opens a local disk path, so the server and player must be on one host.
- **Progress is estimated** from the player process lifetime (resume position + seconds alive). Pausing still counts, so heavy pausing can over-report. Playback shorter than 20 s is not reported.
- **PotPlayer only** — no mpv/VLC/MPC support (those would need a way to read the player's internal position, e.g. mpv's IPC).
- **LAN access** — opening the web UI via a LAN IP fails the Origin check.

## Security

- The listener binds `127.0.0.1` only; other devices on the LAN cannot reach it.
- Requests from outside the Origin allowlist get 403 with no CORS headers.
- `POST /play` requires both (a) the per-startup token (only same-origin local pages can obtain it via `/token`) and (b) a valid Jellyfin token that can see the claimed `userId`.
- Known trade-off: any program on the machine that can serve a `localhost` page can obtain the temporary token — but it also needs a valid Jellyfin token to play anything.
- Validation uses `/Users` rather than `/Users/Me`: console-generated API keys have no user context and get a 400 from `/Users/Me`.

## Repository layout

```
.
├── docs/
│   └── PROTOCOL.md                  # contract between userscript and plugin
├── plugin/                          # Jellyfin server plugin (C# / net9.0)
│   ├── Directory.Build.props         # JellyfinDir / EnableJellyfinDlls + assembly refs
│   ├── PotPlayerListener.cs         # listener / auth / launch / progress reporting
│   ├── tests/                        # 41 protocol checks, fake player recorder, no NuGet
│   ├── deploy/
│   │   ├── install.ps1              # build + install in one step
│   │   └── meta.json                # plugin metadata required for manual install
│   └── README.md                    # implementation notes and test details (Chinese)
├── userscript/
│   ├── jellyfin-potplayer-button.user.js
│   └── README.md
├── LICENSE
└── README.md / README.en.md
```

## Tests

```powershell
cd plugin/tests
.\run-tests.ps1          # 41 checks, ~30s, never opens a real player
```

The suite drives a real `HttpListener` over HTTP and covers the protocol contract in
[`docs/PROTOCOL.md`](docs/PROTOCOL.md): handshake, auth, the Origin allowlist, CORS preflight,
payload validation, the legacy `GET /play` endpoint, non-ASCII paths and port-conflict handling.
The player is replaced by a small `.cmd` recorder that writes its own command line to a file,
so the suite can assert what the plugin actually passed (for example the resume `/seek=00:05:00`).

On a machine without Jellyfin installed (or in CI), add a switch to build against the NuGet packages instead:

```powershell
.\run-tests.ps1 -EnableJellyfinDlls
```

## Continuous integration

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs two jobs:

| Job | Runner | What it does |
|---|---|---|
| `test` | windows-latest | Builds the plugin, runs the full 41-check suite, uploads the plugin DLL as an artifact |
| `build-linux` | ubuntu-latest | Compile-only, to catch host API changes / target-framework drift early (runtime is Windows-only, so no tests) |

Both jobs pass `-p:EnableJellyfinDlls=true`, i.e. they pull the Jellyfin packages from NuGet instead of
requiring a Jellyfin installation on the runner. The main thing CI catches: a Jellyfin upgrade that
changes host APIs breaks the build in your own repository instead of on a user's machine.

## Build from source

```powershell
git clone https://github.com/jiaxiaowei666/jellyfin-potplayer-launcher.git
cd jellyfin-potplayer-launcher\plugin
dotnet build -c Release
# output: bin\Release\net9.0\Jellyfin.Plugin.PotPlayerLauncher.dll
```

Requires .NET SDK 9+. The build references the Jellyfin server assemblies from `C:\Program Files\Jellyfin\Server\` by default; override with `-p:JellyfinDir=...`, or use `-p:EnableJellyfinDlls=true` to reference NuGet packages instead (versions must match your server).

## Contributing

Issues and PRs are welcome. Before submitting, please make sure `cd plugin && dotnet build -c Release` is clean and `cd plugin/tests && ./run-tests.ps1` prints `OK`. If you change the protocol (`/token`, the `/play` payload, status codes or error identifiers), update [`docs/PROTOCOL.md`](docs/PROTOCOL.md) **first**, then the implementation and the tests. User-visible changes should get a line under `Unreleased` in [`CHANGELOG.md`](CHANGELOG.md).

Version numbering, how the three versions (protocol / plugin / userscript) relate, and the full release checklist are documented in [PROTOCOL.md section 8](docs/PROTOCOL.md#8-版本协商).

## License

[MIT](LICENSE)
