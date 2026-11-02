@echo off
setlocal EnableDelayedExpansion
set "ROOT=%~dp0"
set "OUT=%ROOT%src\TiaPortalSourceBridge\bin\Release\net48"
set "ONLINE_OUT=%ROOT%online-bin"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%Build-UiaInterop.ps1" -Quiet
if errorlevel 1 exit /b %errorlevel%

set "MAIN_BUILT=0"
where dotnet >nul 2>nul
if not errorlevel 1 (
  dotnet --list-sdks | findstr /r "." >nul
  if not errorlevel 1 (
    dotnet build "%ROOT%TiaPortalSourceBridge.sln" -c Release
    if not errorlevel 1 set "MAIN_BUILT=1"
    if "!MAIN_BUILT!"=="0" echo [WARN] SDK build failed. Trying the .NET Framework compiler fallback.
  )
)

if "!MAIN_BUILT!"=="0" (
  if not exist "%CSC%" (
    echo [ERROR] Neither a working .NET SDK nor the x64 .NET Framework compiler was found.
    echo Install Visual Studio 2022 or the .NET Framework 4.8 Developer Pack.
    exit /b 1
  )

  if not exist "%OUT%" mkdir "%OUT%"
  if exist "%OUT%\TiaPortalSourceBridge.exe.old" del /f /q "%OUT%\TiaPortalSourceBridge.exe.old" >nul 2>nul
  if exist "%OUT%\TiaPortalSourceBridge.exe" move /y "%OUT%\TiaPortalSourceBridge.exe" "%OUT%\TiaPortalSourceBridge.exe.old" >nul 2>nul
  if exist "%OUT%\S7OnlineWorker.exe.old" del /f /q "%OUT%\S7OnlineWorker.exe.old" >nul 2>nul
  if exist "%OUT%\S7OnlineWorker.exe" move /y "%OUT%\S7OnlineWorker.exe" "%OUT%\S7OnlineWorker.exe.old" >nul 2>nul
  if exist "%ONLINE_OUT%\TiaPortalSourceBridge.exe.old" del /f /q "%ONLINE_OUT%\TiaPortalSourceBridge.exe.old" >nul 2>nul
  if exist "%ONLINE_OUT%\TiaPortalSourceBridge.exe" move /y "%ONLINE_OUT%\TiaPortalSourceBridge.exe" "%ONLINE_OUT%\TiaPortalSourceBridge.exe.old" >nul 2>nul
  if exist "%ONLINE_OUT%\S7OnlineWorker.exe.old" del /f /q "%ONLINE_OUT%\S7OnlineWorker.exe.old" >nul 2>nul
  if exist "%ONLINE_OUT%\S7OnlineWorker.exe" move /y "%ONLINE_OUT%\S7OnlineWorker.exe" "%ONLINE_OUT%\S7OnlineWorker.exe.old" >nul 2>nul
  set "SOURCES="
  for /r "%ROOT%src\TiaPortalSourceBridge" %%F in (*.cs) do (
    set "FILE_PATH=%%F"
    if "!FILE_PATH:\obj\=!"=="!FILE_PATH!" if "!FILE_PATH:\bin\=!"=="!FILE_PATH!" (
      set SOURCES=!SOURCES! "%%F"
    )
  )

  "%CSC%" /nologo /utf8output /codepage:65001 /target:exe /platform:x64 /optimize+ /out:"%OUT%\TiaPortalSourceBridge.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /link:"%ROOT%build-support\uia\Interop.UIAutomationClient.dll" !SOURCES!
  if errorlevel 1 exit /b !errorlevel!
)

if exist "%CSC%" if exist "%ROOT%online-helper\Program.cs" (
  set "WORKER_SOURCES="%ROOT%online-helper\Program.cs""
  for %%F in ("%ROOT%online-helper\vendor\*.cs") do (
    set WORKER_SOURCES=!WORKER_SOURCES! "%%F"
  )
  "%CSC%" /nologo /utf8output /codepage:65001 /target:exe /platform:x86 /optimize+ /out:"%OUT%\S7OnlineWorker.exe" /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll !WORKER_SOURCES!
  if errorlevel 1 exit /b !errorlevel!
)
if not exist "%OUT%\S7OnlineWorker.exe" exit /b 1

if exist "%OUT%\TiaPortalSourceBridge.exe" (
  if not exist "%ONLINE_OUT%" mkdir "%ONLINE_OUT%"
  copy /y "%OUT%\TiaPortalSourceBridge.exe" "%ONLINE_OUT%\TiaPortalSourceBridge.exe" >nul
  if errorlevel 1 exit /b !errorlevel!
  if exist "%OUT%\S7OnlineWorker.exe" copy /y "%OUT%\S7OnlineWorker.exe" "%ONLINE_OUT%\S7OnlineWorker.exe" >nul
  if errorlevel 1 exit /b !errorlevel!
  xcopy "%ROOT%templates" "%OUT%\templates\" /e /i /y /q >nul
  if errorlevel 1 exit /b !errorlevel!
  xcopy "%ROOT%templates" "%ONLINE_OUT%\templates\" /e /i /y /q >nul
  if errorlevel 1 exit /b !errorlevel!
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%TrustThisTool.ps1" -QuietIfNotAdmin
  del /f /q "%OUT%\*.exe.old" >nul 2>nul
  del /f /q "%ONLINE_OUT%\*.exe.old" >nul 2>nul
)

exit /b 0
