@echo off
setlocal
cd /d "%~dp0"
set "exe=%~dp0src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalDashboard.exe"
if not exist "%exe%" if exist "%~dp0build.bat" call "%~dp0build.bat"
if not exist "%exe%" (
    echo Dashboard EXE missing. Extract the entire ZIP to a permanent folder.
    pause
    exit /b 1
)
"%exe%" --config "%~dp0config.json" %*
set "code=%errorlevel%"
if not "%code%"=="0" (
    echo Dashboard startup failed with exit code %code%.
    pause
)
exit /b %code%
