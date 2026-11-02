param([switch]$Quiet)
$ErrorActionPreference='Stop'
$uiaOutput=Join-Path $PSScriptRoot 'build-support\uia'
$uiaDll=Join-Path $uiaOutput 'Interop.UIAutomationClient.dll'
$uiaSource=Join-Path $PSScriptRoot 'tests\GenerateUiaInterop.cs'
$uiaSystem=Join-Path $env:WINDIR 'System32\UIAutomationCore.dll'
if((Test-Path -LiteralPath $uiaDll) -and (Get-Item -LiteralPath $uiaDll).LastWriteTimeUtc -gt (Get-Item -LiteralPath $uiaSource).LastWriteTimeUtc -and (Get-Item -LiteralPath $uiaDll).LastWriteTimeUtc -gt (Get-Item -LiteralPath $uiaSystem).LastWriteTimeUtc){if(-not $Quiet){$uiaDll};return}
New-Item -ItemType Directory -Path $uiaOutput -Force|Out-Null
$uiaCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$uiaGenerator=Join-Path $uiaOutput 'GenerateUiaInterop.exe'
& $uiaCompiler /nologo /target:exe ('/out:'+$uiaGenerator) $uiaSource
if($LASTEXITCODE -ne 0){throw 'UI Automation interface generator build failed'}
Push-Location $uiaOutput
try { & $uiaGenerator $uiaDll | Out-Null;if($LASTEXITCODE -ne 0){throw 'UI Automation interface generation failed'} } finally {Pop-Location}
if(-not $Quiet){$uiaDll}
