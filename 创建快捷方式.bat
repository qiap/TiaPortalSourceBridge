@echo off
setlocal
cd /d "%~dp0"
set "exe=%~dp0src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe"
if not exist "%exe%" set "exe=%~dp0online-bin\TiaPortalSourceBridge.exe"
if not exist "%exe%" set "exe=%~dp0TiaPortalSourceBridge.exe"
if not exist "%exe%" (
    echo Dashboard EXE missing. Extract the entire ZIP first.
    pause
    exit /b 1
)
"%exe%" create-shortcuts --config "%~dp0config.json" --notify
exit /b %errorlevel%
