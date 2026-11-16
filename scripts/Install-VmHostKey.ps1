# Install only the prepared host public key in the intended VM.
$ErrorActionPreference = 'Stop'
try {
    $root = Split-Path -Parent $PSScriptRoot
    $enrollment = Get-Content -LiteralPath (Join-Path $root 'vm-access\enrollment.json') -Raw | ConvertFrom-Json
    if ($env:COMPUTERNAME -eq $enrollment.hostComputer) { throw 'Run this installer INSIDE the VM, not on the host computer.' }
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Right-click this BAT and choose Run as administrator inside the VM.' }
    $sshd = Join-Path $env:WINDIR 'System32\OpenSSH\sshd.exe'
    if (-not (Test-Path -LiteralPath $sshd)) { throw 'Windows OpenSSH Server is not installed.' }
    $effective = @(& $sshd -T -C ('user=' + $env:USERNAME + ',host=localhost,addr=127.0.0.1') 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ('Cannot validate sshd configuration: ' + ($effective -join "`n")) }
    if ('pubkeyauthentication yes' -notin $effective) { throw 'Public-key authentication is disabled in sshd_config.' }
    $keySetting = @($effective | Where-Object { $_ -match '^authorizedkeysfile ' }) -join ''
    if ($keySetting -notmatch '(?i)^authorizedkeysfile __PROGRAMDATA__/ssh/administrators_authorized_keys\s*$') {
        throw ('Unexpected AuthorizedKeysFile configuration; nothing changed: ' + $keySetting)
    }
    $publicPath = Join-Path $root 'vm-access\host_ed25519.pub'
    $publicKey = [IO.File]::ReadAllText($publicPath).Trim()
    if ($publicKey -notmatch '^ssh-ed25519 [A-Za-z0-9+/=]+(?: [^\r\n]*)?$') { throw 'Invalid prepared public key.' }
    $fingerprint = & (Join-Path $env:WINDIR 'System32\OpenSSH\ssh-keygen.exe') -lf $publicPath
    if ($LASTEXITCODE -ne 0 -or $fingerprint -notlike ('*' + $enrollment.fingerprint + '*')) { throw 'Public key fingerprint does not match the prepared enrollment.' }
    $destination = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
    $existing = if (Test-Path -LiteralPath $destination) { [IO.File]::ReadAllText($destination) } else { '' }
    if (Test-Path -LiteralPath $destination) {
        $backup = $destination + '.backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
        Copy-Item -LiteralPath $destination -Destination $backup
    }
    $sameKey = @($existing -split '\r?\n' | Where-Object { ($_ -split '\s+')[1] -eq ($publicKey -split '\s+')[1] })
    if ($sameKey.Count -eq 0) {
        $updated = if ($existing.Length) { $existing.TrimEnd("`r", "`n") + "`r`n" + $publicKey + "`r`n" } else { $publicKey + "`r`n" }
        [IO.File]::WriteAllText($destination, $updated, [Text.UTF8Encoding]::new($false))
    }
    & icacls.exe $destination /inheritance:r /grant:r '*S-1-5-32-544:(F)' '*S-1-5-18:(F)' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect authorized_keys permissions.' }
    $rules = (Get-Acl -LiteralPath $destination).GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier])
    foreach ($sid in @($rules | ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique)) {
        if ($sid -notin @('S-1-5-32-544','S-1-5-18')) {
            & icacls.exe $destination /remove ('*' + $sid) | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not remove an incompatible authorized_keys permission.' }
        }
    }
    Start-Service sshd
    . (Join-Path $PSScriptRoot 'VmAutoConnect.ps1')
    $null = @(Save-VmHostPublicKeys $root $enrollment.fingerprint)
    Write-Host ''
    Write-Host 'SUCCESS: Host public key installed. Windows login password was not changed.' -ForegroundColor Green
    Write-Host ('Key: ' + $enrollment.fingerprint)
    Write-Host 'Return to the HOST and double-click the existing VM connection BAT.'
    exit 0
} catch {
    Write-Host ('FAILED: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
