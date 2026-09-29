# Offline behavioral tests; no network or real credential access.
$ErrorActionPreference='Stop'
$global:NeoBootstrapTest_remote=$null
$global:NeoBootstrapTest_logs=@()
$global:NeoBootstrapTest_creates=0
$global:NeoBootstrapTest_ghReads=0
$global:NeoBootstrapTest_item=[pscustomobject]@{id='11111111-1111-1111-1111-111111111111';key='TEST-1';isArchived=$false;ownerChatId=$null;version=[guid]::NewGuid().ToString();status='Backlog';description='SECRET_PRIVATE_DESCRIPTION'}
function Invoke-RestMethod($Uri,$Method='Get',$Headers,$Body,$MaximumRedirection,$ContentType){
    $url=[string]$Uri
    if($url.StartsWith('http://127.0.0.1')){
        if($url.Contains('/items?')){return [pscustomobject]@{items=@($global:NeoBootstrapTest_item);total=1}}
        if($url.EndsWith('/logs')){
            $p=[Text.Encoding]::UTF8.GetString($Body)|ConvertFrom-Json
            if($p.expectedVersion -ne $global:NeoBootstrapTest_item.version){throw 'Stale version'}
            $global:NeoBootstrapTest_logs+=,[pscustomobject]@{message=$p.message}
            $global:NeoBootstrapTest_item.version=[guid]::NewGuid().ToString()
        }
        return [pscustomobject]@{item=$global:NeoBootstrapTest_item;logs=$global:NeoBootstrapTest_logs}
    }
    if(!$url.StartsWith('https://api.github.com/')){throw 'Unexpected destination'}
    $global:NeoBootstrapTest_ghReads++
    if($url.EndsWith('/repos/test/repo')){return [pscustomobject]@{full_name='test/repo';has_issues=$true;permissions=[pscustomobject]@{push=$true}}}
    if($url.EndsWith('/user')){return [pscustomobject]@{login='test-owner'}}
    if($url.Contains('/issues?')){
        # Real Invoke-RestMethod emits a JSON array as one pipeline object.
        # Fill page one to exercise pagination before reaching the marker.
        if($url.EndsWith('page=1')){
            $dummy=@(1..100|ForEach-Object{[pscustomobject]@{number=$_;body='unrelated';user=[pscustomobject]@{login='test-owner'}}})
            return ,$dummy
        }
        if($global:NeoBootstrapTest_remote){return ,@($global:NeoBootstrapTest_remote)}
        return ,@()
    }
    if($url.EndsWith('/issues') -and $Method -eq 'Post'){
        $p=[Text.Encoding]::UTF8.GetString($Body)|ConvertFrom-Json
        if($p.body.Contains('SECRET_PRIVATE_DESCRIPTION')){throw 'Private context leaked'}
        $global:NeoBootstrapTest_creates++
        $global:NeoBootstrapTest_remote=[pscustomobject]@{number=101;title=$p.title;body=$p.body;user=[pscustomobject]@{login='test-owner'};html_url='https://github.com/test/repo/issues/101';state='open'}
        return $global:NeoBootstrapTest_remote
    }
    throw "Unexpected operation: $Method $url"
}
$previousToken=$env:GH_TOKEN
try{
    $env:GH_TOKEN='offline-test-not-a-real-credential'
    $tool=Join-Path $PSScriptRoot '../../tools/Publish-GitHubBacklog.ps1'
    $options=@{NeoWorkspaceApi='http://127.0.0.1/api/orchestration/v1/organizations/11111111-1111-1111-1111-111111111111/workspaces/22222222-2222-2222-2222-222222222222';ProjectId='33333333-3333-3333-3333-333333333333';ChatId='test-chat';Repository='test/repo';PublicManifest=(Join-Path $PSScriptRoot 'public-item.json');LocalDevelopment=$true}
    $preview=@(& $tool @options)
    if($preview.Count -ne 1 -or $global:NeoBootstrapTest_ghReads -ne 0 -or $global:NeoBootstrapTest_creates -ne 0){throw 'Preview had unexpected external effects'}
    $first=@(& $tool @options -Apply)
    $second=@(& $tool @options -Apply)
    if($first.Count -ne 1 -or !$first[0].Created -or $second.Count -ne 1 -or $second[0].Created){throw 'Replay did not reconcile the existing marker'}
    if($global:NeoBootstrapTest_creates -ne 1 -or $global:NeoBootstrapTest_logs.Count -ne 1){throw 'Replay created duplicate issue/log'}
    if($global:NeoBootstrapTest_remote.body -notlike 'Approved public summary.*'){throw 'Public manifest was not used'}
    $global:NeoBootstrapTest_item.ownerChatId='other-chat'
    $rejected=$false
    try{& $tool @options -Apply|Out-Null}catch{if($_.Exception.Message -like 'Another chat owns*'){$rejected=$true}else{throw}}
    if(!$rejected -or $global:NeoBootstrapTest_creates -ne 1){throw 'Ownership conflict was not blocked'}
    'PASS: dry-run isolation, paginated JSON-array handling, publication, replay, log deduplication, privacy and ownership guard.'
}finally{$env:GH_TOKEN=$previousToken}
