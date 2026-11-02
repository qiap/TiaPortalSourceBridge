@echo off
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe"

if not exist "%EXE%" (
  call "%ROOT%build.bat"
  if errorlevel 1 (
    echo.
    echo Build failed. See the messages above.
    pause
    exit /b 1
  )
)

if "%~1"=="" (
  "%EXE%" interactive --config "%ROOT%config.json"
) else (
  "%EXE%" interactive --project "%~f1" --config "%ROOT%config.json"
)

set "RESULT=%errorlevel%"
echo.
if "%RESULT%"=="0" echo Operation finished.
if not "%RESULT%"=="0" echo Operation failed. Import item errors: last-import-errors.log. Other errors: last-error.log.
pause
exit /b %RESULT%
