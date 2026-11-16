# Shared-folder discovery. Public records only; private keys stay on the host.
function Write-VmJson([string]$Path, $Value) {
    $dir = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    $temp = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temp -Destination $Path -Force
}

function Test-VmTcp([string]$Address, [int]$Port) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $pending = $client.BeginConnect($Address, $Port, $null, $null)
        if (-not $pending.AsyncWaitHandle.WaitOne(500)) { return $false }
        $client.EndConnect($pending)
        return $true
    } catch { return $false } finally { $client.Dispose() }
}

function Initialize-VmHostIdentity([string]$Root) {
    $access = Join-Path $Root 'vm-access'
    $recordPath = Join-Path $access 'host-identity.json'
    if (Test-Path -LiteralPath $recordPath) {
        $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
        if ($record.hostComputer -ne $env:COMPUTERNAME) { throw 'This shared tool is paired with another host. Use a separate tool folder for another host.' }
        if (-not (Test-Path -LiteralPath $record.identityFile)) { throw 'The paired host private key is missing. Restore the key before reconnecting.' }
        return $record
    }
    $keyDir = Join-Path $env:USERPROFILE '.ssh'
    [IO.Directory]::CreateDirectory($keyDir) | Out-Null
    $identity = Join-Path $keyDir ('tia_vm_' + [Guid]::NewGuid().ToString('N') + '_ed25519')
    $keygen = (Get-Command ssh-keygen.exe -ErrorAction Stop).Source
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $keygen
    $psi.Arguments = '-t ed25519 -f "' + $identity + '" -N "" -C tia-vm-host-access'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($psi)
    $null = $process.StandardOutput.ReadToEnd()
    $err = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw ('Could not create host key: ' + $err) }
    $process.Dispose()
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $identity /inheritance:r /grant:r ('*' + $sid + ':(F)') '*S-1-5-18:(F)' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect host private key.' }
    [IO.Directory]::CreateDirectory($access) | Out-Null
    Copy-Item -LiteralPath ($identity + '.pub') -Destination (Join-Path $access 'host_ed25519.pub')
    $fp = (& $keygen -lf ($identity + '.pub')) -split '\s+'
    if ($LASTEXITCODE -ne 0) { throw 'Could not read host key fingerprint.' }
    $record = [pscustomobject]@{ hostComputer=$env:COMPUTERNAME; identityFile=$identity; fingerprint=$fp[1] }
    Write-VmJson $recordPath $record
    Write-VmJson (Join-Path $access 'enrollment.json') $record
    return $record
}

function Save-VmHostPublicKeys([string]$Root, [string]$Fingerprint) {
    # Called during elevated first-time setup. Export public material only.
    $keys = @(Get-ChildItem -LiteralPath (Join-Path $env:ProgramData 'ssh') -Filter 'ssh_host_*_key.pub' -ErrorAction Stop |
        ForEach-Object { $parts = ([IO.File]::ReadAllText($_.FullName).Trim() -split '\s+'); $parts[0] + ' ' + $parts[1] })
    if (-not $keys.Count) { throw 'SSH host public keys are missing.' }
    foreach ($key in $keys) {
        if ($key -notmatch '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp\d+) [A-Za-z0-9+/=]+$') { throw 'Invalid SSH host public key.' }
    }
    Write-VmJson (Join-Path $Root 'vm-access\vm-host-public-keys.json') @{
        schema=1; vmComputer=$env:COMPUTERNAME; user=$env:USERNAME
        pairedFingerprint=$Fingerprint; hostKeys=$keys
    }
    return $keys
}

