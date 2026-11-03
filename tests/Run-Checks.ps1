param([string]$MainExecutable, [string]$V21Executable)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $MainExecutable -or -not $V21Executable) {
    $stage = Join-Path $root '.work\regression-build'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build-online.ps1') -OutputDirectory (Join-Path $stage 'main') -NoSync
    if ($LASTEXITCODE -ne 0) { throw 'Main build failed' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'engines\v21\build-online.ps1') -OutputDirectory (Join-Path $stage 'v21') -NoSync
    if ($LASTEXITCODE -ne 0) { throw 'V21 build failed' }
    $MainExecutable = Join-Path $stage 'main\TiaPortalSourceBridge.exe'
    $V21Executable = Join-Path $stage 'v21\TiaPortalSourceBridge.exe'
}
# Shared components must never acquire a second independently maintained copy.
$shared = Join-Path $root 'shared'
foreach ($file in Get-ChildItem $shared -Recurse -Filter '*.cs') {
    $relative = $file.FullName.Substring($shared.Length + 1)
    foreach ($engine in @('src\TiaPortalSourceBridge', 'engines\v21\src\TiaPortalSourceBridge')) {
        if (Test-Path -LiteralPath (Join-Path (Join-Path $root $engine) $relative)) { throw ('Duplicate shared source: ' + $relative) }
    }
}
foreach ($test in @('Test-BrowserLifetime.ps1','Test-Reliability.ps1','Test-Performance.ps1','Test-Index.ps1','Test-HmiGuard.ps1','Test-Io.ps1')) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $test)
    if ($LASTEXITCODE -ne 0) { throw ($test + ' failed') }
}
& python (Join-Path $PSScriptRoot 'Test-VersionRouting.py') $MainExecutable $V21Executable
if ($LASTEXITCODE -ne 0) { throw 'Version routing checks failed' }
foreach ($test in @('Test-ProjectDropdown.cjs', 'Test-IoPanel.cjs')) {
    & node (Join-Path $PSScriptRoot $test)
    if ($LASTEXITCODE -ne 0) { throw ($test + ' failed') }
}
Write-Output 'PASS: all offline regression checks. No engineering connection or PLC writes.'
