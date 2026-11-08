$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'build-support\project-operation-guard-checks'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $outDir 'ProjectOperationGuardChecks.exe'
& $compiler /nologo /utf8output /codepage:65001 /target:exe /platform:x64 ('/out:' + $testExe) /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll (Join-Path $root 'shared\Mcp\ProjectOperationGuard.cs') (Join-Path $PSScriptRoot 'ProjectOperationGuardChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Project operation guard checks failed to compile' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Project operation guard checks failed' }
