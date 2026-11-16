$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts\VmAutoConnect.ps1')
$checks = 0
function Check($Condition, $Message) { if (-not $Condition) { throw $Message }; $script:checks++ }
$testRoot = Join-Path $env:TEMP ('tia-vm-auto-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$access = Join-Path $testRoot 'vm-access'
$identity = [pscustomobject]@{hostComputer=$env:COMPUTERNAME; identityFile=(Join-Path $testRoot 'test-key'); fingerprint='SHA256:test-pair'}
[IO.File]::WriteAllText($identity.identityFile, 'fixture-not-a-private-key')
Write-VmJson (Join-Path $access 'host-identity.json') $identity
$loaded = Initialize-VmHostIdentity $testRoot
Check ($loaded.identityFile -eq $identity.identityFile) 'Existing host identity must be reused without regenerating keys'
$endpoint = @{schema=1;vmComputer='fixture-vm';user='admin';addresses=@('192.0.2.1','192.0.2.2');sshPort=22;remotePort=18980;pairedFingerprint=$identity.fingerprint;hostKeys=@('ssh-ed25519 AAAATEST');toolDir='\\vmware-host\Shared Folders\WORK\TiaPortalSourceBridge';executable='\\vmware-host\Shared Folders\WORK\TiaPortalSourceBridge\bin\TiaPortalSourceBridge.exe'}
Write-VmJson (Join-Path $access 'vm-endpoint.json') $endpoint
function Test-VmTcp([string]$Address, [int]$Port) { return $Address -eq '192.0.2.2' }
$resolved = Resolve-VmEndpoint $testRoot $identity
Check ($resolved.target -eq 'admin@192.0.2.2') 'Choose a reachable VM adapter instead of the first adapter'
Check ((Get-Content -LiteralPath $resolved.knownHosts -Raw).Contains('192.0.2.2 ssh-ed25519 AAAATEST')) 'Host identity is pinned from the shared public record'
$endpoint.addresses = @('192.0.2.99'); Write-VmJson (Join-Path $access 'vm-endpoint.json') $endpoint
function Test-VmTcp([string]$Address, [int]$Port) { return $Address -eq '192.0.2.99' }
$moved = Resolve-VmEndpoint $testRoot $identity
Check ($moved.target -eq 'admin@192.0.2.99') 'A VM IP change must update the target without prompting'
$endpoint.pairedFingerprint='SHA256:other'; Write-VmJson (Join-Path $access 'vm-endpoint.json') $endpoint
$rejected=$false
try { Resolve-VmEndpoint $testRoot $identity | Out-Null } catch { $rejected=$true }
Check $rejected 'An unpaired VM record must be rejected'
$endpoint.pairedFingerprint=$identity.fingerprint; $endpoint.hostKeys=@('bad-key'); Write-VmJson (Join-Path $access 'vm-endpoint.json') $endpoint
$rejected=$false
try { Resolve-VmEndpoint $testRoot $identity | Out-Null } catch { $rejected=$true }
Check $rejected 'Malformed host keys must be rejected'
# Load only the real command builders, without starting tunnels or touching saved settings.
$parseTokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'scripts\Connect-VmDashboard.ps1'),[ref]$parseTokens,[ref]$parseErrors)
foreach ($name in @('Build-SshTunnelArgs','Build-McpRemoteSnippet')) {
    $fn=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    Invoke-Expression $fn.Extent.Text
}
$automatic=$true; $IdentityFile=$identity.identityFile; $knownHosts=$resolved.knownHosts
$Target='admin@192.0.2.99'; $SshPort=22; $RemotePort=18980; $LocalPort=18980
$discoveredExe=$endpoint.executable; $VmToolDir=$endpoint.toolDir
$sshArgs=@(Build-SshTunnelArgs)
Check ('BatchMode=yes' -in $sshArgs) 'Automatic connection must never ask for passwords'
Check ('StrictHostKeyChecking=yes' -in $sshArgs) 'Automatic connection must verify the pinned host key'
Check ('IdentitiesOnly=yes' -in $sshArgs) 'Automatic connection must select the dedicated identity'
$mcp=Build-McpRemoteSnippet | ConvertFrom-Json
Check ($mcp.mcpServers.'tia-portal-vm'.args[-1] -eq ('"' + $discoveredExe + '" mcp-client')) 'MCP must use the discovered actual executable, including UNC paths with spaces'
Check ('BatchMode=yes' -in $mcp.mcpServers.'tia-portal-vm'.args) 'AI MCP authentication must also be noninteractive'
# Normal restarts must not read protected ProgramData SSH files at all.
$restartRoot=Join-Path $testRoot 'restart'
$fingerprint='SHA256:restart-pair'
Write-VmJson (Join-Path $restartRoot 'vm-access\enrollment.json') @{hostComputer='fixture-other-host';fingerprint=$fingerprint}
Write-VmJson (Join-Path $restartRoot 'vm-access\vm-endpoint.json') @{
    schema=1;vmComputer=$env:COMPUTERNAME;user=$env:USERNAME;pairedFingerprint=$fingerprint;hostKeys=@('ssh-ed25519 AAAARESTART')
}
function Save-VmHostPublicKeys { throw [UnauthorizedAccessException]::new('Protected SSH files cannot be read by this normal user.') }
Publish-VmEndpoint $restartRoot 'C:\Bridge\bridge.exe' 18980 @('192.0.2.10')
$published=Get-Content (Join-Path $restartRoot 'vm-access\vm-endpoint.json') -Raw | ConvertFrom-Json
Check ($published.hostKeys[0] -eq 'ssh-ed25519 AAAARESTART') 'Existing install migrates public keys without protected reads'
Check (Test-Path (Join-Path $restartRoot 'vm-access\vm-host-public-keys.json')) 'Migration persists normal-user public key cache'
Publish-VmEndpoint $restartRoot 'C:\Bridge\bridge.exe' 18980 @('192.0.2.11')
$published=Get-Content (Join-Path $restartRoot 'vm-access\vm-endpoint.json') -Raw | ConvertFrom-Json
Check ($published.addresses[0] -eq '192.0.2.11') 'Repeated normal-user restart refreshes IP while retaining public keys'
$rejected=$false
try { Get-VmHostPublicKeys $restartRoot 'SHA256:wrong-pair' | Out-Null } catch { $rejected=$true }
Check $rejected 'A public key cache from a different pairing must not be reused'
Check ((Resolve-VmLocalPort 19980 18980) -eq 19980) 'Explicit local port must survive discovery'
Check ((Resolve-VmLocalPort 0 18980) -eq 18980) 'Unset local port follows discovered port'
$otherIdentity=[pscustomobject]@{fingerprint='SHA256:another-vm';identityFile=$identity.identityFile}
Check ((Get-VmPairId $identity) -ne (Get-VmPairId $otherIdentity)) 'VM pair storage IDs must be isolated'
$endpoint.pairedFingerprint=$identity.fingerprint; $endpoint.hostKeys=@('ssh-ed25519 AAAATEST')
Write-VmJson (Join-Path $access 'vm-endpoint.json') $endpoint
$first=Resolve-VmEndpoint $testRoot $identity
$firstText=[IO.File]::ReadAllText($first.knownHosts)
$endpoint.pairedFingerprint=$otherIdentity.fingerprint; $endpoint.hostKeys=@('ssh-ed25519 AAAAOTHER')
Write-VmJson (Join-Path $access 'vm-endpoint.json') $endpoint
$second=Resolve-VmEndpoint $testRoot $otherIdentity
Check ($first.knownHosts -ne $second.knownHosts) 'Two VM records must not overwrite known_hosts'
Check ([IO.File]::ReadAllText($first.knownHosts) -eq $firstText) 'Connecting another VM preserves first host keys'
$startup=Get-Content (Join-Path $root 'scripts\Start-VmDashboard.ps1') -Raw
Check ($startup.Contains('if (-not $LocalOnly -and ($InitializeAccessOnly -or -not $CheckOnly))')) 'Local-only launch skips pairing prerequisites'
Check ($startup.Contains('if (-not $LocalOnly) { Publish-VmEndpoint')) 'Local-only launch does not publish pairing'
Write-Output "PASS: $checks automatic VM connection checks. Fixtures: $testRoot"
