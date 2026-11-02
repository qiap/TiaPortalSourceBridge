param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'artifacts\process-library-3.2' }
$outputDir = [IO.Path]::GetFullPath($OutputDirectory)
$releaseDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'src\TiaPortalSourceBridge\bin\Release\net48'))
$onlineDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'online-bin'))
if ($outputDir -eq $releaseDir -or $outputDir -eq $onlineDir) { throw 'Use a candidate directory, not an installed runtime directory.' }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-UiaInterop.ps1') -Quiet
if ($LASTEXITCODE -ne 0) { throw 'UIA interop build failed' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem (Join-Path $PSScriptRoot 'src\TiaPortalSourceBridge') -Filter '*.cs' -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object FullName)
& $compiler /nologo /utf8output /codepage:65001 /target:exe /platform:x64 /optimize+ ('/out:' + (Join-Path $outputDir 'TiaPortalSourceBridge.exe')) /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ('/link:' + (Join-Path $PSScriptRoot 'build-support\uia\Interop.UIAutomationClient.dll')) @sources
if ($LASTEXITCODE -ne 0) { throw 'Candidate build failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'templates') -Destination $outputDir -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'mcp-config.json') -Destination $outputDir -Force
$worker = Join-Path $releaseDir 'S7OnlineWorker.exe'
if (Test-Path -LiteralPath $worker) { Copy-Item -LiteralPath $worker -Destination $outputDir -Force }
Write-Output ('Candidate built: ' + $outputDir)
