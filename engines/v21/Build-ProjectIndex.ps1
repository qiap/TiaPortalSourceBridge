param(
    [Parameter(Position=0)][string]$ProjectPath,
    [switch]$PauseOnCompletion,
    [switch]$Resume,
    [ValidateRange(1,1000)][int]$BatchSize=25,
    [ValidateRange(100,60000)][int]$TimeBudgetMs=15000,
    [string]$ReportPath,
    [string]$ExecutablePath,
    [string]$ConfigPath,
    [ValidateRange(1,720)][int]$RequestTimeoutSeconds=660
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$utf8=[Text.UTF8Encoding]::new($false)
$process=$null
$inputWriter=$null
$stderr=$null
$report=$null
$exitCode=1
$cancelled=$false
$timer=[Diagnostics.Stopwatch]::StartNew()

function Select-IndexProject {
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.Application]::EnableVisualStyles()
    $recentPath=Join-Path $PSScriptRoot 'profiles\index-cli-recent.json'
    $lastProject=''
    foreach($candidate in @($recentPath,(Join-Path $PSScriptRoot 'recent.json'))) {
        if(Test-Path -LiteralPath $candidate -PathType Leaf){
            try{$lastProject=(Get-Content -LiteralPath $candidate -Raw -Encoding UTF8|ConvertFrom-Json).LastProjectPath}catch{}
            if($lastProject -and (Test-Path -LiteralPath $lastProject -PathType Leaf)){break}
            $lastProject=''
        }
    }
    $dialog=[Windows.Forms.OpenFileDialog]::new()
    try {
        $dialog.Title='选择要建立索引的 TIA Portal V21 工程'
        $dialog.Filter='TIA Portal V21 工程 (*.ap21;*.als21)|*.ap21;*.als21|所有 TIA 工程 (*.ap*;*.als*)|*.ap*;*.als*|所有文件 (*.*)|*.*'
        $dialog.CheckFileExists=$true;$dialog.CheckPathExists=$true
        $dialog.Multiselect=$false;$dialog.RestoreDirectory=$true
        if($lastProject){$dialog.InitialDirectory=Split-Path -Parent $lastProject;$dialog.FileName=Split-Path -Leaf $lastProject}
        else{$dialog.InitialDirectory=Split-Path -Parent $PSScriptRoot}
        if($dialog.ShowDialog() -ne [Windows.Forms.DialogResult]::OK){return ''}
        $selected=$dialog.FileName
        if([IO.Path]::GetExtension($selected) -match '^\.(ap|als)\d+$'){
            try{
                [IO.Directory]::CreateDirectory((Split-Path -Parent $recentPath))|Out-Null
                [IO.File]::WriteAllText($recentPath,(@{LastProjectPath=$selected}|ConvertTo-Json -Compress),$utf8)
            }catch{Write-Host ('未能记住上次选择的位置：'+$_.Exception.Message)}
        }
        return $selected
    } finally {$dialog.Dispose()}
}

function Invoke-IndexRequest([string]$Method,$Parameters) {
    $script:requestId++
    $request=@{jsonrpc='2.0';id=$script:requestId;method=$Method;params=$Parameters}|ConvertTo-Json -Depth 30 -Compress
    $inputWriter.WriteLine($request)
    $read=$process.StandardOutput.ReadLineAsync()
    if(-not $read.Wait($RequestTimeoutSeconds*1000)){throw '索引请求超时；已保存的索引检查点可用 -Resume 继续。'}
    if($null -eq $read.Result){throw '索引进程已退出，没有返回结果。'}
    $reply=$read.Result|ConvertFrom-Json
    if($reply.id -ne $script:requestId){throw '索引服务返回了不匹配的请求编号。'}
    if($reply.error){throw ('索引协议错误：'+$reply.error.message)}
    if($reply.result.isError){throw ('索引操作失败：'+$reply.result.content[0].text)}
    return $reply.result
}

