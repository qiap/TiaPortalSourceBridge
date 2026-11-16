param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root ('.work\installer-' + [Guid]::NewGuid().ToString('N'))
$main = Join-Path $stage 'main'
$performance = Join-Path $stage 'performance'
$clear = Join-Path $root 'scripts\Clear-InstallerStage.ps1'
$checks = 0
function Check([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
    $script:checks++
}
function CheckTree([string]$Source, [string]$Destination) {
    $files = @(Get-ChildItem -LiteralPath $Source -File -Recurse)
    Check (@(Get-ChildItem -LiteralPath $Destination -File -Recurse).Count -eq $files.Count) 'Copied tree contains missing or extra files.'
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($Source.Length).TrimStart('\')
        $copy = Join-Path $Destination $relative
        Check ((Test-Path -LiteralPath $copy) -and
            (Get-FileHash -LiteralPath $copy).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) "Asset is stale or missing: $relative"
    }
}
New-Item -ItemType Directory -Path $main -Force | Out-Null
for ($iteration = 0; $iteration -lt 2; $iteration++) {
    if ($iteration -eq 1) {
        # A subsequent source build must replace an older local UI, not leave it in the runnable directory.
        [IO.File]::WriteAllText((Join-Path $main 'web\index.html'), 'outdated UI')
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build-online.ps1') -OutputDirectory $main -NoSync
    if ($LASTEXITCODE -ne 0) { throw 'Asset-check build failed' }
    CheckTree (Join-Path $root 'web') (Join-Path $main 'web')
    CheckTree (Join-Path $root 'templates') (Join-Path $main 'templates')
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-Performance.ps1') -Output $performance
    if ($LASTEXITCODE -ne 0) { throw 'Repeated performance run failed' }
    CheckTree (Join-Path $root 'templates') (Join-Path $performance 'templates')
}
# Cleanup must reject the repository and similarly named nested directories.
foreach ($invalid in @($root, (Join-Path $stage ('installer-' + [Guid]::NewGuid().ToString('N'))))) {
    if (-not (Test-Path -LiteralPath $invalid)) { New-Item -ItemType Directory -Path $invalid | Out-Null }
    $rejected = $false
    try { & $clear -Stage $invalid } catch { $rejected = $true }
    Check $rejected 'Staging cleanup accepted an invalid scope.'
    Check (Test-Path -LiteralPath $invalid) 'Rejected cleanup removed the target.'
}
$other = Join-Path $root ('.work\installer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $other | Out-Null
[IO.File]::WriteAllText((Join-Path $other 'sentinel.txt'), 'another packaging run')
& $clear -Stage $stage
Check (-not (Test-Path -LiteralPath $stage)) 'Successful build staging was not removed.'
Check (Test-Path -LiteralPath (Join-Path $other 'sentinel.txt')) 'Cleanup affected another build.'
& $clear -Stage $other
Write-Output "PASS: $checks repeated build asset and confined staging cleanup checks; no engineering connection."
