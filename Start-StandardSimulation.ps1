param([Parameter(Mandatory=$true)][string]$ProjectPath,[string]$OutputDirectory,[switch]$InspectOnly)
$ErrorActionPreference='Stop'
$ProjectPath=(Resolve-Path -LiteralPath $ProjectPath).Path
if([IO.Path]::GetExtension($ProjectPath) -notmatch '^\.ap(?<version>\d+)$'){throw 'Expected an .apNN TIA Portal project file'}
$version=$Matches.version
$target=$null
$shortcut=Join-Path $env:ProgramData ('Microsoft\Windows\Start Menu\Programs\Siemens Automation\S7-PLCSIM V'+$version+'\S7-PLCSIM V'+$version+'.lnk')
if(Test-Path -LiteralPath $shortcut){
    $shell=New-Object -ComObject WScript.Shell
    $link=$shell.CreateShortcut($shortcut)
    if($link.Arguments){throw 'Installed simulator shortcut requires arguments; inspect its startup contract before launching'}
    $target=$link.TargetPath
}
if(-not $target){
    $displayName='SIMATIC S7-PLCSIM V'+$version
    $entries=@(Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue|Where-Object {$_.DisplayName -eq $displayName -and $_.InstallLocation})
    $paths=@($entries|ForEach-Object {Join-Path $_.InstallLocation 'Bin\Siemens.Simatic.PlcSim.Compact.exe'}|Where-Object {Test-Path -LiteralPath $_}|Select-Object -Unique)
    if($paths.Count -ne 1){throw ('Cannot uniquely locate the installed standard S7-PLCSIM V'+$version+'; Advanced will not be selected as a fallback')}
    $target=$paths[0]
}
$target=(Resolve-Path -LiteralPath $target).Path
if([IO.Path]::GetFileName($target) -ne 'Siemens.Simatic.PlcSim.Compact.exe'){throw 'Unexpected standard PLCSIM executable'}
$report=[ordered]@{project_path=$ProjectPath;backend='standard';portal_version=('V'+$version);executable=$target;process_id=$null;process_running=$false;download_verified=$false;runtime_tested=$false}
if(-not $InspectOnly){
    $running=@(Get-CimInstance Win32_Process -Filter "Name='Siemens.Simatic.PlcSim.Compact.exe'"|Where-Object {$_.ExecutablePath -eq $target})
    if($running.Count -eq 0){
        # Explicit simulator launch requested by the user; show its normal interactive window.
        $process=Start-Process -FilePath $target -WorkingDirectory (Split-Path -Parent $target) -WindowStyle Normal -PassThru
        $report.process_id=$process.Id
        Start-Sleep -Milliseconds 1500
        $running=@(Get-CimInstance Win32_Process -Filter "Name='Siemens.Simatic.PlcSim.Compact.exe'"|Where-Object {$_.ExecutablePath -eq $target})
    }
    if($running.Count -eq 0){throw 'Simulator process did not stay running; startup was not verified'}
    $report.process_id=@($running.ProcessId);$report.process_running=$true
}
if($OutputDirectory){
    New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
    [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'standard-launch.json'),($report|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
}
$report|ConvertTo-Json -Depth 10
