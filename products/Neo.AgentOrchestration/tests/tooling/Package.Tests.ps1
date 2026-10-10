# Offline packaging behavior; mock publish/Git, no model, DB, network or hosts.
# Each test owns only a newly generated temporary directory.
#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$tool = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../tools/Package-Local.ps1'))
$productRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $productRoot '../..'))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('fanasa-bundle-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$global:FanasaBundleTest = @{ publishCalls=0; failPublish=$false; dirtySource=$false;
    changeHead=$false; headReads=0; changeStatus=$false; statusReads=0 }
function git {
    if($args -contains 'rev-parse'){
        $global:FanasaBundleTest.headReads++
        $global:LASTEXITCODE=0
        if($global:FanasaBundleTest.changeHead -and $global:FanasaBundleTest.headReads -gt 1){ return ('b' * 40) }
        return ('a' * 40)
    }
    if($args -contains 'status'){
        $global:FanasaBundleTest.statusReads++
        $global:LASTEXITCODE=0
        if($global:FanasaBundleTest.dirtySource -or ($global:FanasaBundleTest.changeStatus -and $global:FanasaBundleTest.statusReads -gt 1)){
            return ' M products/test/owned-source.cs'
        }
        return
    }
    throw 'Unexpected Git command.'
}
function dotnet {
    if($args[0] -ne 'publish'){ throw 'Packaging tried an unexpected runtime operation.' }
    $global:FanasaBundleTest.publishCalls++
    if($global:FanasaBundleTest.failPublish){ $global:LASTEXITCODE=1; return }
    $destination = $args[[Array]::IndexOf($args,'-o') + 1]
    New-Item -ItemType Directory -Path $destination | Out-Null
    # Fake payload: verifies composition/archive behavior, NOT a real DLL build.
    'offline-publish-placeholder' | Set-Content -LiteralPath (Join-Path $destination 'mock.dll')
    $global:LASTEXITCODE=0
}
function Reject([string]$path,[string]$expected){
    $rejected=$false
    try{ & $tool -OutputDirectory $path | Out-Null }
    catch{ if($_.Exception.Message -like $expected){ $rejected=$true }else{ throw } }
    if(!$rejected){ throw "Expected refusal: $expected" }
}
try {
    $parseErrors=$null; $tokens=$null
    $null=[Management.Automation.Language.Parser]::ParseFile($tool,[ref]$tokens,[ref]$parseErrors)
    if($parseErrors.Count){ throw 'Packaging syntax failed.' }

    # Existing directory and ZIP survive; refusal happens before fake publish.
    $existing=Join-Path $testRoot 'existing'
    New-Item -ItemType Directory -Path $existing | Out-Null
    'retained-release' | Set-Content -LiteralPath (Join-Path $existing 'marker.txt')
    Reject $existing 'Output already exists*'
    if((Get-Content -LiteralPath (Join-Path $existing 'marker.txt')) -ne 'retained-release'){ throw 'Existing data changed.' }
    $zipTarget=Join-Path $testRoot 'archived'
    'retained-archive' | Set-Content -LiteralPath "$zipTarget.zip"
    Reject $zipTarget 'Archive already exists*'
    if((Get-Content -LiteralPath "$zipTarget.zip") -ne 'retained-archive' -or (Test-Path $zipTarget)){ throw 'Archive refusal changed files.' }
    Reject (Join-Path $repoRoot 'products/new-package-test') 'Output must be a new artifact directory*'
    $linkPath=Join-Path $testRoot 'artifact-link'
    $linkType=if($IsWindows){ 'Junction' }else{ 'SymbolicLink' }
    New-Item -ItemType $linkType -Path $linkPath -Target $existing | Out-Null
    Reject (Join-Path $linkPath 'nested-output') 'Artifact output may not traverse a symlink or junction*'
    if($global:FanasaBundleTest.publishCalls -ne 0){ throw 'Unsafe target reached publish.' }

    $output=Join-Path $testRoot 'complete'
    & $tool -OutputDirectory $output | Out-Null
    $manifest=Get-Content -Raw -LiteralPath (Join-Path $output 'MANIFEST.json') | ConvertFrom-Json
    if($global:FanasaBundleTest.publishCalls -ne 6 -or $manifest.components.Count -ne 6 -or $manifest.components -notcontains 'gateway'){
        throw 'All six components, including gateway, must be packaged.'
    }
    if($manifest.sourceDirty -or $manifest.sourceCommit -ne ('a'*40) -or $manifest.gatewayAutoStarted -or
        $manifest.nativeRuntimesIncluded -or $manifest.modelConnectionActivated -or $manifest.databaseMigrationExecuted -or $manifest.seedExecuted){
        throw 'Packaging activation or provenance claims are incorrect.'
    }
    foreach($relative in @('tools/react-lsp/server.mjs','tools/react-lsp/package-lock.json','tools/react-lsp/tests/test_freshness.py','gateway/mock.dll','docs/GATEWAY-JOURNAL.md','docs/MODEL-CONNECTIONS.md',
        'skill/references/tools.md','skill/references/project-binding.md','deploy/agents/opencode.proxy.template.json',
        'deploy/agents/hermes.proxy.template.yaml','Start-Local-Bundle.ps1')){
        if(!(Test-Path -LiteralPath (Join-Path $output $relative) -PathType Leaf)){ throw "Missing bundled companion: $relative" }
    }
    if(!$manifest.reactLspIncluded -or $manifest.reactLspDependenciesIncluded -or $manifest.reactLspNodeRuntimeIncluded -or $manifest.reactLspAutoStarted){ throw 'React LSP packaging flags incorrect.' }
    $template=Get-Content -Raw -LiteralPath (Join-Path $output 'deploy/agents/opencode.proxy.template.json') | ConvertFrom-Json
    if($template.disabled_providers -notcontains 'fanasa-proxy' -or $template.permission.'*' -ne 'ask'){
        throw 'Packaged provider must remain disabled/ask.'
    }
    $archive=[IO.Compression.ZipFile]::OpenRead("$output.zip")
    try{
        if(!($archive.Entries | Where-Object { ($_.FullName -replace '\\','/') -eq 'gateway/mock.dll' })){
            throw 'Gateway missing from archive.'
        }
    }finally{ $archive.Dispose() }
    Reject $output 'Output already exists*'
    if($global:FanasaBundleTest.publishCalls -ne 6){ throw 'Replay started publishing.' }

    $global:FanasaBundleTest.dirtySource=$true
    $dirtyOutput=Join-Path $testRoot 'dirty'
    & $tool -OutputDirectory ($dirtyOutput + [IO.Path]::DirectorySeparatorChar) | Out-Null
    $dirty=Get-Content -Raw -LiteralPath (Join-Path $dirtyOutput 'MANIFEST.json') | ConvertFrom-Json
    if(!$dirty.sourceDirty -or $dirty.trackedSourceChanges.Count -ne 1){ throw 'Dirty source was misrepresented as clean.' }
    $global:FanasaBundleTest.dirtySource=$false

    $global:FanasaBundleTest.changeHead=$true; $global:FanasaBundleTest.headReads=0
    $headOutput=Join-Path $testRoot 'changed-head'
    Reject $headOutput 'Source commit changed during packaging*'
    if(Test-Path -LiteralPath "$headOutput.zip"){ throw 'Changed HEAD produced an archive.' }
    $global:FanasaBundleTest.changeHead=$false; $global:FanasaBundleTest.changeStatus=$true; $global:FanasaBundleTest.statusReads=0
    $statusOutput=Join-Path $testRoot 'changed-status'
    Reject $statusOutput 'Tracked working-tree state changed during packaging*'
    if(Test-Path -LiteralPath "$statusOutput.zip"){ throw 'Changed status produced an archive.' }
    $global:FanasaBundleTest.changeStatus=$false; $global:FanasaBundleTest.failPublish=$true
    $failedOutput=Join-Path $testRoot 'failed'
    Reject $failedOutput 'Publish failed*'
    if(!(Test-Path -LiteralPath $failedOutput) -or (Test-Path -LiteralPath "$failedOutput.zip")){
        throw 'Failed publish must retain its directory without making an archive.'
    }
    'PASS: syntax; existing directory/ZIP retention; source-target/junction refusal; six-component archive; disabled providers; skill/docs; replay; dirty provenance; HEAD/status change refusal; failed-publish retention.'
} finally {
    # Only this randomly named test directory is disposable, never source paths.
    $resolved=[IO.Path]::GetFullPath($testRoot)
    $temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if($resolved.StartsWith($temporaryRoot,[StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved) -match '^fanasa-bundle-test-[0-9a-f]{32}$'){
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }else{ throw 'Unsafe disposable test cleanup target.' }
}
