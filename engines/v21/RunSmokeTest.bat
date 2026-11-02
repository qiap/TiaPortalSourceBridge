@echo off
setlocal
cd /d "%~dp0"
call "%~dp0build.bat"
if errorlevel 1 (
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tests\SmokeMcp.ps1" %*
pause
