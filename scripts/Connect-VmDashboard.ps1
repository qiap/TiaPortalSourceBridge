param(
    [string]$Target = '',
    [int]$SshPort = 22,
    [int]$RemotePort = 18980,
    [int]$LocalPort = 0,
    [string]$VmToolDir = '',
    [string]$IdentityFile = '',
    [string]$ConnectionFile = '',
    [switch]$NoSave,
    [switch]$Configure,
    [switch]$NoBrowser,
    [switch]$PrintMcpConfig,
    [switch]$DryRun,
    [int]$WaitSeconds = 60
)
$ErrorActionPreference = 'Stop'
$utf8Out = [Text.UTF8Encoding]::new($false)
try {
    [Console]::OutputEncoding = $utf8Out
    $OutputEncoding = $utf8Out
} catch {}

$toolRoot = Split-Path -Parent $PSScriptRoot
$defaultConnFile = Join-Path $toolRoot 'vm-connection.json'
$useSavedFile = -not $NoSave
if ($PSBoundParameters.ContainsKey('ConnectionFile')) {
    if ([string]::IsNullOrWhiteSpace($ConnectionFile) -or $ConnectionFile -eq 'none') {
        $useSavedFile = $false
    }
} else {
    $ConnectionFile = $defaultConnFile
}

if ($useSavedFile -and (Test-Path -LiteralPath $ConnectionFile)) {
    try {
        $saved = Get-Content -LiteralPath $ConnectionFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $PSBoundParameters.ContainsKey('Target') -and $saved.target) { $Target = [string]$saved.target }
        if (-not $PSBoundParameters.ContainsKey('SshPort') -and $saved.sshPort) { $SshPort = [int]$saved.sshPort }
        if (-not $PSBoundParameters.ContainsKey('RemotePort') -and $saved.remotePort) { $RemotePort = [int]$saved.remotePort }
        if (-not $PSBoundParameters.ContainsKey('LocalPort') -and $saved.localPort) { $LocalPort = [int]$saved.localPort }
        if (-not $PSBoundParameters.ContainsKey('VmToolDir') -and $saved.vmToolDir) { $VmToolDir = [string]$saved.vmToolDir }
        if (-not $PSBoundParameters.ContainsKey('IdentityFile') -and $saved.identityFile) { $IdentityFile = [string]$saved.identityFile }
    } catch {
        Write-Warning ("读取已保存连接配置失败，将忽略旧配置: {0}" -f $_.Exception.Message)
    }
}

$shouldSave = $false
if ($Configure -or [string]::IsNullOrWhiteSpace($Target)) {
    if ($DryRun -and [string]::IsNullOrWhiteSpace($Target)) {
        throw 'DryRun 模式下需要提供 -Target（例如 Administrator@192.168.56.101）。'
    }
    Write-Host '================================================='
    Write-Host '  连接虚拟机博图看板（SSH 隧道向导）'
    Write-Host '================================================='
    $defaultHint = if (-not [string]::IsNullOrWhiteSpace($Target)) { " [当前: $Target]" } else { '' }
    $inputTarget = Read-Host ("请输入虚拟机 SSH 目标 (格式 user@vm-ip 或 ssh config 别名){0}" -f $defaultHint)
    if (-not [string]::IsNullOrWhiteSpace($inputTarget)) { $Target = $inputTarget.Trim() }
    if ([string]::IsNullOrWhiteSpace($Target)) { throw '未提供虚拟机 SSH 连接目标。' }

    $inputSshPort = Read-Host ("SSH 端口 [默认 {0}]" -f $SshPort)
    if (-not [string]::IsNullOrWhiteSpace($inputSshPort)) { $SshPort = [int]$inputSshPort.Trim() }

    $inputRemotePort = Read-Host ("虚拟机内看板端口 [默认 {0}]" -f $RemotePort)
    if (-not [string]::IsNullOrWhiteSpace($inputRemotePort)) {
        $RemotePort = [int]$inputRemotePort.Trim()
        if (-not $PSBoundParameters.ContainsKey('LocalPort')) { $LocalPort = $RemotePort }
    }

    $dirHint = if (-not [string]::IsNullOrWhiteSpace($VmToolDir)) { " [当前: $VmToolDir]" } else { ' [可选，用于生成 AI MCP 配置，直接回车跳过]' }
    $inputDir = Read-Host ("虚拟机内工具目录 (如 E:\TiaPortalSourceBridge){0}" -f $dirHint)
    if (-not [string]::IsNullOrWhiteSpace($inputDir)) { $VmToolDir = $inputDir.Trim().Trim('"') }
    $shouldSave = $true
} elseif ($PSBoundParameters.ContainsKey('Target') -and $useSavedFile -and -not (Test-Path -LiteralPath $ConnectionFile) -and -not $DryRun) {
    $shouldSave = $true
}

if ($LocalPort -le 0) {
    $LocalPort = $RemotePort
}

