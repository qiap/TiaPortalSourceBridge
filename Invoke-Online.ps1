param([Parameter(Mandatory=$true)][string]$Tool,[string]$ArgumentsFile,[string]$OutputFile,[switch]$Engineering)
$ErrorActionPreference='Stop'
$onlineArgs=@{}
if($ArgumentsFile){$onlineArgs=Get-Content -LiteralPath $ArgumentsFile -Raw -Encoding UTF8|ConvertFrom-Json}
$pipeName='TiaPortalSourceBridge-'+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-','_')
if(-not $Engineering){$pipeName+='-Online'}
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$pipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
try{
    try {
        $pipe.Connect(400)
    } catch {
        $pipe.Dispose()
        # A busy gateway owns the mutex even while its single pipe client is in use.
        # Wait for that gateway instead of starting another copy on every connection timeout.
        $runningHost=$false
        $hostMutex=$null
        try {
            $hostMutex=[Threading.Mutex]::OpenExisting('Local\'+$pipeName)
            $probeOwned=$false
            try {$probeOwned=$hostMutex.WaitOne(0)} catch [Threading.AbandonedMutexException] {$probeOwned=$true}
            if($probeOwned){$hostMutex.ReleaseMutex()}else{$runningHost=$true}
        } catch [Threading.WaitHandleCannotBeOpenedException] {
            $runningHost=$false
        } finally {
            if($null -ne $hostMutex){$hostMutex.Dispose()}
        }
        if(-not $runningHost){
            $exe = Join-Path $PSScriptRoot 'src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
            if (-not (Test-Path -LiteralPath $exe)) {
                $exe = Join-Path $PSScriptRoot 'online-bin\TiaPortalSourceBridge.exe'
            }
            $cfg = Join-Path $PSScriptRoot 'mcp-config.json'
            $hostCmd = if ($Engineering) { 'mcp-host' } else { 'online-host' }
            Start-Process -FilePath $exe -ArgumentList "$hostCmd --config `"$cfg`"" -WindowStyle Hidden | Out-Null
        }
        $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$pipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
        $pipe.Connect(15000)
    }
    $reader=[IO.StreamReader]::new($pipe,[Text.UTF8Encoding]::new($false))
    $writer=[IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false));$writer.AutoFlush=$true
    $writer.WriteLine((@{jsonrpc='2.0';id=1;method='tools/call';params=@{name=$Tool;arguments=$onlineArgs}}|ConvertTo-Json -Compress -Depth 40))
    # Allow the project worker's 10-minute deadline to return its unknown-outcome receipt first.
    $read=$reader.ReadLineAsync();if(-not $read.Wait(660000)){throw 'MCP response timeout; inspect server state before repeating a mutation'}
    if($null -eq $read.Result){throw 'Online MCP closed connection without a response'}
    if($OutputFile){[IO.File]::WriteAllText($OutputFile,$read.Result,[Text.UTF8Encoding]::new($true))}
    $read.Result
}finally{$pipe.Dispose()}
