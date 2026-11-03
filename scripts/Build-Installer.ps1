param(
    [string]$CompilerPath,
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '4.1.1'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $CompilerPath) {
    $candidates = @(
        (Join-Path $root '.work\installer-tools\inno\ISCC.exe'),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath)) {
    throw 'Install Inno Setup 6 (https://jrsoftware.org/isdl.php), or supply -CompilerPath.'
}
$stage = Join-Path $root ('.work\installer-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage "TiaPortalSourceBridge-$Version-Portable"
$output = Join-Path $root 'artifacts\installer'
New-Item -ItemType Directory -Force $payload,$output | Out-Null
$binRelative = 'src\TiaPortalSourceBridge\bin\Release\net48'
$utf8 = [Text.UTF8Encoding]::new($false)
foreach ($edition in @('main','v21')) {
    $sourceRoot = if ($edition -eq 'main') { $root } else { Join-Path $root 'engines\v21' }
    $dest = if ($edition -eq 'main') { $payload } else { Join-Path $payload 'engines\v21' }
    $bin = Join-Path $dest $binRelative
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $sourceRoot 'build-online.ps1') -OutputDirectory $bin -NoSync
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $edition" }
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'templates') -Destination $dest -Recurse
    foreach ($name in @('TrustThisTool.ps1','TrustThisTool.bat','Build-ProjectIndex.ps1')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $dest
    }
    $portal = if ($edition -eq 'main') { 'V17' } else { 'V21' }
    # A local placeholder satisfies config validation; it never names a customer's project.
    $config = [ordered]@{tiaPortalVersion=$portal;projectPath=('.\SelectProject.ap'+$portal.Substring(1));
        plcName='';sourceDirectory='.\plc-src';siemensEngineeringDll='';connectionMode='AttachOnly';
        withUserInterface=$true;saveProjectAfterImport=$false;compileBeforeExport=$false;
        includeSystemBlocks=$false;continueOnError=$true;allowProjectUpgrade=$false} | ConvertTo-Json
    foreach ($name in @('config.json','mcp-config.json')) { [IO.File]::WriteAllText((Join-Path $dest $name), $config, $utf8) }
}
Copy-Item -LiteralPath (Join-Path $payload 'engines\v21\mcp-config.json') -Destination (Join-Path $payload 'mcp-config-v21.json')
foreach ($name in @('web','app.ico','app.png','LaunchWeb.vbs','CreateShortcuts.vbs','StartMcp.bat','StartTiaMcp.bat','StartTiaMcpV21.bat',
    'StartOnlineMcp.bat','StopTiaMcp.bat','Invoke-Online.ps1','Invoke-HmiAlarms.ps1','BuildProjectIndex.cmd',
    'Start-AutoResetSimulation.ps1','Start-StandardSimulation.ps1')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $payload -Recurse
}
Get-ChildItem -LiteralPath $root -Filter '*.md' -File | Copy-Item -Destination $payload
Copy-Item -LiteralPath (Join-Path $root 'installer\INSTALL.txt') -Destination $payload
Copy-Item -LiteralPath (Join-Path $root 'installer\PORTABLE.txt') -Destination $payload
[IO.File]::WriteAllText((Join-Path $payload 'WebDashboard.cmd'), "@echo off`r`nwscript.exe `"%~dp0LaunchWeb.vbs`" %*`r`n", [Text.Encoding]::ASCII)
$main = Join-Path $payload "$binRelative\TiaPortalSourceBridge.exe"
$v21 = Join-Path $payload "engines\v21\$binRelative\TiaPortalSourceBridge.exe"
# Generate the catalog from the exact packaged engine without connecting to TIA.
$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $v21
$psi.Arguments = 'mcp --config "' + (Join-Path $payload 'engines\v21\mcp-config.json') + '"'
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true
$psi.StandardOutputEncoding = $utf8
$p = [Diagnostics.Process]::Start($psi)
try {
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}')
    $p.StandardInput.Flush()
    $read = $p.StandardOutput.ReadLineAsync()
    if (-not $read.Wait(15000)) { throw 'Catalog generation timed out' }
    $catalog = $read.Result | ConvertFrom-Json
    if ($catalog.error -or -not $catalog.result.tools) { throw 'Invalid V21 catalog' }
    [IO.File]::WriteAllText((Join-Path $payload 'engines\v21\tool-catalog.json'), (ConvertTo-Json -InputObject @($catalog.result.tools) -Depth 60), $utf8)
} finally {
    $p.StandardInput.Close()
    if (-not $p.WaitForExit(3000)) { $p.Kill() }
    $p.Dispose()
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\Run-Checks.ps1') -MainExecutable $main -V21Executable $v21
if ($LASTEXITCODE -ne 0) { throw 'Offline checks failed' }
& $CompilerPath "/DPayloadDir=$payload" "/DOutputDir=$output" "/DAppVersion=$Version" (Join-Path $root 'installer\Setup.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$exe = Join-Path $output "TiaPortalSourceBridge-$Version-Setup.exe"
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
[IO.File]::WriteAllText(($exe + '.sha256'), "$hash  $([IO.Path]::GetFileName($exe))`r`n", $utf8)
$zip = Join-Path $output "TiaPortalSourceBridge-$Version-Portable.zip"
Compress-Archive -LiteralPath $payload -DestinationPath $zip -CompressionLevel Optimal -Force
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($exe))`r`n$zipHash  $([IO.Path]::GetFileName($zip))`r`n", $utf8)
[pscustomobject]@{installer=$exe;sha256=$hash;portable=$zip;portable_sha256=$zipHash;payload=$payload} | ConvertTo-Json
