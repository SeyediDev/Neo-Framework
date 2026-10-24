param([ValidateRange(3, 99)][int]$EvidenceVersion = 3)

$ErrorActionPreference = 'Stop'
$runtimeRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
$runtimeEvidence = Join-Path $PSScriptRoot 'fixtures'

function Get-RuntimeSources {
    $runtimePaths = @(& rg --files -g '*.cs' -g '*.csproj' -g '*.cshtml' -g '*.json' -g '*.js' -g '*.css' -g '*.cjs' -g '!**/obj/**' -g '!**/bin/**' -g '!**/App_Data/**' `
        'products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web' `
        'products/Fanasa.AccessManagement/tests/Organization.Tests' `
        'products/Fanasa.AccessManagement/tests/usage-accounting-ui.test.cjs' `
        'products/Fanasa.AccessManagement/tests/usage-accounting-browser-check.cjs' `
        'docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/src/Fanasa.ContractPolicy' `
        'docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/pcc' `
        'docs/fanasa-platform-control-center/workstreams/contract-policy/schemas')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime source discovery failed.' }
    # Secret/local configuration is never included, even if present outside Git.
    $runtimePaths = @($runtimePaths | Where-Object { $_ -notmatch 'appsettings\.(Local|Development)\.json$' }) + @(
        'Directory.Build.props', 'Directory.Packages.props',
        'products/Fanasa.AccessManagement/Directory.Build.props',
        'products/Fanasa.AccessManagement/Directory.Packages.props')
    @($runtimePaths | Sort-Object -Unique | ForEach-Object {
        $runtimePath = $_.Replace('\', '/')
        if (Test-Path -LiteralPath $runtimePath -PathType Leaf) {
            [ordered]@{ path = $runtimePath; sha256 = (Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
    })
}

Push-Location $runtimeRoot
try {
    $runtimeBefore = @(Get-RuntimeSources)
    & dotnet build 'products/Fanasa.AccessManagement/tests/Organization.Tests/Organization.Tests.csproj' `
        -c Release --no-restore --disable-build-servers -m:1 -nr:false `
        -p:NuGetAudit=false -p:UseSharedCompilation=false -p:BuildInParallel=false 2>&1 |
        Tee-Object -FilePath (Join-Path $runtimeEvidence "runtime-build.v$EvidenceVersion.txt")
    if ($LASTEXITCODE -ne 0) { throw 'Runtime build failed; no source baseline was updated.' }
    $runtimeBinaryDirectory = 'products/Fanasa.AccessManagement/tests/Organization.Tests/bin/Release/net10.0'
    & dotnet (Join-Path $runtimeBinaryDirectory 'Organization.Tests.dll') 2>&1 |
        Tee-Object -FilePath (Join-Path $runtimeEvidence "runtime-checks.v$EvidenceVersion.txt")
    if ($LASTEXITCODE -ne 0) { throw 'Runtime tests failed; no source baseline was updated.' }
    & node --test 'products/Fanasa.AccessManagement/tests/usage-accounting-ui.test.cjs' 2>&1 |
        Tee-Object -FilePath (Join-Path $runtimeEvidence "usage-accounting-ui-checks.v$EvidenceVersion.txt")
    if ($LASTEXITCODE -ne 0) { throw 'Usage accounting UI tests failed; no source baseline was updated.' }
    $runtimeAfter = @(Get-RuntimeSources)
    if (($runtimeBefore | ConvertTo-Json -Depth 5 -Compress) -cne ($runtimeAfter | ConvertTo-Json -Depth 5 -Compress)) {
        throw 'Runtime sources changed during verification; rerun on a coherent source state. No baseline was updated.'
    }
    $runtimeBinaries = @('Organization.Tests.dll', 'Fanasa.AccessManagement.Web.dll', 'Fanasa.ContractPolicy.dll') | ForEach-Object {
        $runtimeBinary = Join-Path $runtimeBinaryDirectory $_
        [ordered]@{ path = $runtimeBinary.Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $runtimeBinary -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    [ordered]@{
        schemaVersion = "$EvidenceVersion.0"; verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        scope = 'Local product runner, selected Web/ContractPolicy runtime inputs and Organization.Tests; not deployment or a complete dependency manifest.'
        sources = $runtimeAfter; binaries = @($runtimeBinaries)
    } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $runtimeEvidence "runtime-source-baseline.v$EvidenceVersion.json") -Encoding utf8
}
finally { Pop-Location }
