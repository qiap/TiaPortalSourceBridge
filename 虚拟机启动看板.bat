@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-VmDashboard.ps1" %*
set "code=%errorlevel%"
if not "%code%"=="0" (
  echo.
  echo Exit code: %code%
  pause
)
exit /b %code%
