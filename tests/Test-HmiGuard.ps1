$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$output = Join-Path $root 'artifacts\hmi-guard-checks'
New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($edition in @('src\TiaPortalSourceBridge', 'engines\v21\src\TiaPortalSourceBridge')) {
    $files = @(Get-ChildItem (Join-Path $root $edition) -Filter '*.cs' -Recurse | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object FullName)
    $files += @(Get-ChildItem (Join-Path $root 'shared') -Filter '*.cs' -Recurse | ForEach-Object FullName)
    $references = @('/reference:System.dll','/reference:System.Core.dll','/reference:System.Xml.dll','/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll',('/link:' + (Join-Path $root 'build-support\uia\Interop.UIAutomationClient.dll')))
    & $compiler /nologo /utf8output /codepage:65001 /target:exe /platform:x64 /optimize+ /main:HmiGuardChecks "/out:$output\HmiGuardChecks.exe" @references @files "$PSScriptRoot\HmiGuardChecks.cs"
    if ($LASTEXITCODE -ne 0) { throw "HMI guard build failed: $edition" }
    & "$output\HmiGuardChecks.exe" $root
    if ($LASTEXITCODE -ne 0) { throw "HMI guard checks failed: $edition" }
}
