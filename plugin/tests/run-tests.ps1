<#
.SYNOPSIS
    Build and run the plugin behaviour tests.

.DESCRIPTION
    Runs plugin/tests - a plain console app (no NuGet test framework) that starts a real
    HttpListener and exercises the protocol contract in docs/PROTOCOL.md. The player is
    replaced by a .cmd recorder, so no PotPlayer window is opened.

    Optional live checks (launch + resume /seek= against a real Jellyfin on localhost:8096)
    are enabled by setting these environment variables first:
        $env:POTPLAYER_TEST_JELLYFIN_URL = "http://127.0.0.1:8096"
        $env:POTPLAYER_TEST_USER_TOKEN   = "<browser session token>"
        $env:POTPLAYER_TEST_USER_ID      = "<user id>"

    ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 files without a BOM as ANSI
    and would garble non-ASCII text.

.PARAMETER JellyfinDir
    Jellyfin server install directory used for compilation (default: the Windows install path).

.PARAMETER EnableJellyfinDlls
    Reference the Jellyfin NuGet packages instead of a local server installation.
    Use this on machines (or CI runners) that do not have Jellyfin installed.

.EXAMPLE
    .\run-tests.ps1
    .\run-tests.ps1 -JellyfinDir "D:\Jellyfin\Server"
    .\run-tests.ps1 -EnableJellyfinDlls
#>
[CmdletBinding()]
param(
    [string]$JellyfinDir = "",
    [switch]$EnableJellyfinDlls
)

$ErrorActionPreference = "Stop"
$testsDir = $PSScriptRoot
$runArgs = @(
    "run", "-c", "Release",
    "--project", (Join-Path $testsDir "PotPlayerLauncher.Tests.csproj"),
    "--nologo"
)
if ($JellyfinDir) { $runArgs += "-p:JellyfinDir=$JellyfinDir" }
if ($EnableJellyfinDlls) { $runArgs += "-p:EnableJellyfinDlls=true" }

# Allow rolling forward when only a newer major runtime is installed (e.g. .NET 10 but not 9)
$hasNet9 = (dotnet --list-runtimes) | Select-String -Quiet "Microsoft\.NETCore\.App 9\."
if (-not $hasNet9) {
    Write-Host "No .NET 9 runtime found; enabling roll-forward to a newer major version" -ForegroundColor DarkGray
    $env:DOTNET_ROLL_FORWARD = "Major"
}

# The fake player sleeps this long inside the .cmd recorder, so the position probe and the
# progress heartbeat have time to produce readings. Tests finish well before it elapses.
if (-not $env:POTPLAYER_TEST_PLAYER_SECONDS) { $env:POTPLAYER_TEST_PLAYER_SECONDS = "60" }

& dotnet @runArgs
exit $LASTEXITCODE
