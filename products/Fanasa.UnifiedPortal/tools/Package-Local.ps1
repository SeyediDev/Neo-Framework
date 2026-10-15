param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src/Fanasa.UnifiedPortal.Web/Fanasa.UnifiedPortal.Web.csproj'
$out = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([char[]]@('\', '/'))
$publish = Join-Path $out 'web'
$archive = Join-Path ([IO.Path]::GetDirectoryName($out)) 'fanasa-unified-portal.zip'
if (Test-Path -LiteralPath $out) { throw 'OutputDirectory already exists. Choose a fresh artifact directory; existing files will not be deleted.' }
if (Test-Path -LiteralPath $archive) { throw 'Archive already exists. Choose a fresh artifact parent; existing packages will not be overwritten.' }
New-Item -ItemType Directory -Path $publish | Out-Null
dotnet publish $project -c Release --no-restore -p:UseSharedCompilation=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE. Incomplete output is retained for diagnosis; no package was created." }
if (-not (Test-Path -LiteralPath (Join-Path $publish 'Fanasa.UnifiedPortal.Web.dll') -PathType Leaf)) { throw 'Published portal DLL is missing; no package was created.' }
$manifest = [ordered]@{ product = 'fanasa-unified-portal'; version = '0.1.0'; containsCredentials = $false; commit = $env:GITHUB_SHA }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'MANIFEST.json') -Encoding utf8
# ZIP paths always use '/', including when packaging on Windows for Linux.
# Do not rely on Compress-Archive's host-specific entry separators or hidden-file handling.
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$entries = @(Get-ChildItem -LiteralPath $out -Recurse -Force)
if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
    throw 'Package symlinks/reparse points are not supported. No ZIP was created.'
}
$outputPrefix = $out.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
$partialArchive = $archive + '.partial-' + [Guid]::NewGuid().ToString('N')
$zip = [IO.Compression.ZipFile]::Open($partialArchive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in @($entries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)) {
        $relative = $entry.FullName.Substring($outputPrefix.Length).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entry.FullName, $relative,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
# Atomic final name; never overwrite a package that appeared during publication.
[IO.File]::Move($partialArchive, $archive)
