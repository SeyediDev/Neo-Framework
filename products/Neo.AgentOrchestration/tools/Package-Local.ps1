#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Get-Location) '.artifacts/fanasa-agentic-work-local'),
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$IncludeReactLspDependencies
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$repo = (Resolve-Path -LiteralPath (Join-Path $root '../..')).Path
$output = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
$zip = "$output.zip"
# Publishing never erases an earlier release or the source tree. Even a partial
# failed publish is retained; the next attempt must choose a fresh directory.
if(Test-Path -LiteralPath $output){ throw 'Output already exists; choose a fresh release directory.' }
if(Test-Path -LiteralPath $zip){ throw 'Archive already exists; choose a fresh release directory.' }
$comparison = if($IsWindows){ [StringComparison]::OrdinalIgnoreCase }else{ [StringComparison]::Ordinal }
$separator = [IO.Path]::DirectorySeparatorChar
$repoPrefix = $repo.TrimEnd($separator) + $separator
$artifactPrefix = (Join-Path $repo '.artifacts').TrimEnd($separator) + $separator
if($output -eq [IO.Path]::GetPathRoot($output) -or
    $output.Equals($repo,$comparison) -or $repo.StartsWith($output.TrimEnd($separator)+$separator,$comparison) -or
    ($output.StartsWith($repoPrefix,$comparison) -and !$output.StartsWith($artifactPrefix,$comparison))){
    throw 'Output must be a new artifact directory, not a source directory or its ancestor.'
}
$ancestor = [IO.DirectoryInfo]::new($output).Parent
while($null -ne $ancestor){
    if($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)){
        throw 'Artifact output may not traverse a symlink or junction.'
    }
    $ancestor = $ancestor.Parent
}
$commit = (git -C $repo rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40,64}$'){ throw 'Cannot establish source commit.' }
$sourceChanges = @(git -C $repo status --porcelain --untracked-files=no)
if($LASTEXITCODE -ne 0){ throw 'Cannot establish tracked working-tree state.' }
New-Item -ItemType Directory -Path $output | Out-Null
$projects = [ordered]@{
    api = Join-Path $root 'src/Neo.AgentOrchestration.Api/Neo.AgentOrchestration.Api.csproj'
    web = Join-Path $root 'src/Neo.AgentOrchestration.Web/Neo.AgentOrchestration.Web.csproj'
    worker = Join-Path $root 'src/Neo.AgentOrchestration.Worker/Neo.AgentOrchestration.Worker.csproj'
    mcp = Join-Path $root 'src/Neo.AgentOrchestration.Mcp/Neo.AgentOrchestration.Mcp.csproj'
    cli = Join-Path $root 'src/Neo.AgentOrchestration.Provisioning/Neo.AgentOrchestration.Provisioning.csproj'
    gateway = Join-Path $root 'src/Fanasa.AgentGateway/Fanasa.AgentGateway.csproj'
}
foreach($name in $projects.Keys){
    dotnet publish $projects[$name] --no-restore --disable-build-servers -c $Configuration -f net10.0 --self-contained false -o (Join-Path $output $name)
    if($LASTEXITCODE -ne 0){ throw "Publish failed: $name" }
}
$sourceDocs = Join-Path $root 'docs'
New-Item -ItemType Directory -Force -Path (Join-Path $output 'docs'),(Join-Path $output 'skill') | Out-Null
# Preserve relative documentation/skill links, not just the main skill file.
Get-ChildItem -LiteralPath $sourceDocs -Filter '*.md' -File | Copy-Item -Destination (Join-Path $output 'docs')
Copy-Item -LiteralPath (Join-Path $sourceDocs 'LOCAL-BUNDLE.md') -Destination (Join-Path $output 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $root '.agents/skills/neo-agent-orchestration/SKILL.md') -Destination (Join-Path $output 'skill') -Force
Copy-Item -LiteralPath (Join-Path $root '.agents/skills/neo-agent-orchestration/references') -Destination (Join-Path $output 'skill') -Recurse
New-Item -ItemType Directory -Path (Join-Path $output 'deploy/agents') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'deploy/agents/opencode.proxy.template.json'),(Join-Path $root 'deploy/agents/hermes.proxy.template.yaml') -Destination (Join-Path $output 'deploy/agents')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-Local-Bundle.ps1') -Destination $output -Force
$reactSource = Join-Path $PSScriptRoot 'react-lsp'
$reactOutput = Join-Path $output 'tools/react-lsp'
New-Item -ItemType Directory -Path (Join-Path $reactOutput 'tests') -Force | Out-Null
foreach($relative in @('server.mjs','package.json','package-lock.json','README.fa.md','tests/test_freshness.py')){
    Copy-Item -LiteralPath (Join-Path $reactSource $relative) -Destination (Join-Path $reactOutput $relative)
}
if($IncludeReactLspDependencies){
    $dependencies = Join-Path $reactSource 'node_modules'
    if(!(Test-Path -LiteralPath (Join-Path $dependencies 'typescript/lib/typescript.js'))){
        throw 'React LSP dependencies missing; explicitly run npm ci before packaging.'
    }
    Copy-Item -LiteralPath $dependencies -Destination $reactOutput -Recurse
}
$finalCommit = (git -C $repo rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or $finalCommit -ne $commit){ throw 'Source commit changed during packaging; no archive was created.' }
$finalChanges = @(git -C $repo status --porcelain --untracked-files=no)
if($LASTEXITCODE -ne 0 -or ($finalChanges -join "`n") -cne ($sourceChanges -join "`n")){
    throw 'Tracked working-tree state changed during packaging; no archive was created.'
}
@{
    product = 'Neo.AgentOrchestration'; bundleType = 'local-framework-dependent'; sourceCommit = $commit
    targetFramework = 'net10.0'; selfContained = $false; databaseMigrationExecuted = $false
    sourceDirty = ($sourceChanges.Count -gt 0); trackedSourceChanges = $sourceChanges
    seedExecuted = $false; containsCredentials = $false; components = @($projects.Keys)
    gatewayAutoStarted = $false; nativeRuntimesIncluded = $false; modelConnectionActivated = $false
    launcher = 'Start-Local-Bundle.ps1'
    reactLspIncluded = $true; reactLspDependenciesIncluded = [bool]$IncludeReactLspDependencies
    reactLspNodeRuntimeIncluded = $false; reactLspAutoStarted = $false
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'MANIFEST.json') -Encoding utf8NoBOM
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -CompressionLevel Optimal
Get-FileHash -Algorithm SHA256 -LiteralPath $zip
Write-Output "Bundle: $output"
Write-Output "Archive: $zip"
