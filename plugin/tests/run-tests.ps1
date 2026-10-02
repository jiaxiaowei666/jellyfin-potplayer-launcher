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
    [switch]$EnableJellyfinDlls,
    [switch]$NoRelaunch
)

# ---------------------------------------------------------------------------
# Engine guard.
#
# Windows PowerShell 5.1 (the inbox engine) reads .ps1 files WITHOUT a BOM as ANSI,
# which turns any non-ASCII text in this file into mojibake - and, depending on the
# bytes, into syntax errors. This script is therefore intentionally ASCII-only.
#
# If PowerShell 7 is installed we re-run ourselves under it: it reads UTF-8 without a
# BOM correctly and gives far better diagnostics. Missing pwsh is not an error - the
# script still works on 5.1, so CI and bare machines do not need PowerShell 7.
# ---------------------------------------------------------------------------
if (-not $NoRelaunch -and $PSVersionTable.PSEdition -ne "Core") {
    $pwsh7 = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($pwsh7) {
        Write-Host "Windows PowerShell $($PSVersionTable.PSVersion): re-running under pwsh (PowerShell 7)" -ForegroundColor DarkGray
        $relaunch = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-NoRelaunch")
        if ($JellyfinDir) { $relaunch += @("-JellyfinDir", $JellyfinDir) }
        if ($EnableJellyfinDlls) { $relaunch += "-EnableJellyfinDlls" }
        & $pwsh7.Source @relaunch
        exit $LASTEXITCODE
    }

    Write-Host "NOTE: running on Windows PowerShell $($PSVersionTable.PSVersion); PowerShell 7 (pwsh) not found." -ForegroundColor DarkYellow
    Write-Host "      This script is ASCII-only so it works here, but install PowerShell 7 for a better experience." -ForegroundColor DarkYellow
}

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
