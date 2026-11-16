@echo off
setlocal
pushd "%~dp0" || exit /b 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Connect-VmDashboard.ps1" %*
set "code=%errorlevel%"
popd
if not "%code%"=="0" (
  echo.
  echo Exit code: %code%
  pause
)
exit /b %code%