foreach ($p in @(@('SshPort', $SshPort), @('RemotePort', $RemotePort), @('LocalPort', $LocalPort))) {
    if ($p[1] -lt 1 -or $p[1] -gt 65535) {
        throw ("端口 {0} 无效: {1}" -f $p[0], $p[1])
    }
}

if ($LocalPort -ne $RemotePort) {
    Write-Warning ("本地端口 ({0}) 与虚拟机看板端口 ({1}) 不一致：VM 的 HttpListener 仅接受 Host=localhost:{1}，强烈建议保持两端口一致。" -f $LocalPort, $RemotePort)
}

if ($shouldSave -and $useSavedFile) {
    $payload = [ordered]@{
        target       = $Target
        sshPort      = $SshPort
        remotePort   = $RemotePort
        localPort    = $LocalPort
        vmToolDir    = $VmToolDir
        identityFile = $IdentityFile
    } | ConvertTo-Json
    $connDir = Split-Path -Parent $ConnectionFile
    if (-not [string]::IsNullOrWhiteSpace($connDir) -and -not (Test-Path -LiteralPath $connDir)) {
        New-Item -ItemType Directory -Path $connDir -Force | Out-Null
    }
    [IO.File]::WriteAllText($ConnectionFile, $payload + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-Host ('[VM] 已保存连接配置到 {0}（下次可直接双击“连接虚拟机看板.bat”）' -f $ConnectionFile)
}

function Format-SshCliArg([string]$Value) {
    if ($Value -match '[\s"]') {
        return '"' + ($Value -replace '"', '\"') + '"'
    }
    return $Value
}

function Build-SshTunnelArgs {
    $list = New-Object System.Collections.Generic.List[string]
    $list.Add('-N')
    $list.Add('-T')
    $list.Add('-o'); $list.Add('ExitOnForwardFailure=yes')
    $list.Add('-o'); $list.Add('ServerAliveInterval=15')
    $list.Add('-o'); $list.Add('ServerAliveCountMax=3')
    if ($SshPort -ne 22) {
        $list.Add('-p'); $list.Add("$SshPort")
    }
    if (-not [string]::IsNullOrWhiteSpace($IdentityFile)) {
        $list.Add('-i'); $list.Add($IdentityFile)
    }
    $list.Add('-L'); $list.Add(("127.0.0.1:{0}:127.0.0.1:{1}" -f $LocalPort, $RemotePort))
    $list.Add($Target)
    return @($list)
}

function Build-McpRemoteSnippet {
    $vmExe = if ([string]::IsNullOrWhiteSpace($VmToolDir)) {
        '<虚拟机工具目录>\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
    } else {
        $trimmed = $VmToolDir.TrimEnd('\', '/')
        $trimmed + '\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
    }
    $mcpArgs = New-Object System.Collections.Generic.List[string]
    $mcpArgs.Add('-T')
    if ($SshPort -ne 22) {
        $mcpArgs.Add('-p'); $mcpArgs.Add("$SshPort")
    }
    if (-not [string]::IsNullOrWhiteSpace($IdentityFile)) {
        $mcpArgs.Add('-i'); $mcpArgs.Add($IdentityFile)
    }
    $mcpArgs.Add($Target)
    $mcpArgs.Add(('"{0}" mcp-client' -f $vmExe))
    $doc = [ordered]@{
        mcpServers = [ordered]@{
            'tia-portal-vm' = [ordered]@{
                command = 'ssh'
                args    = @($mcpArgs)
            }
        }
    }
    return ($doc | ConvertTo-Json -Depth 10)
}

function Test-LoopbackPortBusy([int]$CheckPort) {
    if (Test-LocalTcpReady $CheckPort) {
        return $true
    }
    $probe = $null
    try {
        $probe = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Loopback, $CheckPort)
        $probe.Start()
        return $false
    } catch {
        return $true
    } finally {
        if ($null -ne $probe) {
            try { $probe.Stop() } catch {}
        }
    }
}

function Test-LocalTcpReady([int]$CheckPort) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $ar = $client.BeginConnect([Net.IPAddress]::Loopback, $CheckPort, $null, $null)
        if (-not $ar.AsyncWaitHandle.WaitOne(300)) { return $false }
        $client.EndConnect($ar)
        return $true
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

function Test-DashboardBuildEndpoint([int]$CheckPort) {
    $req = [Net.HttpWebRequest]::Create(("http://localhost:{0}/api/build" -f $CheckPort))
    $req.Proxy = $null
    $req.Timeout = 1500
    try {
        $resp = [Net.HttpWebResponse]$req.GetResponse()
        try {
            return ([int]$resp.StatusCode -eq 200)
        } finally {
            $resp.Close()
        }
    } catch {
        return $false
    }
}

$sshArgs = @(Build-SshTunnelArgs)
$dashboardUrl = "http://localhost:$LocalPort/"
$mcpSnippet = Build-McpRemoteSnippet

if ($DryRun) {
    Write-Host ("SSH_CMD: ssh {0}" -f (($sshArgs | ForEach-Object { Format-SshCliArg $_ }) -join ' '))
    Write-Host ("URL: {0}" -f $dashboardUrl)
    if ($PrintMcpConfig) {
        Write-Host 'MCP_CONFIG_JSON:'
        Write-Host $mcpSnippet
    }
    exit 0
}

if ($PrintMcpConfig -and $NoBrowser -and -not $PSBoundParameters.ContainsKey('Target') -and [string]::IsNullOrWhiteSpace($Target)) {
    Write-Host $mcpSnippet
    exit 0
}

$sshCmd = Get-Command 'ssh.exe' -ErrorAction SilentlyContinue
if ($null -eq $sshCmd) {
    $sshCmd = Get-Command 'ssh' -ErrorAction SilentlyContinue
}
if ($null -eq $sshCmd) {
    throw '宿主机未找到 ssh.exe（请在 Windows“可选功能”中启用 OpenSSH 客户端）。'
}

if (Test-LoopbackPortBusy $LocalPort) {
    throw ("宿主机本地端口 {0} 已被占用（可能是宿主机自己打开了看板）。请先退出宿主机看板，或让虚拟机与宿主机同时换用备用端口，例如 -RemotePort {1}。" -f $LocalPort, ($LocalPort + 1))
}

Write-Host ("正在建立 SSH 隧道到 {0} (localhost:{1} -> 虚拟机 127.0.0.1:{2})..." -f $Target, $LocalPort, $RemotePort)
Write-Host '（如首次连接，请按提示确认指纹或输入虚拟机用户密码）'

$argString = ($sshArgs | ForEach-Object { Format-SshCliArg $_ }) -join ' '
$sshProc = Start-Process -FilePath $sshCmd.Source -ArgumentList $argString -NoNewWindow -PassThru
$waitingVmMessageShown = $false
$tunnelBoundAt = $null

try {
    $authStart = [DateTime]::UtcNow
    $ready = $false
    while (-not $sshProc.HasExited) {
        if (Test-LocalTcpReady $LocalPort) {
            if ($null -eq $tunnelBoundAt) {
                $tunnelBoundAt = [DateTime]::UtcNow
            }
            if (Test-DashboardBuildEndpoint $LocalPort) {
                $ready = $true
                break
            }
            if (-not $waitingVmMessageShown) {
                Write-Host ('[VM] SSH 隧道已建立，但虚拟机端口 {0} 尚未响应看板；请在虚拟机双击“虚拟机启动看板.bat”，正在等待自动连入...' -f $RemotePort) -ForegroundColor Yellow
                $waitingVmMessageShown = $true
            }
            if (([DateTime]::UtcNow - $tunnelBoundAt).TotalSeconds -ge $WaitSeconds) {
                throw ("SSH 隧道已连通，但等待虚拟机看板端口 {0} 超时 ({1}s)。请确认虚拟机已启动看板且使用端口 {0}。" -f $RemotePort, $WaitSeconds)
            }
            Start-Sleep -Milliseconds 750
        } else {
            if (([DateTime]::UtcNow - $authStart).TotalSeconds -ge ($WaitSeconds + 60)) {
                throw ("等待 SSH 隧道绑定本地端口 {0} 超时。" -f $LocalPort)
            }
            Start-Sleep -Milliseconds 350
        }
    }

    if ($sshProc.HasExited -and -not $ready) {
        throw ("SSH 隧道启动失败并退出（退出码 {0}）。请核对：1) 虚拟机已开启 OpenSSH Server (sshd)；2) 虚拟机 IP / 用户名正确；3) 防火墙放行 TCP {1}。" -f $sshProc.ExitCode, $SshPort)
    }

    Write-Host ''
    Write-Host '================================================='
    Write-Host ("  已连接到虚拟机看板: {0}" -f $dashboardUrl)
    Write-Host '================================================='
    if ($PrintMcpConfig) {
        Write-Host ''
        Write-Host '宿主机 AI MCP 配置参考 (路线 1):'
        Write-Host $mcpSnippet
        Write-Host ''
    } else {
        Write-Host '提示: 运行“连接虚拟机看板.bat -PrintMcpConfig”可打印宿主机 AI 的 MCP 配置 JSON。'
    }

    if (-not $NoBrowser) {
        Start-Process -FilePath $dashboardUrl | Out-Null
    }

    Write-Host '保持此窗口打开以维持连接；按 Ctrl+C 或关闭窗口即可断开隧道（不会关闭虚拟机里的看板）。'
    while (-not $sshProc.HasExited) {
        Start-Sleep -Milliseconds 500
    }
    Write-Host ("SSH 隧道已断开（退出码 {0}）。" -f $sshProc.ExitCode)
} finally {
    if ($null -ne $sshProc -and -not $sshProc.HasExited) {
        try {
            $sshProc.Kill()
            $sshProc.WaitForExit(3000) | Out-Null
        } catch {}
    }
    if ($null -ne $sshProc) { $sshProc.Dispose() }
}
