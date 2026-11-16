@echo off
setlocal
pushd "%~dp0" || exit /b 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install-VmHostKey.ps1"
set "code=%errorlevel%"
popd
echo.
pause
exit /b %code%
