@echo off
setlocal
cd /d "%~dp0"
set "EXE=%~dp0src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe"
if not exist "%EXE%" call "%~dp0build.bat"
if not exist "%EXE%" exit /b 1
set "MCP_CONFIG=%~dp0mcp-config.json"
if /i "%~1"=="V21" shift
if "%~1"=="" (
  "%EXE%" mcp-host --config "%MCP_CONFIG%"
) else (
  "%EXE%" mcp-host --config "%MCP_CONFIG%" --project "%~1"
)
pause
