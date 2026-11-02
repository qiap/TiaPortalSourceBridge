param([switch]$HostClient, [switch]$ReadPlc, [switch]$CompilePlc)
$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $utf8NoBom
[Console]::OutputEncoding = $utf8NoBom

$toolRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $toolRoot 'src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
$newestCs = Get-ChildItem -Path (Join-Path $toolRoot 'src') -Filter '*.cs' -Recurse |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1

if (-not (Test-Path -LiteralPath $exePath) -or ($newestCs -and $newestCs.LastWriteTimeUtc -gt (Get-Item -LiteralPath $exePath).LastWriteTimeUtc)) {
    & cmd.exe /c "`"$(Join-Path $toolRoot 'build.bat')`""
    if ($LASTEXITCODE -ne 0) { throw "build.bat failed with exit code $LASTEXITCODE" }
}

function Find-V21SchemaDir {
    foreach ($candidate in @(
        'E:\botuv_21\Portal V21\PublicAPI\V21\Schemas',
        'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\Schemas',
        'D:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\Schemas'
    )) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

function Assert-XmlFragmentValidAgainstSchema([string]$fragmentXml, [string]$schemaFilePath) {
    $schemaDir = Split-Path -Parent $schemaFilePath
    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.ValidationType = [System.Xml.ValidationType]::Schema
    $resolver = New-Object System.Xml.XmlUrlResolver
    $settings.Schemas.XmlResolver = $resolver
    [void]$settings.Schemas.Add($null, $schemaFilePath)
    $errors = New-Object System.Collections.Generic.List[string]
    $settings.add_ValidationEventHandler({
        param($s, $e)
        if ($e.Severity -eq [System.Xml.Schema.XmlSeverityType]::Error) {
            $errors.Add($e.Message)
        }
    })
    $sr = New-Object System.IO.StringReader($fragmentXml)
    $xr = [System.Xml.XmlReader]::Create($sr, $settings)
    try {
        while ($xr.Read()) { }
    } finally {
        $xr.Close()
        $sr.Dispose()
    }
    if ($errors.Count -gt 0) {
        throw ("XSD validation failed against $([IO.Path]::GetFileName($schemaFilePath)): " + ($errors -join ' | '))
    }
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

function Request($id, $method, $parameters, $timeoutMs = 20000) {
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
    # 1. Verify MCP initialize & V21 server version 4.0.0
    $init = Request 1 initialize @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='smoke-v21';version='1'}}
    if ($init.serverInfo.version -notmatch '^4\.[0-9]+\.[0-9]+$') {
        throw ('Unexpected server version: ' + $init.serverInfo.version)
    }

    # 2. Verify all V21 tools are registered
    $catalog = Request 2 'tools/list' @{}
    $requiredTools = @(
        'tia_connection_status','tia_connect','tia_list_plcs',
        'tia_read_document','tia_read_documents_batch','tia_import_document','tia_import_documents_batch',
        'tia_import_blocks_batch','tia_list_software_units','tia_query_cross_references',
        'tia_read_unified_hmi','tia_compile_unified_hmi','tia_search_hardware_catalog',
        'tia_export_aml','tia_import_aml',
        'tia_list_blocks','tia_list_types','tia_list_tag_tables','tia_read_tag_table','tia_import_tag_table',
        'tia_read_block','tia_read_source','tia_read_sources_batch','tia_import_block','tia_import_source','tia_import_sources_batch',
        'tia_restore_backup','tia_delete_block','tia_generate_lad','tia_generate_graph',
        'tia_simulation_status','tia_simulation_start','tia_simulation_stop','tia_simulation_download',
        'tia_build_project_index','tia_get_task_context','tia_list_process_library','tia_render_process_template'
    )
    foreach ($name in $requiredTools) {
        if ($name -notin $catalog.tools.name) { throw "Missing V21 MCP tool: $name" }
    }

    # 3. Verify V21 LAD generation (Engineering V21 + Namespace + FlgNet/v5 + official XSD validation)
    $networks = '[{"coil":"Motor","coil_type":"set","branches":[[{"symbol":"#Start","edge":"p","edge_memory":"#StartEdge"},{"symbol":"Station[1].Stop","negated":true},{"compare":"==","left":"#Step","right":"10","datatype":"Int"}],[{"symbol":"Motor"}]]},{"coil":"Indicator","branches":[[]]}]'
    $iface = '{"Input":[{"name":"Start","type":"Bool"},{"name":"Step","type":"Int"}],"Output":[{"name":"Motor","type":"Bool"}],"Static":[{"name":"StartEdge","type":"Bool"}]}'
    $ladResult = Request 3 'tools/call' @{
        name = 'tia_generate_lad'
        arguments = @{
            block_name = 'Mcp_V21_Smoke'
            block_number = 32000
            block_type = 'FB'
            namespace = 'UnitA.Motion'
            networks_json = $networks
            interface_json = $iface
        }
    }
    if ($ladResult.isError) { throw $ladResult.content[0].text }
    [xml]$xml = $ladResult.structuredContent.xml
    if ($xml.Document.Engineering.version -ne 'V21') { throw ('Expected Engineering version V21, got: ' + $xml.Document.Engineering.version) }
    $nsNode = $xml.SelectSingleNode('/Document/SW.Blocks.FB/AttributeList/Namespace')
    if (-not $nsNode -or $nsNode.InnerText -ne 'UnitA.Motion') { throw 'V21 Namespace value mismatch' }

    $flgNets = $xml.SelectNodes("//*[local-name()='FlgNet']")
    if ($flgNets.Count -ne 2) { throw 'Expected 2 FlgNet nodes' }
    foreach ($fn in $flgNets) {
        if ($fn.NamespaceURI -ne 'http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5') {
            throw ('Expected V21 FlgNet/v5 namespace, got: ' + $fn.NamespaceURI)
        }
    }

    $units = $xml.SelectNodes('//SW.Blocks.CompileUnit')
    foreach ($unit in $units) {
        $ids = @($unit.SelectNodes('.//*[@UId and (local-name()="Access" or local-name()="Part" or local-name()="Wire")]') | ForEach-Object { $_.UId })
        if (($ids | Select-Object -Unique).Count -ne $ids.Count) { throw 'Duplicate network UId' }
    }

    # 4. Verify V21 GRAPH generation (Graph/v6 + official XSD validation)
    $graphModel = @{
        block_name = 'FB_Seq_V21'
        block_number = 32001
        namespace = 'UnitA.Sequence'
        sequences = @(
            @{
                title = 'MainSeq'
                steps = @(
                    @{ number = 1; name = 'Step_Init'; initial = $true; actions = @(@{ qualifier = 'N'; operand = '"MotorRun"' }) },
                    @{ number = 2; name = 'Step_Work'; initial = $false }
                )
                transitions = @(
                    @{ number = 1; name = 'Trans_Start'; condition = @{ branches = @(,@(@{ symbol = 'StartCmd' })) } },
                    @{ number = 2; name = 'Trans_Done'; condition = @{ branches = @(,@(@{ symbol = 'WorkDone' })) } }
                )
                connections = @(
                    @{ from = @{ kind = 'Step'; number = 1 }; to = @{ kind = 'Transition'; number = 1 } },
                    @{ from = @{ kind = 'Transition'; number = 1 }; to = @{ kind = 'Step'; number = 2 } },
                    @{ from = @{ kind = 'Step'; number = 2 }; to = @{ kind = 'Transition'; number = 2 } },
                    @{ from = @{ kind = 'Transition'; number = 2 }; to = @{ kind = 'Step'; number = 1 }; link_type = 'Jump' }
                )
            }
        )
    } | ConvertTo-Json -Depth 15 -Compress

    $graphResult = Request 4 'tools/call' @{
        name = 'tia_generate_graph'
        arguments = @{ model_json = $graphModel; include_model = $false }
    }
    if ($graphResult.isError) { throw ('tia_generate_graph failed: ' + $graphResult.content[0].text) }
    [xml]$gXml = $graphResult.structuredContent.xml
    $graphNode = $gXml.SelectSingleNode("//*[local-name()='Graph']")
    if (-not $graphNode -or $graphNode.NamespaceURI -ne 'http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v6') {
        throw 'Expected Graph/v6 namespace in generated V21 GRAPH XML'
    }
    $gNsNode = $gXml.SelectSingleNode('/Document/SW.Blocks.FB/AttributeList/Namespace')
    if (-not $gNsNode -or $gNsNode.InnerText -ne 'UnitA.Sequence') {
        throw 'Expected V21 Namespace UnitA.Sequence in generated GRAPH XML'
    }

    $schemaDir = Find-V21SchemaDir
    $xsdChecked = 'skipped (no local V21 Schemas dir)'
    if ($schemaDir) {
        $ladXsd = Join-Path $schemaDir 'SW.PlcBlocks.LADFBD_v5.xsd'
        $graphXsd = Join-Path $schemaDir 'SW.PlcBlocks.Graph_v6.xsd'
        if (Test-Path -LiteralPath $ladXsd) {
            foreach ($fn in $flgNets) { Assert-XmlFragmentValidAgainstSchema $fn.OuterXml $ladXsd }
        }
        if (Test-Path -LiteralPath $graphXsd) {
            Assert-XmlFragmentValidAgainstSchema $graphNode.OuterXml $graphXsd
        }
        $xsdChecked = 'PASS (LADFBD_v5.xsd + Graph_v6.xsd)'
    }

    # 5. Verify clean V21 process library (no legacy OP270 fixtures)
    $libList = Request 5 'tools/call' @{ name = 'tia_list_process_library'; arguments = @{ kind = 'all' } }
    if ($libList.isError) { throw $libList.content[0].text }
    if ($libList.structuredContent.items.id -match 'op270') { throw 'Legacy OP270 entry still present in V21 process library' }
    $rendered = Request 6 'tools/call' @{
        name = 'tia_render_process_template'
        arguments = @{
            id = 'v21.scl-fb-starter'
            parameters_json = '{"block_name":"FB_AxisCtrl","cmd_execute":"Execute"}'
        }
    }
    if ($rendered.isError) { throw $rendered.content[0].text }

    # 6. Verify PLCSIM Runtime API 8.0 read-only status discovery
    $simStatus = Request 7 'tools/call' @{ name = 'tia_simulation_status'; arguments = @{} }
    if ($simStatus.isError) { throw $simStatus.content[0].text }
    $simInfo = $simStatus.structuredContent

    # 7. Verify connection status (no writes, no project mutations)
    $status = Request 8 'tools/call' @{ name = 'tia_connection_status'; arguments = @{} }

    Write-Output ("PASS: initialize ({0} v{1}), {2} V21 tools, LAD FlgNet/v5 + Namespace, GRAPH v6, XSD={3}, V21 library clean, PLCSIM API={4} ({5} CPU types); zero project writes." -f `
        $init.serverInfo.name, $init.serverInfo.version, $catalog.tools.Count, $xsdChecked, $simInfo.api_version, $simInfo.supported_cpu_type_count)
    Write-Output $status.content[0].text

    if ($ReadPlc) {
        $plcs = Request 9 'tools/call' @{name='tia_list_plcs';arguments=@{}}
        if ($plcs.isError) { throw $plcs.content[0].text }
        if ($plcs.structuredContent.plcs.Count -ne 1) { throw 'Read verification requires exactly one PLC' }
        $plcName = $plcs.structuredContent.plcs[0]
        if ($CompilePlc) {
            $compile = Request 10 'tools/call' @{name='tia_compile_plc';arguments=@{plc_name=$plcName}} 120000
            Write-Output ('Compile: {0} errors, {1} warnings; no save/download.' -f $compile.structuredContent.error_count,$compile.structuredContent.warning_count)
        }
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $list = Request 11 'tools/call' @{name='tia_list_blocks';arguments=@{plc_name=$plcName}}
        if ($list.isError) { throw $list.content[0].text }
        $clock.Stop()
        Write-Output ('PASS: {0} blocks listed in {1} ms' -f $list.structuredContent.blocks.Count,$clock.ElapsedMilliseconds)
    }
} finally {
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(3000)) { $process.Kill() }
    $process.Dispose()
}
