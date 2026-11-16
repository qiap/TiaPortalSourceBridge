param(
    [int]$Port = 18980,
    [string]$Project = '',
    [string]$Config = '',
    [switch]$OpenBrowser,
    [switch]$SetupSsh,
    [switch]$CheckOnly,
    [switch]$InitializeAccessOnly
)
$ErrorActionPreference = 'Stop'
$utf8Out = [Text.UTF8Encoding]::new($false)
try {
    [Console]::OutputEncoding = $utf8Out
    $OutputEncoding = $utf8Out
} catch {}

$toolRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'VmAutoConnect.ps1')
if ([string]::IsNullOrWhiteSpace($Config)) {
    $Config = Join-Path $toolRoot 'config.json'
}
$Config = [IO.Path]::GetFullPath($Config)

function Find-BridgeExecutable {
    $candidates = @(
        (Join-Path $toolRoot 'src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'),
        (Join-Path $toolRoot 'online-bin\TiaPortalSourceBridge.exe'),
        (Join-Path $toolRoot 'bin\TiaPortalSourceBridge.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    $buildScript = Join-Path $toolRoot 'build-online.ps1'
    if (Test-Path -LiteralPath $buildScript) {
        Write-Host '[VM] 未找到已编译程序，正在编译 TiaPortalSourceBridge.exe ...'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $buildScript
        if ($LASTEXITCODE -ne 0) { throw '编译失败，无法启动虚拟机看板。' }
        foreach ($candidate in $candidates) {
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }
    throw '找不到 TiaPortalSourceBridge.exe，请先完整解压运行包或执行 build.bat。'
}

function Ensure-ConfigFile([string]$ConfigPath) {
    if (Test-Path -LiteralPath $ConfigPath) { return }
    $dir = Split-Path -Parent $ConfigPath
    if (-not [string]::IsNullOrWhiteSpace($dir) -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $defaultConfig = [ordered]@{
        tiaPortalVersion     = 'V17'
        projectPath          = '.\SelectProject.ap17'
        plcName              = ''
        sourceDirectory      = '.\plc-src'
        siemensEngineeringDll = ''
        connectionMode       = 'AttachOnly'
        withUserInterface    = $true
        saveProjectAfterImport = $false
        compileBeforeExport  = $false
        includeSystemBlocks  = $false
        continueOnError      = $true
        allowProjectUpgrade  = $false
    } | ConvertTo-Json
    [IO.File]::WriteAllText($ConfigPath, $defaultConfig + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-Host ("[VM] 已生成默认配置文件: {0}" -f $ConfigPath)
}

function Get-InstalledTiaVersions {
    $versions = New-Object System.Collections.Generic.List[string]
    $regPath = 'HKLM:\SOFTWARE\Siemens\Automation\_InstalledSW'
    if (Test-Path -LiteralPath $regPath) {
        foreach ($item in (Get-ChildItem -LiteralPath $regPath -ErrorAction SilentlyContinue)) {
            if ($item.PSChildName -match '^TIAP\d+') { $versions.Add($item.PSChildName) }
        }
    }
    $autoDir = 'C:\Program Files\Siemens\Automation'
    if (Test-Path -LiteralPath $autoDir) {
        foreach ($dir in (Get-ChildItem -LiteralPath $autoDir -Directory -Filter 'Portal V*' -ErrorAction SilentlyContinue)) {
            if (-not $versions.Contains($dir.Name)) { $versions.Add($dir.Name) }
        }
    }
    return @($versions)
}

function Get-UsableIpv4Addresses {
    $ips = New-Object System.Collections.Generic.List[string]
    try {
        $interfaces = [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
            Where-Object {
                $_.OperationalStatus -eq [Net.NetworkInformation.OperationalStatus]::Up -and
                $_.NetworkInterfaceType -ne [Net.NetworkInformation.NetworkInterfaceType]::Loopback
            }
        foreach ($nic in $interfaces) {
            foreach ($addr in $nic.GetIPProperties().UnicastAddresses) {
                if ($addr.Address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) {
                    $ip = $addr.Address.IPAddressToString
                    if ($ip -and -not $ip.StartsWith('127.') -and -not $ip.StartsWith('169.254.')) {
                        if (-not $ips.Contains($ip)) { $ips.Add($ip) }
                    }
                }
            }
        }
    } catch {}
    return @($ips)
}

function Enable-VmOpenSsh {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '启用 OpenSSH Server 需要管理员权限。请右键“虚拟机启用SSH.bat”并选择“以管理员身份运行”！'
    }
    $svc = Get-Service -Name 'sshd' -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-Host '[SSH] 正在安装 Windows OpenSSH Server ...'
        $cap = Get-WindowsCapability -Online | Where-Object { $_.Name -like 'OpenSSH.Server*' } | Select-Object -First 1
        if ($null -eq $cap) { throw '未找到 OpenSSH.Server 系统功能包，请检查 Windows 版本或手动安装 Win32-OpenSSH。' }
        Add-WindowsCapability -Online -Name $cap.Name | Out-Null
    }
    Set-Service -Name 'sshd' -StartupType Automatic
    Start-Service -Name 'sshd'
    $rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
    if ($null -eq $rule) {
        New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' `
            -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 | Out-Null
    } elseif ($rule.Enabled -ne 'True') {
        Enable-NetFirewallRule -Name 'OpenSSH-Server-In-TCP'
    }
    Write-Host '[SSH] OpenSSH Server (sshd) 已启动，防火墙 TCP 22 已放行。'
}

function Get-VmTiaAccess {
    $group = Get-LocalGroup -Name 'Siemens TIA Openness' -ErrorAction SilentlyContinue
    if ($null -eq $group) { return @{ installed=$false; member=$false; tokenReady=$false } }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]$identity
    $tokenReady = $principal.IsInRole($group.SID)
    $members = @(Get-LocalGroupMember -Group $group.Name -ErrorAction Stop)
    return @{ installed=$true; member=($tokenReady -or $identity.User.Value -in @($members | ForEach-Object { $_.SID.Value })); tokenReady=$tokenReady }
}

function Initialize-VmTiaAccess {
    $access = Get-VmTiaAccess
    if (-not $access.installed) { return }
    if (-not $access.member) {
        Add-LocalGroupMember -Group 'Siemens TIA Openness' -Member ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
        Write-Host '[VM] 已加入 Siemens TIA Openness 用户组；请保存工程后注销并重新登录虚拟机一次。'
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $toolRoot 'TrustThisTool.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'TIA Openness 程序白名单注册失败。' }
}

if ($Port -lt 1 -or $Port -gt 65535) {
    throw "无效端口号: $Port"
}

if ($SetupSsh) {
    Enable-VmOpenSsh
}

if ($InitializeAccessOnly -or -not $CheckOnly) {
    $enrollmentPath = Join-Path $toolRoot 'vm-access\enrollment.json'
    if (-not (Test-Path -LiteralPath $enrollmentPath)) {
        throw '请先在宿主机双击一次“连接虚拟机看板.bat”准备连接，再启动虚拟机看板。无需填写参数。'
    }
    $enrollment = Get-Content -LiteralPath $enrollmentPath -Raw | ConvertFrom-Json
    if ($enrollment.hostComputer -eq $env:COMPUTERNAME) { throw '请在虚拟机里面运行“虚拟机启动看板.bat”；宿主机请用“连接虚拟机看板.bat”。' }
    $endpointPath = Join-Path $toolRoot 'vm-access\vm-endpoint.json'
    $previous = if (Test-Path -LiteralPath $endpointPath) { Get-Content -LiteralPath $endpointPath -Raw | ConvertFrom-Json } else { $null }
    $service = Get-Service sshd -ErrorAction SilentlyContinue
    $tiaAccess = Get-VmTiaAccess
    $needsSetup = $InitializeAccessOnly -or $null -eq $service -or $service.Status -ne 'Running' -or
        $null -eq $previous -or $previous.vmComputer -ne $env:COMPUTERNAME -or
        $previous.user -ne $env:USERNAME -or $previous.pairedFingerprint -ne $enrollment.fingerprint -or
        ($tiaAccess.installed -and -not $tiaAccess.member)
    if ($needsSetup) {
        $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            Write-Host '[VM] 首次准备 SSH 和宿主机授权，请确认管理员提示；以后无需填写连接参数。'
            $argsText = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -InitializeAccessOnly -CheckOnly'
            $setup = Start-Process powershell.exe -ArgumentList $argsText -Verb RunAs -WindowStyle Hidden -Wait -PassThru
            if ($setup.ExitCode -ne 0) { throw '首次 SSH 准备未完成。请在虚拟机内右键“虚拟机启动看板.bat”，以管理员身份运行查看原因。' }
        } else {
            Enable-VmOpenSsh
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Install-VmHostKey.ps1')
            if ($LASTEXITCODE -ne 0) { throw '宿主机公钥安装失败。' }
            Initialize-VmTiaAccess
        }
    }
    if ($InitializeAccessOnly) { exit 0 }
    $tiaAccess = Get-VmTiaAccess
    if ($tiaAccess.installed -and -not $tiaAccess.tokenReady) {
        throw 'TIA Openness 权限已配置，但当前 Windows 登录会话尚未刷新。请先保存工程，再注销并重新登录虚拟机一次，随后双击启动看板。无需重装或重填连接参数。'
    }
}

$exe = Find-BridgeExecutable
Ensure-ConfigFile $Config

$tiaVersions = @(Get-InstalledTiaVersions)
$sshd = Get-Service -Name 'sshd' -ErrorAction SilentlyContinue
$ips = @(Get-UsableIpv4Addresses)
$displayIp = if ($ips.Count -gt 0) { $ips[0] } else { '<虚拟机IP>' }
$user = $env:USERNAME

Write-Host '================================================='
Write-Host '  TIA Portal Source Bridge - 虚拟机宿主入口'
Write-Host '================================================='
Write-Host ("程序位置 : {0}" -f $exe)
Write-Host ("配置文件 : {0}" -f $Config)
Write-Host ("看板端口 : 127.0.0.1:{0}" -f $Port)

if ($tiaVersions.Count -gt 0) {
    Write-Host ("博图环境 : 已检测到 ({0})" -f ($tiaVersions -join ', '))
} else {
    Write-Host '博图环境 : 未检测到本机 TIA Portal（离线看图、Diff 审查、SSH 通道与看板界面仍可正常工作；连接/导出/写入工程需在虚拟机安装博图后可用）'
}

if ($null -ne $sshd -and $sshd.Status -eq 'Running') {
    Write-Host 'SSH 服务 : sshd 运行中'
} elseif ($null -ne $sshd) {
    Write-Host ('SSH 服务 : sshd 当前状态为 {0}（请右键“虚拟机启用SSH.bat”以管理员身份运行）' -f $sshd.Status) -ForegroundColor Yellow
} else {
    Write-Host 'SSH 服务 : 未安装 OpenSSH Server（请右键“虚拟机启用SSH.bat”以管理员身份运行）' -ForegroundColor Yellow
}

if ($ips.Count -gt 0) {
    Write-Host ("虚拟机IP : {0}" -f ($ips -join ', '))
} else {
    Write-Host '虚拟机IP : 未检测到非回环 IPv4（请确认虚拟机网卡已启用 Host-Only / NAT / 桥接）' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '宿主机：直接双击“连接虚拟机看板.bat”。'
Write-Host '用户名、地址、端口和公钥由共享目录自动传递，无需手动填写。'
Write-Host ''

if ($CheckOnly) {
    exit 0
}

Publish-VmEndpoint $toolRoot $exe $Port $ips
Write-Host '[VM] 已自动发布连接信息；宿主机直接双击“连接虚拟机看板.bat”即可，无需输入 IP、用户名或密码。'
try {
    $request = [Net.HttpWebRequest]::Create(('http://localhost:{0}/api/build' -f $Port))
    $request.Proxy = $null
    $request.Timeout = 1000
    $response = $request.GetResponse()
    $response.Close()
    Write-Host '[VM] 看板已经运行，已更新连接信息。'
    if ($OpenBrowser) { Start-Process ('http://localhost:{0}/' -f $Port) | Out-Null }
    exit 0
} catch {}

$bridgeArgs = @('web', '--config', $Config, '--port', "$Port")
if (-not $OpenBrowser) {
    $bridgeArgs += '--no-browser'
}
if (-not [string]::IsNullOrWhiteSpace($Project)) {
    $bridgeArgs += @('--project', $Project)
}

Write-Host '正在虚拟机内启动看板与后台 MCP 服务（按 Ctrl+C 停止）...'
& $exe @bridgeArgs
exit $LASTEXITCODE
