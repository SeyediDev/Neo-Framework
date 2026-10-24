param([Parameter(Mandatory = $true)][string]$OutputPath)

$ErrorActionPreference = 'Stop'
$fixtureSource = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Gateway.Tests/Program.cs'))
$expectedHash = '0979c82022a5f7e91d743576e23d0dcb84831eee0756984cee5c4ab7b77e3377'
$actualHash = (Get-FileHash -LiteralPath $fixtureSource -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) { throw 'Owner Gateway fixture changed; review the isolated extraction pin before building.' }

$sourceText = [System.IO.File]::ReadAllText($fixtureSource)
$preambleMarker = '// Local integration only:'
$tailMarker = 'sealed record SignedRequest('
$preambleEnd = $sourceText.IndexOf($preambleMarker, [System.StringComparison]::Ordinal)
$tailStart = $sourceText.IndexOf($tailMarker, [System.StringComparison]::Ordinal)
if ($preambleEnd -lt 0 -or $tailStart -le $preambleEnd -or
    $sourceText.IndexOf($tailMarker, $tailStart + 1, [System.StringComparison]::Ordinal) -ge 0) {
    throw 'Pinned fixture extraction markers do not identify one preamble and one fixture tail.'
}
$generated = '// Generated from the SHA-256-pinned owner fixture; owner source is never modified.' + [Environment]::NewLine +
    '// The shared pilot calls SendWitness with packaged Lua output and never executes Witness, Operate or Sign.' + [Environment]::NewLine +
    $sourceText.Substring(0, $preambleEnd) + $sourceText.Substring($tailStart)

$outputFull = [System.IO.Path]::GetFullPath($OutputPath)
$allowedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'obj')) + [System.IO.Path]::DirectorySeparatorChar
if (-not $outputFull.StartsWith($allowedOutputRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Generated fixture output must remain inside this isolated project obj directory.'
}
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($outputFull)) | Out-Null
[System.IO.File]::WriteAllText($outputFull, $generated, [System.Text.UTF8Encoding]::new($false))
