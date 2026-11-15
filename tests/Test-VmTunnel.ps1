param([string]$MainExecutable)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}

# 1. Verify LocalPipeHelper (PIPE_REJECT_REMOTE_CLIENTS + no NetworkSid deny) in both async and sync modes.
$outDir = Join-Path $root 'build-support\vm-pipe-checks'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $outDir 'VmPipeChecks.exe'
& $compiler /nologo /utf8output /codepage:65001 /target:exe /platform:x64 /main:VmPipeChecks ('/out:' + $testExe) `
    (Join-Path $root 'shared\Mcp\LocalPipeHelper.cs') (Join-Path $PSScriptRoot 'VmPipeChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'VmPipeChecks failed to compile' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'VmPipeChecks failed' }
$checks++

# 2. Verify Connect-VmDashboard.ps1 DryRun, saved config persistence, MCP snippet, and matching LocalPort.
$tempDir = Join-Path $env:TEMP ('tia-vm-tunnel-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
try {
    $connFile = Join-Path $tempDir 'vm-connection.json'
    $connectScript = Join-Path $root 'scripts\Connect-VmDashboard.ps1'

    $dryOut = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $connectScript `
        -Target 'tester@192.168.56.101' -VmToolDir 'D:\TIA\Bridge' -RemotePort 18985 -PrintMcpConfig `
        -ConnectionFile $connFile -DryRun 2>&1 | Out-String
    Check ($LASTEXITCODE -eq 0) 'Connect-VmDashboard DryRun exited 0'
    Check ($dryOut.Contains('-L 127.0.0.1:18985:127.0.0.1:18985')) 'Default LocalPort matches RemotePort in SSH args'
    Check ($dryOut.Contains('http://localhost:18985/')) 'Dashboard URL uses matching localhost port'
    Check ($dryOut.Contains('mcp-client') -and $dryOut.Contains('tia-portal-vm')) 'MCP snippet includes ssh mcp-client entry'

    $savedJson = '{"target":"saveduser@10.0.2.15","sshPort":2222,"remotePort":18990,"localPort":18990,"vmToolDir":"C:\\Tools\\Tia"}'
    [IO.File]::WriteAllText($connFile, $savedJson, [Text.UTF8Encoding]::new($false))
    $reloadOut = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $connectScript `
        -ConnectionFile $connFile -DryRun 2>&1 | Out-String
    Check ($LASTEXITCODE -eq 0) 'Connect-VmDashboard DryRun reloads saved config'
    Check ($reloadOut.Contains('-p 2222 -L 127.0.0.1:18990:127.0.0.1:18990 saveduser@10.0.2.15')) 'Reloaded SSH args match saved JSON'

    # Busy local port check should fail fast without launching ssh.exe.
    $busyListener = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $busyListener.Start()
    $busyPort = ([Net.IPEndPoint]$busyListener.LocalEndpoint).Port
    try {
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $busyOut = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $connectScript `
            -Target 'tester@192.168.56.101' -RemotePort $busyPort -NoSave 2>&1 | Out-String
        $busyExit = $LASTEXITCODE
        $ErrorActionPreference = $prevEap
        Check ($busyExit -ne 0 -and $busyOut.Contains("$busyPort") -and -not $busyOut.Contains('192.168.56.101')) 'Occupied local port fails fast cleanly without invoking ssh'
    } finally {
        $busyListener.Stop()
    }

    # 3. Verify Start-VmDashboard.ps1 -CheckOnly creates placeholder config when missing and tolerates no TIA Portal.
    $vmCfg = Join-Path $tempDir 'vm-config.json'
    $startScript = Join-Path $root 'scripts\Start-VmDashboard.ps1'
    $vmCheckOut = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $startScript `
        -Config $vmCfg -Port 18992 -CheckOnly 2>&1 | Out-String
    Check ($LASTEXITCODE -eq 0) 'Start-VmDashboard -CheckOnly exited 0'
    Check (Test-Path -LiteralPath $vmCfg) 'Start-VmDashboard creates default config.json on fresh VM'
    Check ($vmCheckOut.Contains('127.0.0.1:18992')) 'Start-VmDashboard prints loopback dashboard port'

    $vmStartBat = Join-Path $root ([string]::Concat([char]0x865A,[char]0x62DF,[char]0x673A,[char]0x542F,[char]0x52A8,[char]0x770B,[char]0x677F,'.bat'))
    $hostConnectBat = Join-Path $root ([string]::Concat([char]0x8FDE,[char]0x63A5,[char]0x865A,[char]0x62DF,[char]0x673A,[char]0x770B,[char]0x677F,'.bat'))
    $vmSshBat = Join-Path $root ([string]::Concat([char]0x865A,[char]0x62DF,[char]0x673A,[char]0x542F,[char]0x7528,'SSH.bat'))
    Check (Test-Path -LiteralPath $vmStartBat) 'Chinese VM start launcher exists'
    Check (Test-Path -LiteralPath $hostConnectBat) 'Chinese host connect launcher exists'
    Check (Test-Path -LiteralPath $vmSshBat) 'Chinese VM SSH setup launcher exists'

    $batStartOut = & cmd.exe /d /c ('""' + $vmStartBat + '" -CheckOnly -Config "' + $vmCfg + '""') 2>&1 | Out-String
    Check ($LASTEXITCODE -eq 0 -and $batStartOut.Contains('127.0.0.1:18980')) 'Chinese VM start .bat runs cleanly'
    $batConnectOut = & cmd.exe /d /c ('""' + $hostConnectBat + '" -Target tester@192.168.56.101 -NoSave -DryRun"') 2>&1 | Out-String
    Check ($LASTEXITCODE -eq 0 -and $batConnectOut.Contains('-L 127.0.0.1:18980:127.0.0.1:18980')) 'Chinese host connect .bat runs cleanly'

    # 4. Verify real headless dashboard (--no-browser) works without TIA Portal installed and shuts down cleanly.
    if (-not $MainExecutable) {
        $candidate = Join-Path $root '.work\regression-build\main\TiaPortalSourceBridge.exe'
        if (Test-Path -LiteralPath $candidate) { $MainExecutable = $candidate }
        else { $MainExecutable = Join-Path $root 'src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe' }
    }
    if (Test-Path -LiteralPath $MainExecutable) {
        $probe = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
        $probe.Start()
        $webPort = ([Net.IPEndPoint]$probe.LocalEndpoint).Port
        $probe.Stop()

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $MainExecutable
        $psi.Arguments = ('web --no-browser --port {0} --config "{1}"' -f $webPort, $vmCfg)
        $psi.WorkingDirectory = $root
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $proc = [Diagnostics.Process]::Start($psi)
        $base = "http://localhost:$webPort"
        $up = $false
        try {
            for ($i = 0; $i -lt 50; $i++) {
                try {
                    $build = Invoke-RestMethod "$base/api/build" -TimeoutSec 1
                    if ($build.stamp) { $up = $true; break }
                } catch {}
                Start-Sleep -Milliseconds 200
            }
            Check $up 'Headless dashboard starts without TIA Portal'
            # Discovery runs in the background; status must report pending without waiting for Openness.
            $statusClock = [Diagnostics.Stopwatch]::StartNew()
            $status = Invoke-RestMethod "$base/api/status" -TimeoutSec 5
            $statusClock.Stop()
            Write-Output ('Dashboard cold status: {0} ms' -f $statusClock.ElapsedMilliseconds)
            Check ([bool]$status.tool_name -and [bool]$status.tool_version) 'Dashboard /api/status succeeds without TIA Portal'
            Check ($status.portal_discovery.state -in @('pending','ready','failed')) 'Discovery state is explicit and status does not block behind it'
        } finally {
            if ($up) {
                try { $null = Invoke-RestMethod "$base/api/shutdown" -Method Post -TimeoutSec 5 } catch {}
            }
            if (-not $proc.WaitForExit(10000)) {
                try { $proc.Kill() } catch {}
            }
            $proc.Dispose()
        }
    }
} finally {
    if (Test-Path -LiteralPath $tempDir) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Output "PASS: $checks VM SSH tunnel and headless dashboard checks."
