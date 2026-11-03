param([string]$Fixture = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$output = Join-Path $root 'artifacts\index-checks'
New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($edition in @('src\TiaPortalSourceBridge', 'engines\v21\src\TiaPortalSourceBridge')) {
    $files = @(Get-ChildItem (Join-Path $root $edition) -Filter '*.cs' -Recurse | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object FullName)
    $files += @(Get-ChildItem (Join-Path $root 'shared') -Filter '*.cs' -Recurse | ForEach-Object FullName)
    $references = @('/reference:System.dll','/reference:System.Core.dll','/reference:System.Xml.dll','/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll',('/link:' + (Join-Path $root 'build-support\uia\Interop.UIAutomationClient.dll')))
    & $compiler /nologo /utf8output /codepage:65001 /target:exe /platform:x64 /optimize+ /main:IndexChecks "/out:$output\IndexChecks.exe" @references @files "$PSScriptRoot\IndexChecks.cs"
    if ($LASTEXITCODE -ne 0) { throw "Index checks build failed: $edition" }
    if ($Fixture) { & "$output\IndexChecks.exe" $Fixture } else { & "$output\IndexChecks.exe" }
    if ($LASTEXITCODE -ne 0) { throw "Index checks failed: $edition" }
}
foreach ($file in @('Build-ProjectIndex.ps1', 'engines\v21\Build-ProjectIndex.ps1')) {
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $root $file), [ref]$tokens, [ref]$errors)
    if ($errors) { throw "Index CLI syntax error: $errors" }
}
