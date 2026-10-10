param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src/Fanasa.UnifiedPortal.Web/Fanasa.UnifiedPortal.Web.csproj'
$out = [IO.Path]::GetFullPath($OutputDirectory)
$publish = Join-Path $out 'web'
$archive = [IO.Path]::GetFullPath((Join-Path $out '..\fanasa-unified-portal.zip'))
if (Test-Path -LiteralPath $out) { throw 'OutputDirectory already exists. Choose a fresh artifact directory; existing files will not be deleted.' }
if (Test-Path -LiteralPath $archive) { throw 'Archive already exists. Choose a fresh artifact parent; existing packages will not be overwritten.' }
New-Item -ItemType Directory -Path $publish | Out-Null
dotnet publish $project -c Release --no-restore -p:UseSharedCompilation=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE. Incomplete output is retained for diagnosis; no package was created." }
if (-not (Test-Path -LiteralPath (Join-Path $publish 'Fanasa.UnifiedPortal.Web.dll') -PathType Leaf)) { throw 'Published portal DLL is missing; no package was created.' }
$manifest = [ordered]@{ product = 'fanasa-unified-portal'; version = '0.1.0'; containsCredentials = $false; commit = $env:GITHUB_SHA }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'MANIFEST.json') -Encoding utf8
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $archive
