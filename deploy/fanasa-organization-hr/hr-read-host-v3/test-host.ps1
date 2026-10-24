param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '../../..'),
    [switch]$CachedRestore
)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$package = $PSScriptRoot.Replace('\', '/')
$repository = $repository.Replace('\', '/') + '/'
$properties = @(
    '-p:EnableHr3Host=true',
    '-p:UseSharedCompilation=false',
    "-p:Hr3RepositoryRoot=$repository",
    "-p:Hr3ProfileRoot=${repository}deploy/fanasa-organization-hr/hr-read-profile-v3",
    "-p:Hr3PdpRoot=${repository}deploy/fanasa-organization-hr/hr-read-pdp-v3",
    "-p:CustomAfterMicrosoftCommonTargets=$package/integration/Hr3Host.targets"
)
$project = Join-Path $PSScriptRoot 'tests/Fanasa.HrReadHost.Tests.csproj'
$restoreOptions = @()
if ($CachedRestore) { $restoreOptions = @('--ignore-failed-sources', '-p:NuGetAudit=false') }
& dotnet restore $project @properties @restoreOptions --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'HR3 actual-host restore failed.' }
& dotnet build $project --no-restore @properties --disable-build-servers -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'HR3 actual-host build failed.' }
& dotnet (Join-Path $PSScriptRoot 'tests/bin/Debug/net10.0/Fanasa.HrReadHost.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw 'HR3 actual-host assertions failed.' }
