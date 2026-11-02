param([switch]$NoTrust)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$engineRoot = Join-Path $root 'engines\v21'
$stage = Join-Path $root ('build-support\merged-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
function Publish-Exe([string]$Source, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) {
        # Renaming a loaded executable preserves the running session until it exits.
        Move-Item -LiteralPath $Destination -Destination ($Destination + '.previous-' + [Guid]::NewGuid().ToString('N'))
    }
    Copy-Item -LiteralPath $Source -Destination $Destination
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $engineRoot 'build-online.ps1') -OutputDirectory (Join-Path $stage 'v21') -NoSync
if ($LASTEXITCODE -ne 0) { throw 'V21 engine build failed' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build-online.ps1') -OutputDirectory (Join-Path $stage 'main') -NoSync
if ($LASTEXITCODE -ne 0) { throw 'Unified entry build failed' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\Run-Checks.ps1') -MainExecutable (Join-Path $stage 'main\TiaPortalSourceBridge.exe') -V21Executable (Join-Path $stage 'v21\TiaPortalSourceBridge.exe')
if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed; existing binaries have not been replaced.' }
foreach ($edition in @('v21', 'main')) {
    $destinationRoot = if ($edition -eq 'v21') { $engineRoot } else { $root }
    foreach ($relative in @('src\TiaPortalSourceBridge\bin\Release\net48', 'online-bin')) {
        $destination = Join-Path $destinationRoot $relative
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        foreach ($file in @('TiaPortalSourceBridge.exe','S7OnlineWorker.exe')) {
            Publish-Exe (Join-Path $stage "$edition\$file") (Join-Path $destination $file)
        }
        Copy-Item -LiteralPath (Join-Path $destinationRoot 'templates') -Destination $destination -Recurse -Force
        if ($edition -eq 'main') { Copy-Item -LiteralPath (Join-Path $root 'web') -Destination $destination -Recurse -Force }
    }
}
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = Join-Path $engineRoot 'src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
$info.Arguments = 'mcp --config "' + (Join-Path $engineRoot 'mcp-config.json') + '"'
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$p = [Diagnostics.Process]::Start($info)
try {
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}')
    $p.StandardInput.Flush()
    $read = $p.StandardOutput.ReadLineAsync()
    if (-not $read.Wait(15000)) { throw 'V21 catalog timed out' }
    $catalog = $read.Result | ConvertFrom-Json
    if ($catalog.error -or -not $catalog.result.tools) { throw 'Invalid V21 catalog' }
    [IO.File]::WriteAllText((Join-Path $engineRoot 'tool-catalog.json'), (ConvertTo-Json -InputObject @($catalog.result.tools) -Depth 60), [Text.UTF8Encoding]::new($false))
} finally { $p.StandardInput.Close(); [void]$p.WaitForExit(3000); $p.Dispose() }
if (-not $NoTrust) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'TrustThisTool.ps1') -QuietIfNotAdmin
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $engineRoot 'TrustThisTool.ps1') -QuietIfNotAdmin
}
# Clean build leftovers: staging dirs and renamed old executables (locked ones are skipped; removed on the next build).
Get-ChildItem -LiteralPath (Join-Path $root 'build-support') -Directory -Filter 'merged-*' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.previous-*' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(_archive)\\' } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
Write-Output 'Unified V17/V21 build complete. Start from this folder only.'
