<#
.SYNOPSIS
    Build and install the PotPlayer Launcher Jellyfin plugin.

.DESCRIPTION
    Builds the plugin, copies the DLL into the Jellyfin plugin directory and writes
    meta.json if it is missing (Jellyfin needs it, otherwise the plugin can end up
    marked as "Malfunctioned").

    Messages are kept in English because Windows PowerShell 5.1 reads .ps1 files
    without a BOM as ANSI and would garble non-ASCII text.

.PARAMETER DataDir
    Jellyfin data directory (the one passed to --datadir).

.PARAMETER JellyfinDir
    Jellyfin server install directory used for compilation (official assemblies).
    Leave empty to use the default in the csproj.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER Package
    Do not install anything. Instead build a release package for GitHub Releases:
    dist/Jellyfin.Plugin.PotPlayerLauncher.dll, dist/meta.json and a SHA256 checksum file.

.PARAMETER CopyDllToDist
    With -Package, also copy the built dll into dist\ so that dist\ is a complete,
    self-contained upload set (useful in CI: one upload path, no nested folders).

.PARAMETER EnableJellyfinDlls
    Build against the Jellyfin NuGet packages instead of a local installation.
    Use this in CI or on a machine without Jellyfin installed.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -DataDir "D:\JellyfinData"
    .\install.ps1 -JellyfinDir "D:\Jellyfin\Server"
    .\install.ps1 -Package -CopyDllToDist -EnableJellyfinDlls
#>
[CmdletBinding()]
param(
    [string]$DataDir = "C:\ProgramData\Jellyfin\Server",
    [string]$JellyfinDir = "",
    [string]$Configuration = "Release",
    [switch]$Package,
    [switch]$CopyDllToDist,
    [switch]$EnableJellyfinDlls,
    [switch]$NoRelaunch
)

# ---------------------------------------------------------------------------
# Engine guard (same reasoning as in tests/run-tests.ps1):
# Windows PowerShell 5.1 reads .ps1 files without a BOM as ANSI, which garbles any
# non-ASCII text - this script is therefore ASCII-only. When PowerShell 7 is present
# we re-run under it for correct UTF-8 handling and better diagnostics; when it is
# not, we just carry on (no hard dependency on PowerShell 7).
# ---------------------------------------------------------------------------
if (-not $NoRelaunch -and $PSVersionTable.PSEdition -ne "Core") {
    $pwsh7 = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($pwsh7) {
        Write-Host "Windows PowerShell $($PSVersionTable.PSVersion): re-running under pwsh (PowerShell 7)" -ForegroundColor DarkGray
        $relaunch = @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-NoRelaunch",
            "-DataDir", $DataDir, "-Configuration", $Configuration
        )
        if ($JellyfinDir) { $relaunch += @("-JellyfinDir", $JellyfinDir) }
        if ($Package) { $relaunch += "-Package" }
        if ($CopyDllToDist) { $relaunch += "-CopyDllToDist" }
        if ($EnableJellyfinDlls) { $relaunch += "-EnableJellyfinDlls" }
        & $pwsh7.Source @relaunch
        exit $LASTEXITCODE
    }

    Write-Host "NOTE: running on Windows PowerShell $($PSVersionTable.PSVersion); PowerShell 7 (pwsh) not found." -ForegroundColor DarkYellow
    Write-Host "      This script is ASCII-only so it works here, but install PowerShell 7 for a better experience." -ForegroundColor DarkYellow
}

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot          # .../plugin
$deployDir = $PSScriptRoot                              # .../plugin/deploy
$pluginName = "PotPlayerLauncher"
$dllName = "Jellyfin.Plugin.PotPlayerLauncher.dll"
$targetDir = Join-Path $DataDir "plugins\$pluginName"

Write-Host "==> Building ($Configuration)" -ForegroundColor Cyan
$buildArgs = @("build", "-c", $Configuration, "--nologo")
if ($JellyfinDir) { $buildArgs += "-p:JellyfinDir=$JellyfinDir" }
if ($EnableJellyfinDlls) { $buildArgs += "-p:EnableJellyfinDlls=true" }
Push-Location $projectDir
try {
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

$sourceDll = Join-Path $projectDir "bin\$Configuration\net9.0\$dllName"
if (-not (Test-Path $sourceDll)) { throw "Build output not found: $sourceDll" }

if ($Package) {
    # Release packaging: the two files a manual installer needs, plus checksums
    $distDir = Join-Path (Split-Path -Parent $projectDir) "dist"
    if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null

    Copy-Item (Join-Path $deployDir "meta.json") (Join-Path $distDir "meta.json") -Force
    if ($CopyDllToDist) {
        # CI 用: dist\ 自成一套上传内容, 上传路径只写 dist/* 即可(解压后就是三个文件, 没有子目录)
        Copy-Item $sourceDll (Join-Path $distDir $dllName) -Force
    }

    $version = (Get-Item $sourceDll).VersionInfo.FileVersion
    $hashes = Get-ChildItem $distDir -File | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    $hashes | Set-Content (Join-Path $distDir "SHA256SUMS.txt") -Encoding ascii

    Write-Host "==> Package ready (plugin $version)" -ForegroundColor Green
    Get-ChildItem $distDir -File | Select-Object Name, Length | Format-Table -AutoSize
    Write-Host "Upload these files to the GitHub Release:"
    Write-Host "  $distDir\$dllName"
    Write-Host "  $distDir\meta.json"
    Write-Host "  $distDir\SHA256SUMS.txt"
    exit 0
}

Write-Host "==> Installing to $targetDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Copy-Item $sourceDll (Join-Path $targetDir $dllName) -Force

# meta.json: only write it when missing, so existing settings are never clobbered
$targetMeta = Join-Path $targetDir "meta.json"
if (Test-Path $targetMeta) {
    Write-Host "    meta.json exists, left untouched" -ForegroundColor DarkGray
} else {
    $template = Join-Path $deployDir "meta.json"
    if (-not (Test-Path $template)) { throw "Template not found: $template" }
    Copy-Item $template $targetMeta -Force
    Write-Host "    wrote meta.json" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Done. Restart Jellyfin to load the plugin (always pass the data directory):" -ForegroundColor Green
Write-Host "  Stop-Process -Name jellyfin -Force"
Write-Host "  Start-Process 'C:\Program Files\Jellyfin\Server\jellyfin.exe' -ArgumentList '--datadir `"$DataDir`"'"
Write-Host ""
Write-Host "Verify: log shows 'PotPlayerLauncher listening on http://127.0.0.1:13579/'"
Write-Host "        http://127.0.0.1:13579/token returns {""ok"":true,...}"
