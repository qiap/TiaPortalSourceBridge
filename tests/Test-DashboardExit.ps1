$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
$baseUrl = 'http://localhost:18981'
if (Get-NetTCPConnection -LocalPort 18981 -State Listen -ErrorAction SilentlyContinue) { throw 'Test port 18981 is already in use.' }
$web = Start-Process -FilePath $exe -ArgumentList ('web --port 18981 --no-browser --hide-console --config "' + (Join-Path $root 'config.json') + '"') -WindowStyle Hidden -PassThru
function Lease($route, $id, $page) {
    Invoke-RestMethod -Method Post -Uri ($baseUrl + '/api/browser/' + $route) -ContentType 'application/json' -Body (@{client_id=$id;page_id=$page} | ConvertTo-Json -Compress) | Out-Null
}
try {
    $status = $null
    for ($i=0; $i -lt 40; $i++) {
        try { $status = Invoke-RestMethod ($baseUrl + '/api/status') -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $status) { throw 'Dashboard did not start.' }
    Lease heartbeat 'exit-test-tab-a' 'page-a'
    Lease heartbeat 'exit-test-tab-b' 'page-b'
    Lease release 'exit-test-tab-a' 'page-a'
    Start-Sleep -Seconds 5
    $status = Invoke-RestMethod ($baseUrl + '/api/status') -TimeoutSec 3
    if ($status.browser_clients -ne 1) { throw 'Closing one of two tabs lost the remaining connection.' }
    Lease heartbeat 'exit-test-tab-b' 'page-b-refresh'
    Lease release 'exit-test-tab-b' 'page-b'
    Start-Sleep -Seconds 5
    $status = Invoke-RestMethod ($baseUrl + '/api/status') -TimeoutSec 3
    if ($status.browser_clients -ne 1) { throw 'Old page release disconnected a refreshed tab.' }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Lease release 'exit-test-tab-b' 'page-b-refresh'
    if (-not $web.WaitForExit(20000)) { throw 'Dashboard did not exit after its final browser lease closed.' }
    for ($i=0; $i -lt 20; $i++) {
        $remaining = @(Get-CimInstance Win32_Process -Filter "Name='TiaPortalSourceBridge.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) })
        if ($remaining.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($remaining.Count) { throw ('Dashboard background process remains: ' + ($remaining.ProcessId -join ',')) }
    if (Get-NetTCPConnection -LocalPort 18981 -State Listen -ErrorAction SilentlyContinue) { throw 'Dashboard port remains open.' }
    $handle = [IO.File]::Open((Join-Path $root 'last-mcp-host.log'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $handle.Dispose()
    'PASS: multi-tab, refresh generation, final-tab shutdown, owned processes exited, port closed, log unlocked ({0:N1}s).' -f $timer.Elapsed.TotalSeconds
} finally {
    if (-not $web.HasExited) {
        try { Invoke-RestMethod -Method Post ($baseUrl + '/api/shutdown') -TimeoutSec 3 | Out-Null } catch { }
    }
    $web.Dispose()
}
