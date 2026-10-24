param([string]$FixturePath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'UsageAccounting.Sample.csproj'
$artifactsPath = Join-Path $PSScriptRoot '.artifacts'
if (-not $FixturePath) { $FixturePath = Join-Path $PSScriptRoot '../fixtures/usage-event-v1.json' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot ('results/run-' + [Guid]::NewGuid().ToString('N')) }
# Global build properties also redirect every referenced project's restore/build output.
& dotnet run --project $projectPath --property:UseArtifactsOutput=true --property:ArtifactsPath=$artifactsPath --property:NuGetAudit=false -- $FixturePath $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "Fixture failed with exit code $LASTEXITCODE" }
