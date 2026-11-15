@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-VmDashboard.ps1" -SetupSsh -CheckOnly %*
set "code=%errorlevel%"
echo.
pause
exit /b %code%
