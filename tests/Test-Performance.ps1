param([string]$SourceRoot = "$PSScriptRoot\..\src\TiaPortalSourceBridge", [string]$Output = "$PSScriptRoot\..\artifacts\performance-optimized", [switch]$Baseline, [string]$GraphFixture = '')
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$outDir = (Resolve-Path -LiteralPath $Output).Path
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$files = @(Get-ChildItem -LiteralPath $source -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object { $_.FullName })
$references = @('/reference:System.dll','/reference:System.Core.dll','/reference:System.Xml.dll','/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll',("/link:" + (Resolve-Path "$PSScriptRoot\..\build-support\uia\Interop.UIAutomationClient.dll").Path))
& $compiler /nologo /utf8output /codepage:65001 /target:exe /platform:x64 /optimize+ "/out:$outDir\TiaPortalSourceBridge.exe" @references @files
if ($LASTEXITCODE -ne 0) { throw 'Application build failed' }
& $compiler /nologo /utf8output /codepage:65001 /target:exe /platform:x64 /optimize+ /main:PerformanceChecks "/out:$outDir\PerformanceChecks.exe" @references @files "$PSScriptRoot\PerformanceChecks.cs" "$PSScriptRoot\XmlBatchChecks.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
$mode = if ($Baseline) { 'baseline' } else { 'optimized' }
if ($GraphFixture) {
    & "$outDir\PerformanceChecks.exe" $mode $GraphFixture | Tee-Object -FilePath "$outDir\results.json"
} else {
    & "$outDir\PerformanceChecks.exe" $mode | Tee-Object -FilePath "$outDir\results.json"
}
if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed' }
Copy-Item "$PSScriptRoot\..\templates" "$outDir\templates" -Recurse -Force
