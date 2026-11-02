$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'build-support\browser-lifetime-checks'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$files = @(Get-ChildItem (Join-Path $root 'src\TiaPortalSourceBridge') -Filter '*.cs' -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object FullName)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $outDir 'BrowserLifetimeChecks.exe'
& $compiler /nologo /utf8output /codepage:65001 /target:exe /platform:x64 /main:BrowserLifetimeChecks ('/out:' + $testExe) /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ('/link:' + (Join-Path $root 'build-support\uia\Interop.UIAutomationClient.dll')) @files (Join-Path $PSScriptRoot 'BrowserLifetimeChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Browser lifetime checks failed to compile' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Browser lifetime checks failed' }
