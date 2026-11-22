$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo '.work\sdk-resolution'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$fixture = Join-Path $output ([guid]::NewGuid().ToString('N'))
$files = @('shared\PortalVersions.cs','shared\Openness\OpennessSdk.cs','src\TiaPortalSourceBridge\Configuration\AppConfig.cs','tests\OpennessSdkChecks.cs') | ForEach-Object { Join-Path $repo $_ }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$metrics = Join-Path $repo 'shared\Openness\OperationMetrics.cs'
foreach ($edition in @('main','v21')) {
    $source = if ($edition -eq 'main') { 'src' } else { 'engines\v21\src' }
    $loader = Join-Path $repo "$source\TiaPortalSourceBridge\Openness\ReflectionApi.cs"
    $binary = Join-Path $output ("OpennessSdkChecks-" + $edition + ".exe")
    & $compiler /nologo /utf8output /codepage:65001 /target:exe /platform:x64 ("/out:" + $binary) @files $loader $metrics
    if ($LASTEXITCODE -ne 0) { throw "SDK checks for $edition failed to compile." }
}
try {
    $classic = Join-Path $output 'OpennessSdkChecks-main.exe'
    & $classic $fixture
    if ($LASTEXITCODE -ne 0) { throw 'SDK checks failed.' }
    foreach ($major in 16..21) {
        $binary = if ($major -eq 21) { Join-Path $output 'OpennessSdkChecks-v21.exe' } else { $classic }
        & $binary inspect ("V" + $major)
        if ($LASTEXITCODE -ne 0) { throw "Installed V$major SDK inspection failed." }
    }
} finally {
    $expected = [IO.Path]::GetFullPath($output).TrimEnd('\') + '\'
    $target = [IO.Path]::GetFullPath($fixture)
    if ($target.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $target)) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
