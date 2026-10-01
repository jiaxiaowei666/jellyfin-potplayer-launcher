@echo off
rem 双击即可构建并安装插件到默认 Jellyfin 数据目录。
rem 需要自定义数据目录时, 在 PowerShell 里执行:
rem   powershell -ExecutionPolicy Bypass -File .\install.ps1 -DataDir "D:\JellyfinData"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
