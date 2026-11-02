param([string]$OutputDirectory, [switch]$NoSync)
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'online-bin' }
$outputDir = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-UiaInterop.ps1') -Quiet
$uiaInterop = Join-Path $PSScriptRoot 'build-support\uia\Interop.UIAutomationClient.dll'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = Get-ChildItem (Join-Path $PSScriptRoot 'src\TiaPortalSourceBridge') -Filter '*.cs' -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object FullName

& $compiler /nologo /utf8output /codepage:65001 /target:exe /platform:x64 /optimize+ ('/out:' + (Join-Path $outputDir 'TiaPortalSourceBridge.exe')) /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ('/link:' + $uiaInterop) @sources
if ($LASTEXITCODE -ne 0) { throw 'MCP V21 build failed' }

$workerSources = @((Join-Path $PSScriptRoot 'online-helper\Program.cs')) + @(Get-ChildItem (Join-Path $PSScriptRoot 'online-helper\vendor') -Filter '*.cs' | ForEach-Object FullName)
& $compiler /nologo /utf8output /codepage:65001 /target:exe /platform:x86 /optimize+ ('/out:' + (Join-Path $outputDir 'S7OnlineWorker.exe')) /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll @workerSources
if ($LASTEXITCODE -ne 0) { throw 'S7ONLINE worker build failed' }

Copy-Item (Join-Path $PSScriptRoot 'templates') $outputDir -Recurse -Force
$releaseDir = Join-Path $PSScriptRoot 'src\TiaPortalSourceBridge\bin\Release\net48'
if (-not $NoSync -and ([IO.Path]::GetFullPath($releaseDir)) -ne $outputDir) {
    New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
    Copy-Item (Join-Path $outputDir 'TiaPortalSourceBridge.exe') (Join-Path $releaseDir 'TiaPortalSourceBridge.exe') -Force
    Copy-Item (Join-Path $outputDir 'S7OnlineWorker.exe') (Join-Path $releaseDir 'S7OnlineWorker.exe') -Force
    Copy-Item (Join-Path $PSScriptRoot 'templates') $releaseDir -Recurse -Force
}
Write-Output ('Build complete: ' + $outputDir)
