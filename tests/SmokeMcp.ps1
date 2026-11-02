param([switch]$HostClient, [switch]$ReadPlc, [switch]$CompilePlc)
$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $utf8NoBom
[Console]::OutputEncoding = $utf8NoBom

$toolRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $toolRoot 'src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
$newestCs = Get-ChildItem -Path @((Join-Path $toolRoot 'src'), (Join-Path $toolRoot 'shared')) -Filter '*.cs' -Recurse | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not (Test-Path -LiteralPath $exePath) -or ($newestCs -and $newestCs.LastWriteTimeUtc -gt (Get-Item -LiteralPath $exePath).LastWriteTimeUtc)) {
    & cmd.exe /c "`"$(Join-Path $toolRoot 'build.bat')`""
}

$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = $exePath
$info.Arguments = if ($HostClient) { 'mcp-client' } else { 'mcp --config "' + (Join-Path $toolRoot 'mcp-config.json') + '"' }
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.StandardOutputEncoding = $utf8NoBom
$info.StandardErrorEncoding = $utf8NoBom

$process = [System.Diagnostics.Process]::Start($info)
$stderrTask = $process.StandardError.ReadToEndAsync()
function Request($id, $method, $parameters, $timeoutMs = 15000) {
    $request = @{jsonrpc='2.0';id=$id;method=$method;params=$parameters} | ConvertTo-Json -Depth 25 -Compress
    $process.StandardInput.WriteLine($request)
    $process.StandardInput.Flush()
    $readTask = $process.StandardOutput.ReadLineAsync()
    if (-not $readTask.Wait($timeoutMs)) { throw "Response timed out: $method" }
    if ($null -eq $readTask.Result) { throw ('Client exited: ' + $stderrTask.Result) }
    $response = $readTask.Result | ConvertFrom-Json
    if ($response.error) { throw ("RPC error on " + $method + ": " + $response.error.message) }
    if ($response.id -ne $id) { throw "Invalid response id: $method" }
    return $response.result
}
try {
    $init = Request 1 initialize @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='smoke';version='1'}}
    if ($init.serverInfo.version -notmatch '^[234]\.[0-9]+\.[0-9]+$') { throw ('Unexpected version: ' + $init.serverInfo.version) }
    $catalog = Request 2 'tools/list' @{}
    foreach ($name in @('tia_connection_status','tia_connect','tia_list_types','tia_list_tag_tables','tia_read_tag_table','tia_import_tag_table','tia_read_block','tia_read_source','tia_read_sources_batch','tia_import_block','tia_import_source','tia_import_sources_batch','tia_import_blocks_batch','tia_restore_backup','tia_delete_block','tia_generate_lad','tia_read_hmi_alarms','tia_write_hmi_alarms')) {
        if ($name -notin $catalog.tools.name) { throw "Missing tool $name" }
    }
    $networks = '[{"coil":"Motor","coil_type":"set","branches":[[{"symbol":"#Start","edge":"p","edge_memory":"#StartEdge"},{"symbol":"Station[1].Stop","negated":true},{"compare":"==","left":"#Step","right":"10","datatype":"Int"}],[{"symbol":"Motor"}]]},{"coil":"Indicator","branches":[[]]}]'
    $iface = '{"Input":[{"name":"Start","type":"Bool"},{"name":"Step","type":"Int"}],"Output":[{"name":"Motor","type":"Bool"}],"Static":[{"name":"StartEdge","type":"Bool"}]}'
    $result = Request 3 'tools/call' @{name='tia_generate_lad';arguments=@{block_name='Mcp_Smoke';block_number=32000;block_type='FB';networks_json=$networks;interface_json=$iface}}
    if ($result.isError) { throw $result.content[0].text }
    [xml]$xml = $result.structuredContent.xml
    if ($xml.Document.Engineering.version -like 'V21*' -and -not $xml.SelectSingleNode('/Document/SW.Blocks.FB/AttributeList/Namespace')) { throw 'V21 Namespace identifier missing' }
    $units = $xml.SelectNodes('//SW.Blocks.CompileUnit')
    if ($units.Count -ne 2) { throw 'Network count incorrect' }
    if ($xml.SelectNodes("//*[local-name()='Part' and @Name='O']").Count -ne 1) { throw 'Parallel branches missing' }
    if ($xml.SelectNodes("//*[local-name()='Part' and @Name='SCoil']").Count -ne 1) { throw 'SCoil missing' }
    if ($xml.SelectNodes("//*[local-name()='Part' and @Name='PContact']").Count -ne 1) { throw 'PContact missing' }
    if ($xml.SelectNodes("//*[local-name()='Part' and @Name='Eq']").Count -ne 1) { throw 'Eq compare missing' }
    if ($xml.SelectNodes("//*[local-name()='Component' and @Name='Station']/*[local-name()='Access' and @Scope='LiteralConstant']").Count -ne 1) { throw 'Array index access missing' }
    if ($xml.SelectNodes("//*[local-name()='Access' and @Scope='LocalVariable']").Count -lt 1) { throw 'LocalVariable access missing' }
    if ($xml.SelectNodes("//*[local-name()='Negated']").Count -ne 1) { throw 'NC contact missing' }
    foreach ($unit in $units) {
        $ids = @($unit.SelectNodes('.//*[@UId and (local-name()="Access" or local-name()="Part" or local-name()="Wire")]') | ForEach-Object { $_.UId })
        if (($ids | Select-Object -Unique).Count -ne $ids.Count) { throw 'Duplicate network UId' }
    }
    $status = Request 4 'tools/call' @{name='tia_connection_status';arguments=@{}}
    Write-Output ("PASS: initialize (v{0}), {1} tools, FB/Static/Array/PContact/Compare/SCoil/LocalVar LAD, status; no project writes." -f $init.serverInfo.version, $catalog.tools.Count)
    Write-Output $status.content[0].text
    if ($ReadPlc) {
        $plcs = Request 5 'tools/call' @{name='tia_list_plcs';arguments=@{}}
        if ($plcs.isError) { throw $plcs.content[0].text }
        if ($plcs.structuredContent.plcs.Count -ne 1) { throw 'Read verification requires exactly one PLC' }
        $plcName = $plcs.structuredContent.plcs[0]
        if ($CompilePlc) {
            $compile = Request 8 'tools/call' @{name='tia_compile_plc';arguments=@{plc_name=$plcName}} 120000
            Write-Output ('Compile: {0} errors, {1} warnings; no save/download.' -f $compile.structuredContent.error_count,$compile.structuredContent.warning_count)
        }
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $list = Request 6 'tools/call' @{name='tia_list_blocks';arguments=@{plc_name=$plcName}}
        if ($list.isError) { throw $list.content[0].text }
        $clock.Stop()
        Write-Output ('PASS: {0} blocks listed in {1} ms' -f $list.structuredContent.blocks.Count,$clock.ElapsedMilliseconds)
        $candidate = $list.structuredContent.blocks | Where-Object { $_.language -eq 'LAD' -and $_.group_path -like 'Standard_Function*' -and $_.name -like 'FC*' } | Select-Object -First 1
        if (-not $candidate) { $candidate = $list.structuredContent.blocks | Select-Object -First 1 }
        $read = Request 7 'tools/call' @{name='tia_read_block';arguments=@{plc_name=$plcName;block_name=$candidate.name;group_path=$candidate.group_path}}
        if ($read.isError) { throw $read.content[0].text }
        [xml]$block = $read.structuredContent.xml
        if (-not $block.Document) { throw 'Invalid exported XML' }
        Write-Output ('PASS: read block {0} ({1}), XML {2} characters; no imports/saves.' -f $candidate.name,$candidate.language,$read.structuredContent.xml.Length)
    }
} finally {
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(2000)) { $process.Kill() }
    $process.Dispose()
}
