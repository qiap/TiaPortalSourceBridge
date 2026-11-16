param([switch]$Apply, [ValidateRange(0,3650)][int]$MinAgeDays = 7)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
# Only reproducible, explicitly named outputs. Never scan engineering recovery/history as candidates.
$relative = @('.work\regression-build', 'artifacts\io-checks', 'artifacts\index-checks',
    'artifacts\hmi-guard-checks', 'artifacts\cylinder-apply-checks',
    'artifacts\cylinder-alarm-reset-checks', 'artifacts\performance-optimized')
function Assert-NoLinks([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked path: $Path" }
    if ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $Path -Force) { Assert-NoLinks $child.FullName }
    }
}
$work = Join-Path $root '.work'
if ((Test-Path -LiteralPath $work) -and -not ((Get-Item -LiteralPath $work -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    $relative += @(Get-ChildItem -LiteralPath $work -Directory -Force | Where-Object { $_.Name -match '^installer-[a-f0-9]{32}$' } | ForEach-Object { '.work\' + $_.Name })
}
$tracked = @()
if (Test-Path -LiteralPath (Join-Path $root '.git')) {
    $tracked = @(git -C $root ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check tracked files; cleanup refused.' }
}
$results = @()
foreach ($rel in $relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $rel))
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $row = [ordered]@{path=$path; bytes=0; status='preview'; reason=''}
    try {
        if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Outside workspace.' }
        $ancestor = $path
        while ($ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked ancestor.' }
            $ancestor = Split-Path -Parent $ancestor
        }
        Assert-NoLinks $path
        $prefix = $rel.Replace('\','/') + '/'
        if (@($tracked | Where-Object { $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }).Count) { throw 'Contains tracked source files.' }
        $files = @(Get-ChildItem -LiteralPath $path -Recurse -Force -File)
        $row.bytes = ($files | Measure-Object Length -Sum).Sum
        $newest = @((Get-Item -LiteralPath $path).LastWriteTimeUtc) + @($files | ForEach-Object LastWriteTimeUtc) | Sort-Object -Descending | Select-Object -First 1
        if ($newest -gt [DateTime]::UtcNow.AddDays(-$MinAgeDays)) { throw 'Recently modified; outside retention threshold.' }
        if ($Apply) {
            # Check again at deletion time. Unreadable process information fails closed.
            $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop)
            if (@($processes | Where-Object { ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($path + '\', [StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($path, [StringComparison]::OrdinalIgnoreCase) -ge 0) }).Count) { throw 'In use by a running process.' }
            Assert-NoLinks $path
            Remove-Item -LiteralPath $path -Recurse -Force
            $row.status = 'removed'
        }
    } catch { $row.status = 'skipped'; $row.reason = $_.Exception.Message }
    $results += [pscustomobject]$row
}
[pscustomobject]@{applied=[bool]$Apply; min_age_days=$MinAgeDays; items=$results} | ConvertTo-Json -Depth 5
