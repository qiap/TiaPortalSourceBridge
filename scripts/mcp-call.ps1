<#
.SYNOPSIS
    最小 MCP 客户端：直连 TIA Portal Source Bridge 的命名管道端点（mcp-host），
    完成 initialize 握手后调用指定工具。

.DESCRIPTION
    管道名 = TiaPortalSourceBridge-<当前用户 SID，'-' 换成 '_'>。
    协议为每行一条 JSON-RPC 2.0 消息（UTF-8，无 Content-Length 头）。

.EXAMPLE
    pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\mcp-call.ps1 -ListTools -SkipCall

.EXAMPLE
    pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\mcp-call.ps1 -Tool tia_connection_status
#>
[CmdletBinding()]
param(
    [string]$Tool = 'tia_connection_status',
    [string]$ArgumentsJson = '{}',
    # 从文件读参数（避免命令行传 JSON 时反斜杠被 shell 吞掉，Windows 上 5.1 -> 7 会掉一层）
    [string]$ArgumentsFile = '',
    [string]$ProjectPath = '',
    [int]$TimeoutSeconds = 600,
    # 把工具返回的文本正文写到文件（XML/源码太大，不经控制台）
    [string]$OutFile = '',
    [switch]$ListTools,
    [switch]$SkipCall
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)

$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_')
$pipeName = "TiaPortalSourceBridge-$sid"

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
    '.', $pipeName,
    [System.IO.Pipes.PipeDirection]::InOut,
    [System.IO.Pipes.PipeOptions]::Asynchronous)
try { $pipe.Connect(8000) }
catch { throw "无法连接 MCP 端点（$pipeName）：$($_.Exception.Message)。请确认看板或 StartMcp.bat 已启动。" }

$writer = New-Object System.IO.StreamWriter($pipe, $utf8, 65536, $true)
$writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($pipe, $utf8, $false, 65536, $true)

function Send-Message([hashtable]$payload) {
    $writer.WriteLine(($payload | ConvertTo-Json -Compress -Depth 30))
}

function Receive-Message([int]$timeout) {
    $task = $reader.ReadLineAsync()
    if (-not $task.Wait($timeout * 1000)) { throw "等待响应超时（$timeout 秒）" }
    if ($null -eq $task.Result) { throw 'MCP 端点已关闭连接。' }
    return $task.Result
}

function ConvertTo-Hashtable($value) {
    if ($null -eq $value) { return $null }
    if ($value -is [System.Management.Automation.PSCustomObject]) {
        $h = @{}
        foreach ($p in $value.PSObject.Properties) { $h[$p.Name] = ConvertTo-Hashtable $p.Value }
        return $h
    }
    if ($value -is [System.Collections.IEnumerable] -and $value -isnot [string]) {
        return @($value | ForEach-Object { ConvertTo-Hashtable $_ })
    }
    return $value
}

function Receive-Response($id, [int]$timeout) {
    while ($true) {
        $line = Receive-Message $timeout
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $msg = $line | ConvertFrom-Json
        if ($null -ne $msg.id -and "$($msg.id)" -eq "$id") { return $msg }
        # 服务端通知或其它消息，忽略后继续等
    }
}

try {
    # 1) 握手
    Send-Message @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{
        protocolVersion = '2025-06-18'
        capabilities = @{}
        clientInfo = @{ name = 'dsh-agent'; version = '1.0' } } }
    $init = Receive-Response 1 30
    if ($init.error) { throw "initialize 失败：$($init.error.message)" }
    $server = $init.result.serverInfo
    Write-Host "已连接: $($server.name) $($server.version)  (protocol $($init.result.protocolVersion))"

    Send-Message @{ jsonrpc = '2.0'; method = 'notifications/initialized' } | Out-Null

    # 2) 工具清单
    if ($ListTools) {
        Send-Message @{ jsonrpc = '2.0'; id = 2; method = 'tools/list'; params = @{} }
        $list = Receive-Response 2 30
        if ($list.error) { throw "tools/list 失败：$($list.error.message)" }
        $names = $list.result.tools | ForEach-Object { $_.name }
        Write-Host "工具数量: $($names.Count)"
        $names | Sort-Object | ForEach-Object { Write-Host "  $_" }
    }

    if ($SkipCall) { return }

    # 3) 调用工具
    if (-not [string]::IsNullOrWhiteSpace($ArgumentsFile)) {
        if (-not (Test-Path $ArgumentsFile)) { throw "参数文件不存在：$ArgumentsFile" }
        $jsonText = [System.IO.File]::ReadAllText($ArgumentsFile, $utf8)
        $toolArgs = ConvertTo-Hashtable ($jsonText | ConvertFrom-Json)
    }
    elseif ([string]::IsNullOrWhiteSpace($ArgumentsJson)) { $toolArgs = @{} }
    else { $toolArgs = ConvertTo-Hashtable ($ArgumentsJson | ConvertFrom-Json) }

    if (-not [string]::IsNullOrWhiteSpace($ProjectPath)) { $toolArgs['project_path'] = $ProjectPath }

    Write-Host "调用: $Tool  $($toolArgs | ConvertTo-Json -Compress -Depth 20)"
    Send-Message @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = $Tool; arguments = $toolArgs } }
    $call = Receive-Response 3 $TimeoutSeconds
    if ($call.error) { throw "tools/call 失败：$($call.error.message)" }

    $isError = $call.result.isError
    Write-Host ("isError: {0}" -f $isError)

    $payload = @()
    foreach ($item in $call.result.content) {
        if ($item.type -eq 'text') { $payload += $item.text }
    }

    if (-not [string]::IsNullOrWhiteSpace($OutFile)) {
        [System.IO.File]::WriteAllText($OutFile, ($payload -join [Environment]::NewLine), $utf8)
        Write-Host ("正文已写入: {0}  ({1:N1} KB)" -f $OutFile, ((Get-Item $OutFile).Length / 1KB))
    }
    else {
        foreach ($t in $payload) { Write-Output $t }
    }

    if ($call.result.structuredContent) {
        Write-Output '--- structuredContent ---'
        Write-Output ($call.result.structuredContent | ConvertTo-Json -Depth 30)
    }
    if ($isError) { exit 1 }
}
finally {
    try { $writer.Dispose() } catch { }
    try { $reader.Dispose() } catch { }
    try { $pipe.Dispose() } catch { }
}
