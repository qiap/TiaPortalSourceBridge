param([string]$OutputDirectory = "$PSScriptRoot\..\portable-releases")
$ErrorActionPreference = 'Stop'
$toolRoot = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
$releaseRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$packageName = 'TiaPortalSourceBridge-V17-Portable-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$packageRoot = Join-Path $releaseRoot $packageName
$binRoot = Join-Path $packageRoot 'bin'
New-Item -ItemType Directory -Path $binRoot | Out-Null
$utf8 = New-Object Text.UTF8Encoding($true)
function Write-PackageText([string]$RelativePath, [string]$Content) {
    [IO.File]::WriteAllText((Join-Path $packageRoot $RelativePath), $Content, $utf8)
}
$sourceBin = Join-Path $toolRoot 'src\TiaPortalSourceBridge\bin\Release\net48'
foreach ($file in @('TiaPortalSourceBridge.exe','S7OnlineWorker.exe')) {
    Copy-Item -LiteralPath (Join-Path $sourceBin $file) -Destination $binRoot
}
Copy-Item -LiteralPath (Join-Path $toolRoot 'templates') -Destination (Join-Path $binRoot 'templates') -Recurse
foreach ($file in @('OpenTool.bat','StartMcp.bat','StartTiaMcp.bat','双击这里打开.bat','TIA源码工具.bat',
    'TrustThisTool.bat','TrustThisTool.ps1','Build-ProjectIndex.ps1','BuildProjectIndex.cmd','建立工程索引.bat',
    'Invoke-Online.ps1','BATCH_XML_IMPORT.md','CAPABILITIES.md','ONLINE_README.md','PROJECT_INDEX.md','MULTI_PROJECT.md')) {
    $text = [IO.File]::ReadAllText((Join-Path $toolRoot $file))
    $text = $text.Replace('src\TiaPortalSourceBridge\bin\Release\net48\','bin\').Replace('online-bin\','bin\')
    Write-PackageText $file $text
}
# Missing packaged binaries should produce a useful error rather than attempting a source build.
$open = @'
@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0bin\TiaPortalSourceBridge.exe" (
  echo Missing program. Extract the complete ZIP first.
  pause
  exit /b 1
)
if "%~1"=="" (
  "%~dp0bin\TiaPortalSourceBridge.exe" interactive --config "%~dp0config.json"
) else (
  "%~dp0bin\TiaPortalSourceBridge.exe" interactive --project "%~f1" --config "%~dp0config.json"
)
pause
'@
Write-PackageText 'OpenTool.bat' $open
Write-PackageText 'StartOnlineMcp.bat' @'
@echo off
cd /d "%~dp0"
"%~dp0bin\TiaPortalSourceBridge.exe" online-host --config "%~dp0mcp-config.json"
pause
'@
Write-PackageText 'StartMcpClient.bat' @'
@echo off
cd /d "%~dp0"
"%~dp0bin\TiaPortalSourceBridge.exe" mcp --config "%~dp0mcp-config.json"
'@
$config = [ordered]@{tiaPortalVersion='V17';siemensEngineeringDll='';connectionMode='AttachOnly';withUserInterface=$true;
    saveProjectAfterImport=$false;compileBeforeExport=$false;includeSystemBlocks=$false;continueOnError=$true;allowProjectUpgrade=$false;
    sourceDirectory='.\plc-src'} | ConvertTo-Json
Write-PackageText 'config.json' $config
Write-PackageText 'mcp-config.json' $config
Write-PackageText 'mcp-config-v21.json' ($config.Replace('V17','V21'))
Write-PackageText '使用说明.txt' @'
TIA Portal Source Bridge 精简迁移包（V17，2026-10-29）

解压整个文件夹后使用，无需编译。保留了当前更新的主程序、在线辅助程序、全部运行模板、索引和批量导入能力。
未包含源码、测试文件、工程文件、工程导出、历史备份、日志、本机最近工程记录和 Siemens DLL。

目标电脑准备：
1. Windows 64 位，安装 TIA Portal V17 及对应 Openness、.NET Framework 4.8。
2. 将使用者加入 Siemens TIA Openness 本地用户组；变更后注销并重新登录。
3. 先用 TIA 打开目标工程，再双击“双击这里打开.bat”；也可把工程文件拖到该入口。
4. 首次连接如出现 TIA Openness 访问提示，按需允许。TrustThisTool.bat 可用于登记解压路径下程序的信任，需要管理员权限。

入口：
- 双击这里打开.bat：交互式工程工具。
- StartTiaMcp.bat：启动工程 MCP 网关。
- StartMcpClient.bat：标准输入输出 MCP 入口（供支持启动脚本的客户端调用）。
- 建立工程索引.bat：选择工程并建立索引。
- StartOnlineMcp.bat：在线功能网关。

MCP 客户端也可以直接配置：
command = 解压目录\bin\TiaPortalSourceBridge.exe
args = ["mcp", "--config", "解压目录\mcp-config.json"]
请将“解压目录”替换成目标电脑真实绝对路径。不要在压缩包内直接运行。

默认配置为 V17、仅连接已打开工程、不自动保存，不自动升级工程。连接时传入目标电脑上的 project_path。
迁移工具不会迁移原 TIA 工程。原工程和项目备份需要另行拷贝。
本包已包含 GRAPH 换行修复，以及多个现有 XML 程序块统一备份、批量导入、一次编译与自动回读比对；详见 BATCH_XML_IMPORT.md。
使用在线功能时需要目标电脑上的 Siemens 通信组件；现场连接参数应按目标设备配置。

不要删除 bin\templates。若文件夹移动、主程序更新，原有信任记录可能需要重新登记。
manifest-sha256.json 记录包内文件校验值。
'@
# Keep the batch launchers in legacy cmd.exe's native ASCII encoding.
foreach ($batchFile in Get-ChildItem -LiteralPath $packageRoot -Filter *.bat -File) {
    $contents = [IO.File]::ReadAllText($batchFile.FullName)
    [IO.File]::WriteAllText($batchFile.FullName, $contents, [Text.Encoding]::ASCII)
}
$manifest = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($packageRoot.Length+1);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
Write-PackageText 'manifest-sha256.json' (ConvertTo-Json -InputObject $manifest -Depth 4)
$zipPath = $packageRoot + '.zip'
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
[pscustomobject]@{directory=$packageRoot;zip=$zipPath;file_count=$manifest.Count+1;zip_bytes=(Get-Item -LiteralPath $zipPath).Length;
    unpacked_bytes=(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Measure-Object Length -Sum).Sum} | ConvertTo-Json
