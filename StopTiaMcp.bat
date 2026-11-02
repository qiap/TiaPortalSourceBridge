@echo off
setlocal
cd /d "%~dp0"
set "EXE=%~dp0src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe"
if not exist "%EXE%" (
  echo TiaPortalSourceBridge.exe not found. Run build.bat first.
  pause
  exit /b 1
)
"%EXE%" stop-mcp --config "%~dp0config.json"
pause
