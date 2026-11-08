param([string]$CachedProject, [string]$Plc, [string]$CandidateOutput)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts\cylinder-apply-checks'
New-Item -ItemType Directory -Force $output | Out-Null
$files = @(Get-ChildItem (Join-Path $root 'src\TiaPortalSourceBridge') -Filter '*.cs' -Recurse | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object FullName)
$files += @(Get-ChildItem (Join-Path $root 'shared') -Filter '*.cs' -Recurse | ForEach-Object FullName)
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$references = @('/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Web.Extensions.dll','/r:System.Windows.Forms.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',('/link:' + (Join-Path $root 'build-support\uia\Interop.UIAutomationClient.dll')))
& $compiler /nologo /utf8output /codepage:65001 /target:exe /platform:x64 /main:CylinderApplyChecks "/out:$output\CylinderApplyChecks.exe" @references @files "$PSScriptRoot\CylinderApplyChecks.cs"
if ($LASTEXITCODE -ne 0) { throw 'Cylinder apply checks failed to compile' }
if ($CachedProject) { & "$output\CylinderApplyChecks.exe" $CachedProject $Plc $CandidateOutput } else { & "$output\CylinderApplyChecks.exe" }
if ($LASTEXITCODE -ne 0) { throw 'Cylinder apply checks failed' }
