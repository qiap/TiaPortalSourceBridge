param([string]$MainExecutable, [string]$V21Executable)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $MainExecutable -or -not $V21Executable) {
    $stage = Join-Path $root '.work\regression-build'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build-online.ps1') -OutputDirectory (Join-Path $stage 'main') -NoSync
    if ($LASTEXITCODE -ne 0) { throw 'Main build failed' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'engines\v21\build-online.ps1') -OutputDirectory (Join-Path $stage 'v21') -NoSync
    if ($LASTEXITCODE -ne 0) { throw 'V21 build failed' }
    $MainExecutable = Join-Path $stage 'main\TiaPortalSourceBridge.exe'
    $V21Executable = Join-Path $stage 'v21\TiaPortalSourceBridge.exe'
}
# Shared components must never acquire a second independently maintained copy.
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-BuildAssets.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Repeated build asset checks failed' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-BuildCache.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Build cache checks failed' }
$shared = Join-Path $root 'shared'
foreach ($file in Get-ChildItem $shared -Recurse -Filter '*.cs') {
    $relative = $file.FullName.Substring($shared.Length + 1)
    foreach ($engine in @('src\TiaPortalSourceBridge', 'engines\v21\src\TiaPortalSourceBridge')) {
        if (Test-Path -LiteralPath (Join-Path (Join-Path $root $engine) $relative)) { throw ('Duplicate shared source: ' + $relative) }
    }
}
foreach ($test in @('Test-WorkbenchServer.ps1','Test-LogicCompare.ps1','Test-BlockRead.ps1','Test-ToolText.ps1','Test-BrowserLifetime.ps1','Test-Reliability.ps1','Test-Performance.ps1','Test-Index.ps1','Test-Index-Scope.ps1','Test-HmiGuard.ps1','Test-Io.ps1','Test-IoReconcile.ps1','Test-SemanticDiff.ps1','Test-SourceReview.ps1','Test-TagReview.ps1','Test-ReviewServer.ps1','Test-OnlineMonitor.ps1','Test-ProjectTexts.ps1','Test-SourceWrite.ps1','Test-Peripheral.ps1','Test-Scenario.ps1','Test-AdvancedRuntime.ps1','Test-GraphMonitor.ps1','Test-HmiCompanion.ps1','Test-HmiEditor.ps1','Test-CylinderApply.ps1','Test-CylinderAlarmReset.ps1','Test-CylinderBatch.ps1','Test-CylinderTemplates.ps1','Test-CylinderApplyServer.ps1','Test-CylinderRecovery.ps1','Test-ProjectOperationGuard.ps1','Test-ProgramAudit.ps1','Test-SclWriteAudit.ps1','Test-CallGraphAudit.ps1','Test-AuditWorkbook.ps1','Test-ProgramAuditServer.ps1')) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $test)
    if ($LASTEXITCODE -ne 0) { throw ($test + ' failed') }
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-ReleaseVersion.ps1') -MainExecutable $MainExecutable -V21Executable $V21Executable
if ($LASTEXITCODE -ne 0) { throw 'Release version checks failed' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-VmTunnel.ps1') -MainExecutable $MainExecutable
if ($LASTEXITCODE -ne 0) { throw 'VM tunnel checks failed' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-VmAutoConnect.ps1')
if ($LASTEXITCODE -ne 0) { throw 'VM automatic connection checks failed' }
& python (Join-Path $PSScriptRoot 'Test-VersionRouting.py') $MainExecutable $V21Executable
if ($LASTEXITCODE -ne 0) { throw 'Version routing checks failed' }
& python (Join-Path $PSScriptRoot 'Test-BlockReadProtocol.py') $MainExecutable $V21Executable
if ($LASTEXITCODE -ne 0) { throw 'Block read protocol checks failed' }
foreach ($test in @('Test-IoLayoutTheme.cjs','Test-BlockReadable.cjs','Test-WorkbenchSimulation.cjs','Test-WorkbenchCompare.cjs','Test-NavigationLayout.cjs','Test-PortalStatus.cjs', 'Test-Workspace.cjs', 'Test-Index-Scope.cjs', 'Test-ProjectDropdown.cjs', 'Test-IoPanel.cjs', 'Test-IoReconcile.cjs', 'Test-SemanticDiff.cjs', 'Test-SourceReview.cjs', 'Test-TagReview.cjs', 'Test-OnlineMonitor.cjs', 'Test-EngineeringPanels.cjs', 'Test-HmiEditor.cjs', 'Test-Cylinder.cjs', 'Test-Cylinder-Extraction.cjs', 'Test-Cylinder-Naming.cjs', 'Test-Cylinder-Apply.cjs', 'Test-Execution-Recovery.cjs', 'Test-Cylinder-Templates.cjs', 'Test-Cylinder-Batch.cjs', 'Test-ProgramAudit.cjs', 'Test-CallGraph.cjs')) {
    & node (Join-Path $PSScriptRoot $test)
    if ($LASTEXITCODE -ne 0) { throw ($test + ' failed') }
}
Write-Output 'PASS: all offline regression checks. No engineering connection or PLC writes.'
