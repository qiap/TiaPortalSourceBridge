param([switch]$Remove, [switch]$QuietIfNotAdmin)
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $QuietIfNotAdmin) {
    throw 'Administrator rights required. Run TrustThisTool.bat and approve the Windows UAC prompt.'
}

$exeCandidate = Join-Path $PSScriptRoot 'src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe'
if (-not $Remove -and -not (Test-Path -LiteralPath $exeCandidate)) {
    $buildBat = Join-Path $PSScriptRoot 'build.bat'
    & cmd.exe /c "`"$buildBat`""
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exeCandidate)) {
        throw 'Failed to build TiaPortalSourceBridge.exe before registering Openness V21 trust.'
    }
}

$baseOpennessKey = 'HKLM:\SOFTWARE\Siemens\Automation\Openness'
$appRoots = @(
    "$baseOpennessKey\AllowList\TiaPortalSourceBridge.exe",
    "$baseOpennessKey\21.0\Whitelist\TiaPortalSourceBridge.exe",
    "$baseOpennessKey\21.0\AllowList\TiaPortalSourceBridge.exe"
)

if ($Remove) {
    foreach ($appRoot in $appRoots) {
        foreach ($subName in @('Entry', 'SourceBridgeManaged', 'OnlineBin')) {
            $key = "$appRoot\$subName"
            if (Test-Path -LiteralPath $key) {
                Remove-Item -LiteralPath $key -Force
            }
        }
    }
    Write-Output 'Removed SourceBridge V21 Openness trust entries.'
    return
}

$exe = (Resolve-Path -LiteralPath $exeCandidate).Path
$onlineExeCandidate = Join-Path $PSScriptRoot 'online-bin\TiaPortalSourceBridge.exe'
$targets = @(
    @{ SubKey = 'Entry'; Path = $exe }
)
if (Test-Path -LiteralPath $onlineExeCandidate) {
    $onlineExe = (Resolve-Path -LiteralPath $onlineExeCandidate).Path
    if (-not [string]::Equals($onlineExe, $exe, [StringComparison]::OrdinalIgnoreCase)) {
        $targets += @{ SubKey = 'OnlineBin'; Path = $onlineExe }
    }
}

$updatedAny = $false
foreach ($target in $targets) {
    $targetPath = $target.Path
    $subName = $target.SubKey
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($targetPath)
    try { $hash = [Convert]::ToBase64String($algorithm.ComputeHash($stream)) }
    finally { $stream.Dispose(); $algorithm.Dispose() }
    $modified = (Get-Item -LiteralPath $targetPath).LastWriteTimeUtc.ToString('yyyy/MM/dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)

    foreach ($appRoot in $appRoots) {
        $key = "$appRoot\$subName"
        try {
            if (Test-Path -LiteralPath $appRoot) {
                foreach ($existingSub in Get-ChildItem -LiteralPath $appRoot -ErrorAction SilentlyContinue) {
                    if ($existingSub.PSChildName -ne $subName) {
                        $existingProps = Get-ItemProperty -LiteralPath $existingSub.PSPath -ErrorAction SilentlyContinue
                        if ($existingProps -and [string]::Equals([string]$existingProps.Path, $targetPath, [StringComparison]::OrdinalIgnoreCase)) {
                            Remove-Item -LiteralPath $existingSub.PSPath -Force -ErrorAction Stop
                        }
                    }
                }
            }
            $null = New-Item -Path $key -Force -ErrorAction Stop
            foreach ($item in @{Path=$targetPath; DateModified=$modified; FileHash=$hash}.GetEnumerator()) {
                $null = New-ItemProperty -LiteralPath $key -Name $item.Key -Value $item.Value -PropertyType String -Force -ErrorAction Stop
            }
            $updatedAny = $true
        } catch {
            if (-not $QuietIfNotAdmin) { throw }
        }
    }
}

if ($updatedAny) {
    Write-Output 'Trusted this exact SourceBridge V21 build for TIA Openness 21.0:'
    Write-Output $exe
} elseif ($QuietIfNotAdmin) {
    Write-Output '[INFO] Skipping Openness 21.0 registry whitelist update (requires elevation). Run TrustThisTool.bat if TIA Portal V21 prompts for access.'
}
