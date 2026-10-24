param(
    [string]$RepositoryRoot,
    [string]$ArtifactsPath,
    [switch]$CachedRestore
)
$ErrorActionPreference = 'Stop'
if (-not $RepositoryRoot) {
    $repositoryCandidate = Get-Item -LiteralPath $PSScriptRoot
    while ($repositoryCandidate -and -not (Test-Path -LiteralPath (Join-Path $repositoryCandidate.FullName 'products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Fanasa.AccessManagement.Web.csproj'))) {
        $repositoryCandidate = $repositoryCandidate.Parent
    }
    if (-not $repositoryCandidate) { throw 'Pass -RepositoryRoot for the actual host repository.' }
    $RepositoryRoot = $repositoryCandidate.FullName
}
$repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$package = $PSScriptRoot.Replace('\', '/')
$artifactBase = [IO.Path]::GetFullPath((Join-Path $repository '.build/hr3-host-r7-artifacts'))
if (-not $ArtifactsPath) {
    $ArtifactsPath = Join-Path $artifactBase ('run-' + [Guid]::NewGuid().ToString('N'))
} elseif (-not [IO.Path]::IsPathRooted($ArtifactsPath)) {
    $ArtifactsPath = Join-Path $repository $ArtifactsPath
}
$artifacts = [IO.Path]::GetFullPath($ArtifactsPath)
if (-not $artifacts.StartsWith($artifactBase + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ArtifactsPath must be a fresh directory beneath repository/.build/hr3-host-r7-artifacts.'
}
if (Test-Path -LiteralPath $artifacts) { throw 'ArtifactsPath already exists; stale artifacts are not accepted.' }
New-Item -ItemType Directory -Path $artifactBase -Force | Out-Null
New-Item -ItemType Directory -Path $artifacts | Out-Null
$artifacts = $artifacts.Replace('\', '/')
$repository = $repository.Replace('\', '/').TrimEnd('/') + '/'
Write-Output "HR3_ARTIFACTS_PATH=$artifacts"
$properties = @(
    '-p:EnableHr3Host=true',
    '-p:UseSharedCompilation=false',
    '-p:UseRazorBuildServer=false',
    '-p:BuildInParallel=false',
    '-p:RestoreDisableParallel=true',
    '-m:1',
    '-nodeReuse:false',
    "-p:Hr3RepositoryRoot=$repository",
    "-p:Hr3ProfileRoot=${repository}deploy/fanasa-organization-hr/hr-read-profile-v3",
    "-p:Hr3PdpRoot=${repository}deploy/fanasa-organization-hr/hr-read-pdp-v3",
    "-p:CustomAfterMicrosoftCommonTargets=$package/integration/Hr3Host.targets"
)
$project = Join-Path $PSScriptRoot 'tests/Fanasa.HrReadHost.Tests.csproj'
$restoreOptions = @()
if ($CachedRestore) { $restoreOptions = @('--ignore-failed-sources', '-p:NuGetAudit=false') }
$previousBuildServer = $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER
try {
    $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
    & dotnet restore $project @properties @restoreOptions --artifacts-path $artifacts --disable-parallel --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'HR3 actual-host restore failed.' }
    & dotnet build $project --no-restore @properties --artifacts-path $artifacts --disable-build-servers -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'HR3 actual-host build failed.' }
    $binRoot = Join-Path $artifacts 'bin/Fanasa.HrReadHost.Tests'
    $testDlls = @(Get-ChildItem -LiteralPath $binRoot -Directory | ForEach-Object {
        $dll = Join-Path $_.FullName 'Fanasa.HrReadHost.Tests.dll'
        if (Test-Path -LiteralPath $dll -PathType Leaf) { Get-Item -LiteralPath $dll }
    })
    if ($testDlls.Count -ne 1) { throw "Expected exactly one fresh HR3 test DLL under $binRoot/<pivot>; found $($testDlls.Count)." }
    Write-Output "HR3_TEST_DLL=$($testDlls[0].FullName.Replace('\', '/'))"
    & dotnet $testDlls[0].FullName
    if ($LASTEXITCODE -ne 0) { throw 'HR3 actual-host assertions failed.' }
} finally {
    $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = $previousBuildServer
}
