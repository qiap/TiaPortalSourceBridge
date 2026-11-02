@echo off
setlocal
if "%~1"=="" (
  powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Build-ProjectIndex.ps1" -PauseOnCompletion
) else (
  powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Build-ProjectIndex.ps1" %*
)
exit /b %errorlevel%
