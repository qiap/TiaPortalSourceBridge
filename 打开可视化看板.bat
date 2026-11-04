@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalDashboard.exe"

if not exist "%EXE%" (
  call "%ROOT%build.bat"
  if errorlevel 1 (
    echo.
    echo 编译失败，请查看上面的错误提示。
    pause
    exit /b 1
  )
)

title TIA Portal Source Bridge - 可视化控制看板

if "%~1"=="" (
  "%EXE%" --config "%ROOT%config.json"
) else (
  "%EXE%" --project "%~f1" --config "%ROOT%config.json"
)

set "RESULT=%errorlevel%"
if not "%RESULT%"=="0" (
  echo.
  echo 服务异常退出，错误码: %RESULT%
  pause
)
exit /b %RESULT%
