#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Get-Location) '.artifacts/neo-agent-orchestration-local'),
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$repo = (Resolve-Path -LiteralPath (Join-Path $root '../..')).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){ Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$projects = @{
    api = Join-Path $root 'src/Neo.AgentOrchestration.Api/Neo.AgentOrchestration.Api.csproj'
    web = Join-Path $root 'src/Neo.AgentOrchestration.Web/Neo.AgentOrchestration.Web.csproj'
    worker = Join-Path $root 'src/Neo.AgentOrchestration.Worker/Neo.AgentOrchestration.Worker.csproj'
    mcp = Join-Path $root 'src/Neo.AgentOrchestration.Mcp/Neo.AgentOrchestration.Mcp.csproj'
    cli = Join-Path $root 'src/Neo.AgentOrchestration.Provisioning/Neo.AgentOrchestration.Provisioning.csproj'
}
foreach($name in $projects.Keys){
    dotnet publish $projects[$name] --no-restore --disable-build-servers -c $Configuration -f net10.0 --self-contained false -o (Join-Path $output $name)
    if($LASTEXITCODE -ne 0){ throw "Publish failed: $name" }
}
$sourceDocs = Join-Path $root 'docs'
New-Item -ItemType Directory -Force -Path (Join-Path $output 'docs'),(Join-Path $output 'skill') | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceDocs 'DATABASE.md'),(Join-Path $sourceDocs 'CLI.md'),(Join-Path $sourceDocs 'MCP.md'),(Join-Path $sourceDocs 'HARNESS.md'),(Join-Path $sourceDocs 'LOCAL-LAUNCHER.md') -Destination (Join-Path $output 'docs') -Force
Copy-Item -LiteralPath (Join-Path $sourceDocs 'LOCAL-BUNDLE.md') -Destination (Join-Path $output 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $root '.agents/skills/neo-agent-orchestration/SKILL.md') -Destination (Join-Path $output 'skill') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-Local-Bundle.ps1') -Destination $output -Force
$commit = (git -C $repo rev-parse HEAD).Trim()
@{
    product = 'Neo.AgentOrchestration'; bundleType = 'local-framework-dependent'; sourceCommit = $commit
    targetFramework = 'net10.0'; selfContained = $false; databaseMigrationExecuted = $false
    seedExecuted = $false; containsCredentials = $false; components = @('api','web','worker','mcp','cli')
    launcher = 'Start-Local-Bundle.ps1'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'MANIFEST.json') -Encoding utf8NoBOM
$zip = "$output.zip"
if(Test-Path -LiteralPath $zip){ Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -CompressionLevel Optimal
Get-FileHash -Algorithm SHA256 -LiteralPath $zip
Write-Output "Bundle: $output"
Write-Output "Archive: $zip"