function Get-VmHostPublicKeys([string]$Root, [string]$Fingerprint) {
    $cachePath = Join-Path $Root 'vm-access\vm-host-public-keys.json'
    # The last successful endpoint also contains trusted public keys. Migrate it
    # for existing installations so a normal restart does not require elevation.
    foreach ($path in @($cachePath, (Join-Path $Root 'vm-access\vm-endpoint.json'))) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $record = Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json
        if ($record.schema -ne 1 -or $record.vmComputer -ne $env:COMPUTERNAME -or
            $record.user -ne $env:USERNAME -or $record.pairedFingerprint -ne $Fingerprint) { continue }
        $keys = @($record.hostKeys)
        if (-not $keys.Count) { continue }
        foreach ($key in $keys) {
            if ($key -notmatch '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp\d+) [A-Za-z0-9+/=]+$') { throw 'Invalid cached SSH host public key; run the VM launcher as administrator to refresh pairing.' }
        }
        if ($path -ne $cachePath) {
            Write-VmJson $cachePath @{ schema=1; vmComputer=$env:COMPUTERNAME; user=$env:USERNAME; pairedFingerprint=$Fingerprint; hostKeys=$keys }
        }
        return $keys
    }
    return @(Save-VmHostPublicKeys $Root $Fingerprint)
}

function Publish-VmEndpoint([string]$Root, [string]$Executable, [int]$Port, [string[]]$Addresses) {
    $access = Join-Path $Root 'vm-access'
    $enrollment = Get-Content -LiteralPath (Join-Path $access 'enrollment.json') -Raw | ConvertFrom-Json
    if ($env:COMPUTERNAME -eq $enrollment.hostComputer) { throw 'Start the VM launcher inside the VM, not on the host.' }
    $keys = @(Get-VmHostPublicKeys $Root $enrollment.fingerprint)
    $record = [ordered]@{
        schema=1; vmComputer=$env:COMPUTERNAME; user=$env:USERNAME; addresses=@($Addresses)
        sshPort=22; remotePort=$Port; executable=$Executable; toolDir=$Root; hostKeys=$keys
        pairedFingerprint=$enrollment.fingerprint; updatedUtc=[DateTime]::UtcNow.ToString('o')
    }
    Write-VmJson (Join-Path $access 'vm-endpoint.json') $record
}

function Resolve-VmEndpoint([string]$Root, $Identity) {
    $path = Join-Path $Root 'vm-access\vm-endpoint.json'
    if (-not (Test-Path -LiteralPath $path)) { throw 'Please start the VM dashboard once inside the VM, then double-click Connect on the host. No parameters are needed.' }
    $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($record.schema -ne 1 -or $record.pairedFingerprint -ne $Identity.fingerprint) { throw 'The VM has not authorized this host key yet. Restart the VM dashboard launcher once.' }
    if ($record.vmComputer -eq $env:COMPUTERNAME) { throw 'Use the Connect launcher on the host, not inside the VM.' }
    if ($record.user -notmatch '^[\w.\-]+$') { throw 'Invalid VM user in discovery record.' }
    foreach ($port in @($record.sshPort, $record.remotePort)) {
        if ($port -lt 1 -or $port -gt 65535) { throw 'Invalid port in VM discovery record.' }
    }
    $addresses = @($record.addresses | Where-Object {
        $ip = $null
        [Net.IPAddress]::TryParse($_, [ref]$ip) -and $ip.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and
        -not $_.StartsWith('127.') -and -not $_.StartsWith('169.254.') -and $_ -ne '0.0.0.0'
    })
    $reachable = @($addresses | Where-Object { Test-VmTcp $_ ([int]$record.sshPort) })
    if (-not $reachable.Count) { throw 'VM SSH is not reachable. Keep the VM running and start its dashboard; its current IP will be discovered automatically.' }
    $known = Join-Path (Split-Path -Parent $Identity.identityFile) 'tia_vm_known_hosts'
    $lines = @(foreach ($address in $addresses) {
        $hostName = if ($record.sshPort -eq 22) { $address } else { '[' + $address + ']:' + $record.sshPort }
        foreach ($key in $record.hostKeys) {
            if ($key -notmatch '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp\d+) [A-Za-z0-9+/=]+$') { throw 'Invalid SSH host public key in VM record.' }
            $hostName + ' ' + $key
        }
    })
    if (-not $lines.Count) { throw 'No trusted VM host keys were published.' }
    [IO.File]::WriteAllLines($known, $lines, [Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{ record=$record; target=($record.user + '@' + $reachable[0]); knownHosts=$known }
}
