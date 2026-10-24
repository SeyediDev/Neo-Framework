param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-z0-9-]+$')][string]$ReleaseName,
    [string]$Workspace = 'E:/SJVS/Projects/Neo'
)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path -LiteralPath $Workspace).Path
$taskOutput = Join-Path $taskRoot ('.build/' + $ReleaseName)
if (Test-Path -LiteralPath $taskOutput) { throw 'Choose a new release name; an existing artifact is never overwritten.' }
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskWeb = Join-Path $taskOutput 'web'
$taskWorkbench = Join-Path $taskOutput 'workbench'
& dotnet publish (Join-Path $taskRoot 'products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Fanasa.AccessManagement.Web.csproj') -c Release --no-restore --nologo -o $taskWeb --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Web publish failed' }
& dotnet publish (Join-Path $taskRoot 'docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/ui/server/Fanasa.ContractPolicy.Workbench.csproj') -c Release --no-restore --nologo -o $taskWorkbench --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Native workbench publish failed' }
if ((Get-FileHash -LiteralPath (Join-Path $taskWeb 'Fanasa.ContractPolicy.dll')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskWorkbench 'Fanasa.ContractPolicy.dll')).Hash) { throw 'The host and workbench must ship the exact same native policy engine' }
$taskUi = Join-Path $taskRoot 'docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/ui'
New-Item -ItemType Directory -Path (Join-Path $taskWeb 'Workbench'),(Join-Path $taskWeb 'wwwroot/workbench') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskUi 'index.html') -Destination (Join-Path $taskWeb 'Workbench/index.html')
foreach ($taskFile in @('app.js','styles.css','workbench.js','workbench.css')) {
    Copy-Item -LiteralPath (Join-Path $taskUi $taskFile) -Destination (Join-Path $taskWeb ('wwwroot/workbench/' + $taskFile))
}
Copy-Item -LiteralPath (Join-Path $taskUi 'assets') -Destination (Join-Path $taskWeb 'wwwroot/workbench/') -Recurse
if ((Get-ChildItem -LiteralPath (Join-Path $taskWorkbench 'Schemas') -File).Count -ne 10) { throw 'All ten native schemas must be present' }
if (!(Test-Path -LiteralPath (Join-Path $taskWorkbench 'runtimes/linux-x64/native/libe_sqlite3.so'))) { throw 'Linux x64 SQLite native runtime must be present for VPS deployment' }
$taskFiles = Get-ChildItem -LiteralPath $taskOutput -Recurse -File | ForEach-Object {
    [ordered]@{ path=$_.FullName.Substring($taskOutput.Length+1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$taskManifest = [ordered]@{
    release=$ReleaseName; createdUtc=[DateTimeOffset]::UtcNow.ToString('O'); framework='net10.0';
    webService='fanasa-access-management@development.service'; workbenchPort=4187;
    workbenchProtection='existing SSO cookie + PlatformAdmin + CSRF';
    workbenchDataMode='isolated server-owned synthetic data'; operationalPolicyActivationChanged=$false;
    workbenchStorage='SQLite'; workbenchDatabaseSchema=1; workbenchRuntimeIntegrity=$true;
    files=@($taskFiles)
}
$taskManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskOutput 'release-manifest.json') -Encoding utf8
$taskArchive = Join-Path $taskRoot ('.build/' + $ReleaseName + '.tar.gz')
& tar -czf $taskArchive -C $taskOutput .
if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed' }
Write-Output $taskArchive
Write-Output (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()
