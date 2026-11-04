param([Parameter(Mandatory=$true)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$registryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E8D3650B-D8F2-4C73-B313-DB8677A664BA}_is1'
if (Test-Path $registryKey) { throw 'An installed copy exists. Run this lifecycle test in a clean user account.' }
$id = [Guid]::NewGuid().ToString('N')
$target = Join-Path $root ('.work\installer-test-' + $id + '\App With Spaces')
$group = 'TIA Portal Source Bridge'
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
function Install-Package {
    $p = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/TASKS=""',('/LOG="'+(Join-Path $root '.work\setup-test.log')+'"'),('/DIR="'+$target+'"'),('/GROUP="'+$group+'"')) -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Setup failed: $($p.ExitCode)" }
}
try {
    Install-Package
    $main = Join-Path $target 'src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
    $v21 = Join-Path $target 'engines\v21\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
    foreach ($relative in @('web\index.html','config.json','mcp-config.json','engines\v21\mcp-config.json','engines\v21\tool-catalog.json','TrustThisTool.ps1','LaunchWeb.vbs')) {
        if (-not (Test-Path -LiteralPath (Join-Path $target $relative))) { throw "Missing installed file: $relative" }
    }
    if (-not (Test-Path $registryKey)) { throw 'Uninstaller registration missing' }
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) ($group + '\TIA Portal Source Bridge.lnk')
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    if ($link.TargetPath -ne (Join-Path (Split-Path $main) 'TiaPortalDashboard.exe') -or $link.WorkingDirectory -ne $target -or $link.Arguments -ne ('--config "'+$target+'\config.json"')) { throw ('Shortcut mismatch: expected ' + $target + '; actual directory=' + $link.WorkingDirectory + '; arguments=' + $link.Arguments + '; exists=' + (Test-Path -LiteralPath $shortcut)) }
    & python (Join-Path $PSScriptRoot 'Test-VersionRouting.py') $main $v21
    if ($LASTEXITCODE -ne 0) { throw 'Installed engine tests failed' }
    # Construct the real dashboard without starting listeners, MCP hosts or TIA connections.
    $probe = Join-Path (Split-Path -Parent $target) 'probe.ps1'
    @'
param($Exe,$Config)
$ErrorActionPreference='Stop'
$asm=[Reflection.Assembly]::LoadFrom($Exe)
$type=$asm.GetType('TiaPortalSourceBridge.Web.WebServer',$true)
$server=[Activator]::CreateInstance($type,[object[]]@($Config,[int]19983))
if(-not $server){throw 'Dashboard construction failed'}
Write-Output 'PASS: installed dashboard loads clean configuration.'
'@ | Set-Content -LiteralPath $probe -Encoding UTF8
    & powershell.exe -NoProfile -File $probe $main (Join-Path $target 'config.json')
    if ($LASTEXITCODE -ne 0) { throw 'Installed dashboard configuration failed' }
    $configs = @('config.json','engines\v21\mcp-config.json')
    foreach ($relative in $configs) {
        $path = Join-Path $target $relative
        $cfg = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $cfg.plcName = 'InstallerUpgradeSentinel'
        $cfg | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding UTF8
    }
    $backup = Join-Path $target 'block-backups\installer-test.txt'
    New-Item -ItemType Directory -Force (Split-Path -Parent $backup) | Out-Null
    Set-Content -LiteralPath $backup -Value 'keep-user-data'
    Install-Package
    foreach ($relative in $configs) {
        if ((Get-Content -LiteralPath (Join-Path $target $relative) -Raw | ConvertFrom-Json).plcName -ne 'InstallerUpgradeSentinel') { throw "Upgrade overwrote $relative" }
    }
    Write-Output 'PASS: installation, shortcuts, dual engines, upgrade configuration preservation.'
} finally {
    $uninstall = Join-Path $target 'unins000.exe'
    if (Test-Path -LiteralPath $uninstall) {
        $p = Start-Process -FilePath $uninstall -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw 'Test uninstall failed' }
    }
}
if (Test-Path $registryKey) { throw 'Uninstall registration remains' }
if (Test-Path -LiteralPath $main) { throw 'Uninstall left program binaries' }
if (Test-Path -LiteralPath $shortcut) { throw 'Uninstall left Start menu shortcut' }
if (-not (Test-Path -LiteralPath $backup)) { throw 'Uninstall removed user data' }
foreach ($relative in $configs) {
    if (-not (Test-Path -LiteralPath (Join-Path $target $relative))) { throw 'Uninstall removed user configuration' }
}
Write-Output 'PASS: uninstall removes program and shortcuts, preserves user configuration and backups.'
