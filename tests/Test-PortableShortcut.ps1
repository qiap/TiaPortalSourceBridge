param([Parameter(Mandatory=$true)][string]$Zip)
$ErrorActionPreference = 'Stop'
# Keep a Chinese character and a space in the extraction path even under Windows PowerShell 5.
$dir = Join-Path $env:TEMP ('TIA ' + [char]0x5DE5 + [char]0x7A0B + ' ' + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $Zip -DestinationPath $dir
$root = (Get-ChildItem -LiteralPath $dir -Directory | Select-Object -First 1).FullName
$exe = Join-Path $root 'src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
$launcher = Join-Path (Split-Path -Parent $exe) 'TiaPortalDashboard.exe'
$config = Join-Path $root 'config.json'
$path = Join-Path $root 'TIA Portal Source Bridge.lnk'
$script = Join-Path $root 'CreateShortcuts.vbs'
& cscript.exe //nologo $script /folder-only
if ($LASTEXITCODE -ne 0) { throw 'Shortcut generator failed' }
for ($i=0; !(Test-Path -LiteralPath $path) -and $i -lt 60; $i++) { Start-Sleep -Milliseconds 200 }
$type = [Reflection.Assembly]::LoadFrom($exe).GetType('TiaPortalSourceBridge.Web.DashboardShortcut',$true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$link = $type.GetMethod('Read',$flags).Invoke($null,[string[]]@($path))
if ($link[0] -ne $launcher -or $link[2] -ne $root -or $link[3] -ne $launcher -or $link[4] -ne '0' -or
    $link[1] -cne ('--config "' + $config + '"')) {
    throw 'Incorrect shortcut target, icon, arguments or working directory'
}
Add-Type -AssemblyName System.Drawing
$icon = [Drawing.Icon]::ExtractAssociatedIcon($exe)
if (!$icon) { throw 'Cannot extract executable icon' }
$icon.Dispose()
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$type.GetMethod('Write',$flags).Invoke($null,[string[]]@($path,$launcher,($link[1] + " --port $port --no-browser"),$root))
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
    $web = @(Get-CimInstance Win32_Process -Filter "Name='TiaPortalSourceBridge.exe'" | Where-Object {
        $_.ExecutablePath -eq $exe -and $_.CommandLine.Contains("--port $port --no-browser")
    })
    if ($web.Count -ne 1) { throw 'Cannot identify the dashboard process started by the shortcut' }
    # CREATE_NO_WINDOW may still provide a console object, but it must have no visible window.
    @'
import ctypes, sys
k = ctypes.WinDLL("kernel32", use_last_error=True)
k.FreeConsole()
attached = k.AttachConsole(int(sys.argv[1]))
if attached:
    k.GetConsoleWindow.restype = ctypes.c_void_p
    hwnd = k.GetConsoleWindow()
    assert not hwnd or not ctypes.windll.user32.IsWindowVisible(ctypes.c_void_p(hwnd))
    k.FreeConsole()
else:
    assert ctypes.get_last_error() == 6
'@ | python - $web[0].ProcessId
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard has a visible console window' }
    Write-Output 'PASS: portable shortcut icon, Unicode paths, dashboard HTTP response and windowless service.'
} finally {
    if ($up) { $null = Invoke-RestMethod "$base/api/shutdown" -Method Post -TimeoutSec 4 }
}
