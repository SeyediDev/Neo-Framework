[CmdletBinding()]
param([string]$ProfileRoot = (Join-Path $PSScriptRoot '../hr-read-profile-v3'))
$ErrorActionPreference = 'Stop'
$profileDirectory = (Resolve-Path -LiteralPath $ProfileRoot).Path
$profileManifest = Join-Path $profileDirectory 'SOURCE-PACKAGE.json'
$expectedProfileHash = '9875088ffe6e3efc9553b0ca42873fa52b8a05d23d8101acd084c69e16b82329'
if ((Get-FileHash -LiteralPath $profileManifest -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedProfileHash) {
    throw 'The immutable HR3 dependency manifest has changed.'
}
function Test-ManifestFiles([string]$Directory, [object]$Manifest) {
    $absoluteDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Manifest.files) {
        $relative = [string]$file.path
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('\') -or $relative.Split('/').Contains('..') -or -not $seen.Add($relative)) {
            throw 'Unsafe or duplicate manifest path.'
        }
        $absolute = [IO.Path]::GetFullPath((Join-Path $Directory $relative))
        if (-not $absolute.StartsWith($absoluteDirectory, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escaped package.' }
        if ((Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$file.sha256) {
            throw "Package file hash mismatch: $relative"
        }
    }
}
$profile = Get-Content -LiteralPath $profileManifest -Raw | ConvertFrom-Json
if ($profile.files.Count -ne 80) { throw 'Unexpected immutable dependency file count.' }
Test-ManifestFiles $profileDirectory $profile
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SOURCE-PACKAGE.json') -Raw | ConvertFrom-Json
if ($manifest.dependencyManifestSha256 -cne $expectedProfileHash -or $manifest.operationalAcceptance -ne $false) {
    throw 'Unexpected dependency or acceptance metadata.'
}
Test-ManifestFiles $PSScriptRoot $manifest
Write-Output "PASS: $($manifest.files.Count) PDP files and 80 immutable HR3 dependency files verified. No live acceptance."
