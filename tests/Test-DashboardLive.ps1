param([Parameter(Mandatory=$true)][string]$ExpectedProject, [string]$ExpectedCpu = '', [string]$ExpectedModel = '', [string]$BaseUrl = 'http://localhost:18980')
$ErrorActionPreference = 'Stop'
$clock = [Diagnostics.Stopwatch]::StartNew()
do {
    $state = Invoke-RestMethod ($BaseUrl + '/api/status') -TimeoutSec 40
    if ($state.available_plcs.Count -gt 0) { break }
    Start-Sleep -Milliseconds 300
} while ($clock.Elapsed.TotalSeconds -lt 45)
if (-not $state.active_project_is_running) { throw 'Current project is incorrectly marked offline' }
if ([IO.Path]::GetFullPath($state.active_project) -ne [IO.Path]::GetFullPath($ExpectedProject)) { throw 'Wrong active project' }
if ($state.detected_version -ne [regex]::Match([IO.Path]::GetExtension($ExpectedProject), '\d+').Value.Insert(0,'V')) { throw 'Wrong project version' }
if (-not ($state.available_projects | Where-Object { $_.path -eq $ExpectedProject -and $_.is_running -and $_.is_active })) { throw 'Opened project missing from dropdown' }
if ($ExpectedCpu -and $state.plc_name -ne $ExpectedCpu) { throw 'Wrong live CPU' }
if ($ExpectedModel -and $state.plc_hardware -ne $ExpectedModel) { throw 'Wrong live hardware model' }
if ($state.active_project_pid -ne $state.tia_processes[0].pid) { throw 'Process card and selected project disagree' }
$tools = Invoke-RestMethod ($BaseUrl + '/api/tools')
if ($state.tool_count -ne $tools.Count -or $tools.name -notcontains 'tia_read_document') { throw 'Wrong merged tool catalog' }
Write-Output ('PASS: {0}; {1}; {2}; PID={3}; {4} tools; dashboard uses live project information.' -f $state.active_project_name,$state.plc_name,$state.plc_hardware,$state.active_project_pid,$tools.Count)
