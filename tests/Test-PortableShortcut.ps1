param([Parameter(Mandatory=$true)][string]$Zip)
$ErrorActionPreference = 'Stop'
# Keep a Chinese character and a space in the extraction path even under Windows PowerShell 5.
$dir = Join-Path $env:TEMP ('TIA ' + [char]0x5DE5 + [char]0x7A0B + ' ' + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $Zip -DestinationPath $dir
$root = (Get-ChildItem -LiteralPath $dir -Directory | Select-Object -First 1).FullName
$exe = Join-Path $root 'src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
$config = Join-Path $root 'config.json'
$path = Join-Path $root 'TIA Portal Source Bridge.lnk'
$script = Join-Path $root 'CreateShortcuts.vbs'
& cscript.exe //nologo $script /folder-only
if ($LASTEXITCODE -ne 0) { throw 'Shortcut generator failed' }
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($path)
if ($link.TargetPath -ne $exe -or $link.WorkingDirectory -ne $root -or
    $link.IconLocation -ne ($exe + ',0') -or $link.Arguments -cne ('web --hide-console --config "' + $config + '"')) {
    throw 'Incorrect shortcut target, icon, arguments or working directory'
}
Add-Type -AssemblyName System.Drawing
$icon = [Drawing.Icon]::ExtractAssociatedIcon($link.TargetPath)
if (!$icon) { throw 'Cannot extract executable icon' }
$icon.Dispose()
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$link.Arguments += " --port $port --no-browser"
$link.Save()
$server = Start-Process -FilePath $path -PassThru -WindowStyle Hidden
$base = "http://localhost:$port"
$up = $false
try {
    for ($i=0; $i -lt 50; $i++) {
        try {
            $build = Invoke-RestMethod "$base/api/build" -TimeoutSec 1
            if (($build.stamp -split '\|')[0] -eq $exe) { $up = $true; break }
        } catch {}
        Start-Sleep -Milliseconds 300
    }
    if (!$up) { throw 'The shortcut did not start the packaged executable' }
    Write-Output 'PASS: portable shortcut icon, quoted Unicode paths, Shell launch and dashboard HTTP response.'
} finally {
    if ($up) { $null = Invoke-RestMethod "$base/api/shutdown" -Method Post -TimeoutSec 4 }
}
