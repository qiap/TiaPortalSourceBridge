$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$checks=0
foreach($edition in @('Build-ProjectIndex.ps1','engines\v21\Build-ProjectIndex.ps1')) {
    $text=[IO.File]::ReadAllText((Join-Path $root $edition))
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
    if($errors){throw "Script syntax: $errors"}
    $scopeFunction=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ScopeStatus'},$true)
    . ([scriptblock]::Create($scopeFunction.Extent.Text))
    $start=$text.IndexOf('    $catalogErrors=')
    $end=$text.IndexOf("`n}",$start)
    $finalize=[scriptblock]::Create($text.Substring($start,$end-$start))
    foreach($case in @(
        @{scope='PLC';ready=235;errors=0;warning=$true;want=0},
        @{scope='HMI';ready=230;errors=0;warning=$true;want=0},
        @{scope='Hardware';ready=74;errors=0;warning=$true;want=0},
        @{scope='PLC';ready=234;errors=1;warning=$true;want=2},
        @{scope='PLC';ready=234;errors=0;warning=$false;want=2},
        @{scope='All';ready=539;errors=0;warning=$true;want=0},
        @{scope='All';ready=538;errors=1;warning=$true;want=2},
        @{scope='HMI';ready=230;errors=0;warning=$true;catalog='HMI catalog Panel: read failed';want=2},
        @{scope='PLC';ready=235;errors=0;warning=$true;catalog='HMI catalog Panel: read failed';want=0},
        @{scope='Hardware';ready=74;errors=0;warning=$true;catalog='Hardware catalog: read failed';want=2}
    )) {
        $Scope=$case.scope
        $layers=@(@{layer='plc';total=235;ready=0;errors=0},@{layer='hmi';total=230;ready=0;errors=0},@{layer='hardware';total=74;ready=0;errors=0})
        if($Scope -ne 'All'){$layer=$layers|Where-Object {$_.layer -eq $Scope.ToLowerInvariant()};$layer.ready=$case.ready;$layer.errors=$case.errors}
        $status=@{total=539;ready=$case.ready;pending=(539-$case.ready);errors=$case.errors;layers=$layers;warnings=@()}
        if($case.warning){$status.warnings=@('本结果不包含集成连接；Connections 空列表不代表 HMI 没有 PLC 连接。')}
        if($case.catalog){$status.warnings+=@($case.catalog)}
        $scopeStatus=Get-ScopeStatus $status
        $kindFilter=switch($Scope){'PLC'{'plc.'};'HMI'{'hmi.'};'Hardware'{'hardware'};default{''}}
        $report=@{failed_objects=@();warnings=$status.warnings}
        . $finalize
        if($exitCode -ne $case.want -or $report.completed -ne ($case.want -eq 0)){throw "Wrong completion: $edition $Scope"}
        if($case.warning -and $report.warnings.Count -lt 1){throw 'Coverage notice was lost'}
        $checks++
    }
}
Write-Host "PASS: $checks index-scope completion cases"
