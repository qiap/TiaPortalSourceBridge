$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path -Parent $PSScriptRoot) 'shared\ProductVersion.cs'
$versions = [regex]::Matches([IO.File]::ReadAllText($source), 'public const string Release\s*=\s*"(?<version>\d+\.\d+\.\d+)"\s*;')
if ($versions.Count -ne 1) { throw 'Expected exactly one release version in shared/ProductVersion.cs.' }
$versions[0].Groups['version'].Value
