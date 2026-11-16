param([Parameter(Mandatory=$true)][string]$Stage)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = [IO.Path]::GetFullPath((Join-Path $root '.work')).TrimEnd('\')
$resolved = (Resolve-Path -LiteralPath $Stage).ProviderPath.TrimEnd('\')
# Only one immediate, uniquely named packaging directory is eligible, never arbitrary output paths.
if ((Split-Path -Parent $resolved) -ne $work -or
    (Split-Path -Leaf $resolved) -notmatch '^installer-[a-f0-9]{32}$') {
    throw 'Refusing cleanup outside an installer staging directory.'
}
foreach ($path in @($work, $resolved)) {
    if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Refusing cleanup through a linked staging directory.'
    }
}
if (@(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object {
    $_.Attributes -band [IO.FileAttributes]::ReparsePoint
}).Count) { throw 'Refusing cleanup of staging content with links.' }
Remove-Item -LiteralPath $resolved -Recurse -Force
