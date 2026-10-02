@echo off
rem Double-click to build and install the plugin into the default Jellyfin data directory.
rem For a custom data directory, run this from PowerShell instead:
rem   powershell -ExecutionPolicy Bypass -File .\install.ps1 -DataDir "D:\JellyfinData"
rem
rem Note: install.ps1 re-runs itself under pwsh (PowerShell 7) when that is installed.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