try {
    if([string]::IsNullOrWhiteSpace($ProjectPath)){
        $ProjectPath=Select-IndexProject
        if([string]::IsNullOrWhiteSpace($ProjectPath)){$cancelled=$true;$exitCode=0;Write-Host '已取消，没有建立或修改索引。';return}
    }
    $project=[IO.Path]::GetFullPath($ProjectPath)
    if(-not (Test-Path -LiteralPath $project -PathType Leaf)){throw ('工程文件不存在：'+$project)}
    if([IO.Path]::GetExtension($project) -notmatch '^\.(ap|als)\d+$'){throw '请指定 TIA Portal V21 工程文件（.ap21 或多用户本地会话 .als21）。'}
    if(-not $ExecutablePath){
        $ExecutablePath=Join-Path $PSScriptRoot 'src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
        if(-not (Test-Path -LiteralPath $ExecutablePath)){$ExecutablePath=Join-Path $PSScriptRoot 'online-bin\TiaPortalSourceBridge.exe'}
    }
    $ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
    if(-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)){throw '未找到工具主程序，请先构建或安装工具。'}
    if(-not $ConfigPath){$ConfigPath=Join-Path $PSScriptRoot 'mcp-config.json'}
    $ConfigPath=[IO.Path]::GetFullPath($ConfigPath)
    if(-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)){throw ('配置文件不存在：'+$ConfigPath)}
    $indexDirectory=Join-Path (Join-Path (Split-Path -Parent $project) '.tia-source-index') ([IO.Path]::GetFileName($project))
    if(-not $ReportPath){$ReportPath=Join-Path $indexDirectory 'last-build-report.json'}
    $ReportPath=[IO.Path]::GetFullPath($ReportPath)
    if($ReportPath -eq $project -or $ReportPath -eq $ConfigPath -or $ReportPath -eq $ExecutablePath){throw '报告路径不能覆盖工程、配置或主程序。'}
    if((Split-Path -Parent $ReportPath) -eq $indexDirectory -and ([IO.Path]::GetFileName($ReportPath) -in @('catalog.json','dirty.json','write.lock'))){throw '报告路径不能覆盖索引内部文件。'}
    $report=[ordered]@{project_path=$project;started_at=[DateTimeOffset]::Now.ToString('o');mode=$(if($Resume){'resume'}else{'fresh'});completed=$false;partial=$false;total=0;ready=0;pending=0;errors=0;warnings=@();batches=@();failed_objects=@();failure='';index_directory=$indexDirectory;report_path=$ReportPath}
    Write-Host ('工程：'+$project)
    Write-Host '建立目录和内容索引；不编译、不保存、不下载。'
    $info=[Diagnostics.ProcessStartInfo]::new()
    $info.FileName=$ExecutablePath
    # A dedicated STA worker keeps this CLI independent of the persistent gateway's single client pipe.
    $info.Arguments='mcp-project-worker --config "'+$ConfigPath+'"'
    $info.WorkingDirectory=$PSScriptRoot
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.StandardOutputEncoding=$utf8;$info.StandardErrorEncoding=$utf8
    $process=[Diagnostics.Process]::Start($info)
    $stderr=$process.StandardError.ReadToEndAsync()
    $inputWriter=[IO.StreamWriter]::new($process.StandardInput.BaseStream,$utf8);$inputWriter.AutoFlush=$true
    $script:requestId=0
    $init=Invoke-IndexRequest 'initialize' @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='full-project-index-cli';version='1'}}
    $catalogTools=Invoke-IndexRequest 'tools/list' @{}
    if('tia_build_project_index' -notin $catalogTools.tools.name){throw '此主程序没有工程索引接口，请更新到3.0或更高版本。'}
    $catalog=Invoke-IndexRequest 'tools/call' @{name='tia_build_project_index';arguments=@{project_path=$project;detail_level='catalog';refresh_catalog=(-not $Resume);include_hardware=$true;response_mode='compact'}}
    $status=$catalog.structuredContent
    foreach($key in @('total','ready','pending','errors')){$report[$key]=[int]$status.$key}
    $report.warnings=@($status.warnings)
    Write-Host ('目录：{0} 个对象；开始分批读取。' -f $status.total)
    $batchLimit=[Math]::Max(100,([int]$status.total*2+10))
    for($batch=1;$batch -le $batchLimit;$batch++) {
        $reply=Invoke-IndexRequest 'tools/call' @{name='tia_build_project_index';arguments=@{project_path=$project;detail_level='content';max_objects=$BatchSize;time_budget_ms=$TimeBudgetMs;response_mode='compact'}}
        $data=$reply.structuredContent;$status=$data.status
        foreach($key in @('total','ready','pending','errors')){$report[$key]=[int]$status.$key}
        $report.warnings=@($status.warnings)
        $report.batches+=@{batch=$batch;updated=[int]$data.updated;failed=[int]$data.failed;ready=[int]$status.ready;pending=[int]$status.pending;execution_ms=$reply._meta.performance.execution_ms}
        Write-Host ('[{0}] 已索引 {1}/{2}，失败 {3}，本批新增 {4}。' -f $batch,$status.ready,$status.total,$status.errors,$data.updated)
        if([int]$status.pending -eq 0 -or [int]$status.pending -eq [int]$status.errors){break}
        if(([int]$data.updated+[int]$data.failed) -eq 0){throw '本批没有取得进展，但仍有未完成对象；请查看索引状态后重试。'}
        if($batch -eq $batchLimit){throw '索引持续变化或批次数超出上限；已保留进度，可以 -Resume 继续。'}
    }
    # Query errors from the persistent catalog, including errors inherited by -Resume.
    for($offset=0;$offset -lt [int]$status.total;$offset+=500) {
        $tree=Invoke-IndexRequest 'tools/call' @{name='tia_get_project_tree';arguments=@{project_path=$project;offset=$offset;limit=500;response_mode='compact'}}
        foreach($item in @($tree.structuredContent.objects)) {
            if($item.state -eq 'error'){$report.failed_objects+=@{name=$item.name;kind=$item.kind;owner=$item.owner;group_path=$item.group_path;error=$item.error}}
        }
    }
    $report.completed=([int]$status.pending -eq 0 -and @($status.warnings).Count -eq 0)
    $report.partial=(-not $report.completed)
    $exitCode=if($report.completed){0}else{2}
    if($report.completed){Write-Host ('完整索引已建立：{0}/{0}。' -f $status.total)}
    else {
        Write-Host ('索引建立完成，但有未读取对象：已索引 {0}/{1}，失败 {2}。' -f $status.ready,$status.total,$status.errors)
        foreach($item in $report.failed_objects){Write-Host ('  '+$item.name+'：'+$item.error)}
        foreach($warning in $report.warnings){Write-Host ('  目录警告：'+$warning)}
    }
}
catch {
    $exitCode=1
    if($report){$report.failure=$_.Exception.Message}
    [Console]::Error.WriteLine($_.Exception.Message)
}
finally {
    if($inputWriter){try{$inputWriter.Close()}catch{}}
    if($process){
        try{if(-not $process.WaitForExit(5000)){$process.Kill();$process.WaitForExit(2000)|Out-Null}}catch{}
        if($stderr -and $stderr.IsCompleted -and $exitCode -eq 1){$diagnostic=$stderr.Result;if($diagnostic){[Console]::Error.WriteLine($diagnostic)}}
        $process.Dispose()
    }
    $timer.Stop()
    if($report){
        $report['finished_at']=[DateTimeOffset]::Now.ToString('o');$report['elapsed_ms']=$timer.ElapsedMilliseconds;$report['exit_code']=$exitCode
        try{
            [IO.Directory]::CreateDirectory((Split-Path -Parent $ReportPath))|Out-Null
            [IO.File]::WriteAllText($ReportPath,($report|ConvertTo-Json -Depth 15),$utf8)
            Write-Host ('索引位置：'+$indexDirectory)
            Write-Host ('结果报告：'+$ReportPath)
            Write-Host ('总用时：{0:N2} 秒。' -f ($timer.ElapsedMilliseconds/1000))
        }catch{[Console]::Error.WriteLine('无法写入索引报告：'+$_.Exception.Message);$exitCode=1}
    }
    if($PauseOnCompletion -and -not $cancelled){Read-Host '按 Enter 关闭窗口'|Out-Null}
}
exit $exitCode
