@echo off
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe"

if not exist "%EXE%" (
  call "%ROOT%build.bat"
  if errorlevel 1 exit /b 1
)

if "%~1"=="" (
  "%EXE%" import --config "%ROOT%config.json"
) else (
  "%EXE%" import --project "%~f1" --config "%ROOT%config.json"
)
exit /b %errorlevel%
