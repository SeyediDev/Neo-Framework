param([string]$ProjectRoot, [ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$companionRoot = Split-Path $PSScriptRoot
if (-not $ProjectRoot) { $ProjectRoot = [IO.Path]::GetFullPath((Join-Path $companionRoot '../..')) }
$env:NEO_PROJECT_ROOT = (Resolve-Path -LiteralPath $ProjectRoot).Path
$serverDll = Join-Path $companionRoot 'artifacts/mcp/Neo.Companion.Mcp.dll'
if (-not (Test-Path -LiteralPath $serverDll)) {
    $serverDll = Join-Path $companionRoot "src/Neo.Companion.Mcp/bin/$Configuration/net10.0/Neo.Companion.Mcp.dll"
}
if (-not (Test-Path -LiteralPath $serverDll)) { throw 'Build or publish Neo.Companion.Mcp first; see the Companion README.' }
& dotnet $serverDll
exit $LASTEXITCODE
