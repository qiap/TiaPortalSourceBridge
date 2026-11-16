param([Parameter(Mandatory=$true)][string]$ReportPath, [switch]$StartDashboard)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$report=[ordered]@{success=$false;isAdministrator=$null;protectedPublicKeyReadable=$null;publications=0;error=$null}
try {
    $principal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $report.isAdministrator=$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($report.isAdministrator) { throw 'This regression test must run with a limited interactive token.' }
    $publicPath=Join-Path $env:ProgramData 'ssh\ssh_host_ecdsa_key.pub'
    try { $null=[IO.File]::ReadAllText($publicPath); $report.protectedPublicKeyReadable=$true }
    catch [UnauthorizedAccessException] { $report.protectedPublicKeyReadable=$false }
    . (Join-Path $root 'scripts\VmAutoConnect.ps1')
    $old=Get-Content -LiteralPath (Join-Path $root 'vm-access\vm-endpoint.json') -Raw | ConvertFrom-Json
    for ($pass=0; $pass -lt 2; $pass++) {
        Publish-VmEndpoint $root $old.executable $old.remotePort @($old.addresses)
        $report.publications++
    }
    if ($StartDashboard) {
        $startup=Join-Path $root 'scripts\Start-VmDashboard.ps1'
        $proc=Start-Process powershell.exe -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "'+$startup+'"') -WindowStyle Hidden -WorkingDirectory $env:USERPROFILE -RedirectStandardOutput ($ReportPath+'.stdout.log') -RedirectStandardError ($ReportPath+'.stderr.log') -PassThru
        $report.launcherPid=$proc.Id
        $report.dashboardHttp=$null
        for ($i=0;$i -lt 30;$i++) {
            try {
                $request=[Net.HttpWebRequest]::Create('http://127.0.0.1:'+ $old.remotePort +'/api/build')
                $request.Proxy=$null; $request.Timeout=1000
                $response=$request.GetResponse()
                $report.dashboardHttp=[int]$response.StatusCode
                $response.Close()
                break
            } catch { Start-Sleep -Milliseconds 500 }
        }
        if ($report.dashboardHttp -ne 200) { throw 'Standard-user dashboard startup did not respond; inspect the accompanying stdout/stderr logs.' }
    }
    $report.success=$true
} catch { $report.error=$_.Exception.Message }
[IO.Directory]::CreateDirectory((Split-Path -Parent $ReportPath)) | Out-Null
[IO.File]::WriteAllText($ReportPath,($report | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
if (-not $report.success) { exit 1 }
