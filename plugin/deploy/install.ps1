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

.EXAMPLE
    .\install.ps1
    .\install.ps1 -DataDir "D:\JellyfinData"
    .\install.ps1 -JellyfinDir "D:\Jellyfin\Server"
#>
[CmdletBinding()]
param(
    [string]$DataDir = "C:\ProgramData\Jellyfin\Server",
    [string]$JellyfinDir = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot          # .../plugin
$deployDir = $PSScriptRoot                              # .../plugin/deploy
$pluginName = "PotPlayerLauncher"
$dllName = "Jellyfin.Plugin.PotPlayerLauncher.dll"
$targetDir = Join-Path $DataDir "plugins\$pluginName"

Write-Host "==> Building ($Configuration)" -ForegroundColor Cyan
$buildArgs = @("build", "-c", $Configuration, "--nologo")
if ($JellyfinDir) { $buildArgs += "-p:JellyfinDir=$JellyfinDir" }
Push-Location $projectDir
try {
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

$sourceDll = Join-Path $projectDir "bin\$Configuration\net9.0\$dllName"
if (-not (Test-Path $sourceDll)) { throw "Build output not found: $sourceDll" }

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
