$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $env:TEMP ('tia-cache-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts') -Force | Out-Null
$scriptPath = Join-Path $fixture 'scripts\Clear-BuildCache.ps1'
Copy-Item -LiteralPath (Join-Path $root 'scripts\Clear-BuildCache.ps1') -Destination $scriptPath
$candidate = Join-Path $fixture 'artifacts\io-checks'
$protected = Join-Path $fixture 'block-backups'
foreach ($dir in @($candidate,$protected,(Join-Path $fixture '.work\engineering-draft'))) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $dir 'sample.txt') -Value 'fixture'
}
$report = (& $scriptPath -MinAgeDays 0 | ConvertFrom-Json)
if ($report.applied -or $report.items.Count -ne 1 -or $report.items[0].status -ne 'preview' -or -not (Test-Path $candidate)) { throw 'Preview changed files or included protected data.' }
$report = (& $scriptPath -Apply | ConvertFrom-Json)
if ($report.items[0].status -ne 'skipped' -or -not (Test-Path $candidate)) { throw 'Recent files must be retained.' }
$report = (& $scriptPath -Apply -MinAgeDays 0 | ConvertFrom-Json)
if ($report.items[0].status -ne 'removed' -or (Test-Path $candidate) -or -not (Test-Path $protected)) { throw 'Cleanup scope failed.' }
# A junction must never cause enumeration/deletion outside the allowlisted output.
New-Item -ItemType Junction -Path $candidate -Target $protected | Out-Null
$report = (& $scriptPath -Apply -MinAgeDays 0 | ConvertFrom-Json)
if ($report.items[0].status -ne 'skipped' -or -not (Test-Path (Join-Path $protected 'sample.txt'))) { throw 'Linked output was not protected.' }
Write-Output 'PASS: cache preview, retention, explicit removal, protected engineering paths and junction rejection.'
