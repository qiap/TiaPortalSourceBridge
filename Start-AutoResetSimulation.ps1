param(
    [Parameter(Mandatory=$true)][string]$ProjectPath,
    [ValidateSet('Standard','Advanced')][string]$Backend='Standard',
    [string]$PlcName,
    [string]$InstanceName,
    [switch]$InstallTestHarness,
    [switch]$ShowSimulator,
    [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$utf8=[Text.UTF8Encoding]::new($false)
if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot ('simulation-results\workflow-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$ProjectPath=(Resolve-Path -LiteralPath $ProjectPath).Path
if($Backend -eq 'Standard'){
    if($InstanceName -or $InstallTestHarness){throw 'Standard mode currently verifies startup only. Advanced-only instance/harness options require explicit -Backend Advanced; no automatic fallback is performed.'}
    & (Join-Path $PSScriptRoot 'Start-StandardSimulation.ps1') -ProjectPath $ProjectPath -OutputDirectory $OutputDirectory
    Write-Output 'Standard PLCSIM startup verified. Download, CPU RUN and functional testing have not yet been verified on this route.'
    return
}
$timer=[Diagnostics.Stopwatch]::StartNew();$script:seq=0
function Call($tool,$arguments){
    $script:seq++;$stem=Join-Path $OutputDirectory ('{0:D2}-{1}' -f $script:seq,$tool)
    [IO.File]::WriteAllText(($stem+'.args.json'),($arguments|ConvertTo-Json -Depth 45 -Compress),$utf8)
    $reply=(& (Join-Path $PSScriptRoot 'Invoke-Online.ps1') -Engineering -Tool $tool -ArgumentsFile ($stem+'.args.json') -OutputFile ($stem+'.result.json'))|ConvertFrom-Json
    if($reply.error -or $reply.result.isError){throw ($tool+': '+$reply.result.content[0].text+'; receipt: '+$stem+'.result.json')}
    return $reply.result.structuredContent
}
$targets=Call 'tia_list_plcs' @{project_path=$ProjectPath}
if(-not $PlcName){if(@($targets.plcs).Count -ne 1){throw 'Please explicitly select PlcName when multiple PLCs exist'};$PlcName=$targets.plcs[0]}
$base=@{project_path=$ProjectPath;plc_name=$PlcName}
$blocks=Call 'tia_list_blocks' ($base+@{name_filter='FB_AutoReset_Test';refresh=$true})
if(@($blocks.blocks|Where-Object name -eq 'FB_AutoReset_Test').Count -ne 1){throw 'Expected exactly one FB_AutoReset_Test; create the function block first'}
if($InstallTestHarness){
    $items=@('DB_BridgeSim_AutoReset','DB_BridgeSim_AutoResetInstance','OB_BridgeSim_AutoReset'|ForEach-Object {
        @{block_name=$_;format='scl';group_path='Bridge_FunctionTests/Simulation';source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot ('plc-online\'+$_+'.scl')),$utf8)}
    })
    $null=Call 'tia_import_sources_batch' ($base+@{items_json=($items|ConvertTo-Json -Depth 20 -Compress);compile_mode='plc';confirm=$true})
}
$start=@{project_path=$ProjectPath;confirm=$true;show_ui=[bool]$ShowSimulator;cpu_type='CPU1513'}
if($InstanceName){$start.instance_name=$InstanceName}
$started=Call 'tia_simulation_start' $start
$InstanceName=$started.instance.instance_name
$target=$base+@{instance_name=$InstanceName}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'target.json'),($target|ConvertTo-Json),$utf8)
$null=Call 'tia_simulation_download' ($target+@{dry_run=$true})
# Deliberately one download attempt. On timeout/failure inspect the receipt before running again.
$download=Call 'tia_simulation_download' ($target+@{dry_run=$false;enable_simulation_support=$true;confirm=$true})
if(-not $download.downloaded){throw 'Download not confirmed successful'}
$null=Call 'tia_simulation_control' ($target+@{action='run';confirm=$true})
$result=Call 'tia_simulation_test_autoreset' ($target+@{confirm=$true})
$timer.Stop()
$report=@{project_path=$ProjectPath;plc=$PlcName;instance_name=$InstanceName;passed=$result.passed;checks=$result.checks;workflow_elapsed_ms=$timer.Elapsed.TotalMilliseconds;test_elapsed_ms=$result.elapsed_ms;test_report=$result.report_path;saved=$false;download_target='local PLCSIM Advanced Softbus only'}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'verification.json'),($report|ConvertTo-Json -Depth 25),$utf8)
Write-Output ('PASS: simulator started, program loaded, CPU RUN and automatic-reset checks completed. Workflow: {0:N2} seconds; tests: {1:N2} seconds.' -f ($timer.Elapsed.TotalSeconds),($result.elapsed_ms/1000.0))
Write-Output ('REPORT='+ (Join-Path $OutputDirectory 'verification.json'))
