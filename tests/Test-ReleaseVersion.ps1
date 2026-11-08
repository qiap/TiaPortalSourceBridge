param([string]$MainExecutable, [string]$V21Executable)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = & (Join-Path $root 'scripts\Get-ReleaseVersion.ps1')
$checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Read([string]$Relative) { [IO.File]::ReadAllText((Join-Path $root $Relative)) }
Check ($version -match '^\d+\.\d+\.\d+$') 'Source release version must be semantic major.minor.patch.'
$source = Read 'shared\ProductVersion.cs'
Check ($source.Contains('public const string File = Release + ".0";')) 'File version must derive from the release constant.'
Check ($source.Contains('AssemblyFileVersion(TiaPortalSourceBridge.ProductVersion.File)') -and $source.Contains('AssemblyInformationalVersion(TiaPortalSourceBridge.ProductVersion.Release)')) 'Compiled metadata must use the release constant.'
foreach ($path in @('src\TiaPortalSourceBridge\Mcp\McpServer.cs', 'engines\v21\src\TiaPortalSourceBridge\Mcp\McpServer.cs')) {
    Check ((Read $path) -match '"serverInfo"[^\r\n]+"version",\s*ProductVersion\.Release') "MCP version is not tied to the source: $path"
}
Check ((Read 'src\TiaPortalSourceBridge\Web\WebServer.cs').Contains('"tool_version", "v" + ProductVersion.Release')) 'Dashboard API must report the compiled release version.'
$html = Read 'web\index.html'
Check ($html -notmatch 'id="toolVersion">v\d' -and $html.Contains("document.getElementById('toolVersion').textContent = data.tool_version")) 'Dashboard must use the API version instead of an independent hardcoded label.'
$package = Read 'scripts\Build-Installer.ps1'
Check ($package.Contains('Get-ReleaseVersion.ps1') -and $package.Contains('if ($Version -ne $releaseVersion)')) 'Packaging must derive and validate its version against the source.'
Check ($package.Contains('"/DPayloadDir=$payload"') -and $package.Contains('Compress-Archive -LiteralPath $payload')) 'Installer and ZIP must use the same payload.'
Check ($package.Contains('Test-ReleaseVersion.ps1') -and $package.Contains('-MainExecutable $main -V21Executable $v21')) 'Packaging must verify the exact engine binaries before building artifacts.'
$setup = Read 'installer\Setup.iss'
Check ($setup -notmatch '#define AppVersion\s+"\d' -and $setup.Contains('#error AppVersion is required')) 'Inno Setup must not silently substitute another release version.'
Check ((Read 'installer\INSTALL.txt') -notmatch '^TIA Portal Source Bridge\s+\d+\.\d+\.\d+') 'Installation instructions must not advertise a stale independent version.'
$legacy = Read 'scripts\Build-Portable.ps1'
Check ($legacy.Contains('[switch]$LegacyV17') -and $legacy.Contains('if (-not $LegacyV17)') -and $legacy.Contains('Legacy-V17-Portable-')) 'The old V17-only package must be explicitly identified and selected.'
Check ((Read 'engines\v21\src\TiaPortalSourceBridge\Properties\AssemblyInfo.cs').Contains('AssemblyVersion("4.0.0.0")')) 'Keep the V21 CLR binding version stable.'
Check ((Read 'src\TiaPortalSourceBridge\TiaPortalSourceBridge.csproj').Contains('<GenerateAssemblyFileVersionAttribute>false</GenerateAssemblyFileVersionAttribute>')) 'SDK builds must not generate a second file version attribute.'
foreach ($path in @('README.md', 'README.en.md')) {
    $text = Read $path
    Check ($text -notmatch 'releases/latest/download/TiaPortalSourceBridge-\d') "$path combines latest release with a fixed-version asset name."
    Check ($text.Contains('https://github.com/qiap/TiaPortalSourceBridge/releases')) "$path has no published-release entry point."
    foreach ($link in [regex]::Matches($text, 'releases/download/v(?<tag>\d+\.\d+\.\d+)/TiaPortalSourceBridge-(?<asset>\d+\.\d+\.\d+)-(Portable\.zip|Setup\.exe)')) {
        Check ($link.Groups['tag'].Value -eq $link.Groups['asset'].Value) "$path links an asset from a different release tag."
    }
}
foreach ($executable in @($MainExecutable, $V21Executable) | Where-Object { $_ }) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $executable).Path)
    Check ($info.FileVersion -eq "$version.0" -and $info.ProductVersion -eq $version) "Built binary version mismatch: $executable ($($info.FileVersion) / $($info.ProductVersion)); expected $version."
}
Write-Output "PASS: $checks release version checks ($version); no packaging, publishing or engineering connection."
