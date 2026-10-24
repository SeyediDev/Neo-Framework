param([string]$DecisionPath, [string]$AdmissionPath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../../../..')).Path
$projectPath = Join-Path $PSScriptRoot 'UsageAccounting.Sample.csproj'
$artifactsPath = Join-Path $PSScriptRoot '.artifacts'
$bindingPath = Join-Path $repoPath 'docs/fanasa-platform-control-center/integration/fixture-binding.v1.json'
if (-not $DecisionPath) { $DecisionPath = Join-Path $repoPath 'docs/fanasa-platform-control-center/workstreams/contract-policy/fixtures/integration-decision.v2.json' }
if (-not $AdmissionPath) { $AdmissionPath = Join-Path (Split-Path -Parent $repoPath) 'NeoCluster/fanasa-platform-workstreams/api-gateway/fixtures/integration-admission.v1.json' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot ('results/integration-' + [Guid]::NewGuid().ToString('N')) }
foreach ($requiredPath in @($bindingPath, $DecisionPath, $AdmissionPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Integration pending: required generated predecessor file is absent: $requiredPath" }
}
& dotnet run --project $projectPath --property:UseArtifactsOutput=true --property:ArtifactsPath=$artifactsPath --property:NuGetAudit=false -- --integration $bindingPath $DecisionPath $AdmissionPath $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "Integration fixture failed with exit code $LASTEXITCODE" }
