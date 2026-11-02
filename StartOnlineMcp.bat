@echo off
setlocal
set "EXE=%~dp0online-bin\TiaPortalSourceBridge.exe"
if not exist "%EXE%" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-online.ps1"
if not exist "%EXE%" exit /b 1
"%EXE%" online-host --config "%~dp0mcp-config.json"
pause
