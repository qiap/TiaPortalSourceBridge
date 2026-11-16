# Read-only diagnostics. Run inside the VM, preferably as administrator.
$ErrorActionPreference = 'Continue'
Write-Host '=== Windows / SSH ==='
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber | Format-List
Get-Service sshd, wuauserv, BITS -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType -AutoSize
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object Name, State | Format-Table -AutoSize
} else {
    Write-Host 'Run as administrator to also inspect the OpenSSH optional feature.'
}
Write-Host '=== System WinHTTP proxy ==='
netsh winhttp show proxy
Write-Host '=== Windows Update policy ==='
$wuPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
Get-ItemProperty -LiteralPath $wuPath -ErrorAction SilentlyContinue |
    Select-Object WUServer, WUStatusServer, DoNotConnectToWindowsUpdateInternetLocations, DisableWindowsUpdateAccess | Format-List
Get-ItemProperty -LiteralPath ($wuPath + '\AU') -ErrorAction SilentlyContinue |
    Select-Object UseWUServer | Format-List
Write-Host '=== DNS ==='
$targets = @('download.windowsupdate.com', 'sls.update.microsoft.com')
$policy = Get-ItemProperty -LiteralPath $wuPath -ErrorAction SilentlyContinue
if ($policy.WUServer) {
    try { $targets += ([uri]$policy.WUServer).DnsSafeHost } catch {}
}
foreach ($target in ($targets | Select-Object -Unique)) {
    try {
        $addresses = [Net.Dns]::GetHostAddresses($target)
        Write-Host ($target + ' => ' + (($addresses | ForEach-Object { $_.IPAddressToString }) -join ', '))
    } catch { Write-Host ($target + ' => FAILED: ' + $_.Exception.Message) }
}
Write-Host '=== VM IPv4 addresses ==='
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -ne '127.0.0.1' } |
    Select-Object InterfaceAlias, IPAddress | Format-Table -AutoSize
Write-Host 'Read-only check finished. No settings were changed.'
