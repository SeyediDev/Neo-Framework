param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src/Fanasa.UnifiedPortal.Web/Fanasa.UnifiedPortal.Web.csproj'
$out = [IO.Path]::GetFullPath($OutputDirectory)
$publish = Join-Path $out 'web'
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Path $publish -Force | Out-Null
dotnet publish $project -c Release --no-restore -o $publish
$manifest = [ordered]@{ product = 'fanasa-unified-portal'; version = '0.1.0'; containsCredentials = $false; commit = $env:GITHUB_SHA }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'MANIFEST.json') -Encoding utf8
Compress-Archive -Path (Join-Path $out '*') -DestinationPath (Join-Path $out '..\fanasa-unified-portal.zip') -Force
