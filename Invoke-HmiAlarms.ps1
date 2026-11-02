param(
    [string]$ProjectPath,[string]$HmiName,[string]$SourceFile,[string]$ChangesFile,[string]$OutputFile,
    [string]$Filter,[int]$Limit=100,[int]$ProcessId=0,[int]$TimeoutMs=60000,[switch]$Apply,[switch]$Refresh
)
$ErrorActionPreference='Stop'
if(-not $SourceFile -and (-not $ProjectPath -or -not $HmiName)){throw '请提供 -ProjectPath 和 -HmiName，或使用 -SourceFile 处理原生报警文件。'}
$alarmArgs=@{timeout_ms=$TimeoutMs}
if($SourceFile){$alarmArgs.source_file=[IO.Path]::GetFullPath($SourceFile)}else{$alarmArgs.project_path=[IO.Path]::GetFullPath($ProjectPath);$alarmArgs.hmi_name=$HmiName}
if($ProcessId){$alarmArgs.process_id=$ProcessId}
$alarmTool='tia_read_hmi_alarms'
if($ChangesFile){
    $alarmTool='tia_write_hmi_alarms'
    $alarmArgs.changes_json=Get-Content -LiteralPath $ChangesFile -Raw -Encoding UTF8
    $alarmArgs.dry_run=-not $Apply.IsPresent
    $alarmArgs.confirm=$Apply.IsPresent
    if($OutputFile){$alarmArgs.output_file=[IO.Path]::GetFullPath($OutputFile)}
}else{$alarmArgs.filter=$Filter;$alarmArgs.limit=$Limit;$alarmArgs.refresh=$Refresh.IsPresent}
$argumentFile=Join-Path $env:TEMP ('tia-alarm-args-'+[guid]::NewGuid().ToString('N')+'.json')
try {
    [IO.File]::WriteAllText($argumentFile,($alarmArgs|ConvertTo-Json -Depth 40 -Compress),[Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'Invoke-Online.ps1') -Engineering -Tool $alarmTool -ArgumentsFile $argumentFile
}finally{if(Test-Path -LiteralPath $argumentFile){Remove-Item -LiteralPath $argumentFile -Force}}
