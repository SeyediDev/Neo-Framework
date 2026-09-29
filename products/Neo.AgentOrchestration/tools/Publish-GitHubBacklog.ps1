#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$NeoWorkspaceApi,
    [Parameter(Mandatory)][guid]$ProjectId,
    [Parameter(Mandatory)][string]$ChatId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][string]$PublicManifest,
    [switch]$LocalDevelopment,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
# This is an explicit, one-way bootstrap. Never export descriptions or logs.
if($NeoWorkspaceApi.Query -or $NeoWorkspaceApi.Fragment -or $NeoWorkspaceApi.UserInfo -or
   $NeoWorkspaceApi.AbsolutePath -notmatch '^/api/orchestration/v1/organizations/[0-9a-f-]{36}/workspaces/[0-9a-f-]{36}/?$') {
    throw 'Supply the exact scoped Neo workspace API URL.'
}
$neoHeaders=@{'X-Orchestration-Chat'=$ChatId}
if($LocalDevelopment){
    if(!$NeoWorkspaceApi.IsLoopback){throw 'Local development mode requires a loopback address.'}
    $neoHeaders['X-Orchestration-Local']='true'
}else{
    if($NeoWorkspaceApi.Scheme -ne 'https' -or !$env:NEO_ORCHESTRATION_TOKEN){throw 'Use HTTPS and NEO_ORCHESTRATION_TOKEN.'}
    $neoHeaders.Authorization="Bearer $env:NEO_ORCHESTRATION_TOKEN"
}
$base=$NeoWorkspaceApi.AbsoluteUri.TrimEnd('/')
$manifest=@(Get-Content -LiteralPath $PublicManifest -Raw | ConvertFrom-Json)
if(!$manifest.Count){throw 'An explicitly reviewed public manifest is required.'}
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $manifest){
    if(!$entry.key -or !$entry.title -or !$entry.summary -or !$seen.Add($entry.key)) {throw 'Manifest entries require unique keys, titles and public summaries.'}
    if($entry.title.Length -gt 200 -or $entry.summary.Length -gt 16000){throw 'Public title/summary is too long.'}
}
$items=@();$skip=0
do{
    $page=Invoke-RestMethod "$base/items?projectId=$ProjectId&includeArchived=true&skip=$skip&take=200" -Headers $neoHeaders
    $items+=@($page.items);$skip+=$page.items.Count
    if(!$page.items.Count -and $skip -lt $page.total){throw 'Neo pagination returned an incomplete result.'}
}while($skip -lt $page.total)
$plan=@(foreach($entry in $manifest){
    $item=@($items|Where-Object key -eq $entry.key)
    if($item.Count -ne 1){throw "Missing or ambiguous Neo key: $($entry.key)"}
    if($item[0].isArchived){throw "Restore before publishing: $($entry.key)"}
    if($item[0].ownerChatId -and $item[0].ownerChatId -ne $ChatId){throw "Another chat owns $($entry.key)."}
    [pscustomobject]@{Item=$item[0];Public=$entry}
})
if(!$Apply){
    $plan|ForEach-Object{[pscustomobject]@{Key=$_.Item.key;Repository=$Repository;PublicTitle=$_.Public.title;PublicSummary=$_.Public.summary;Action='Preview only'}}
    return
}
# Prefer an explicitly scoped token. Otherwise use the existing OS credential;
# never prompt, save credentials, echo token text or send it to the Neo host.
$githubToken=$env:GH_TOKEN
if(!$githubToken){
    $oldInteractive=$env:GCM_INTERACTIVE
    try{
        $env:GCM_INTERACTIVE='Never'
        $raw="protocol=https`nhost=github.com`n`n" | git credential-manager get
        if($LASTEXITCODE -ne 0){throw 'GitHub credential lookup failed.'}
        foreach($line in $raw){if($line.StartsWith('password=')){$githubToken=$line.Substring(9)}}
    }finally{$env:GCM_INTERACTIVE=$oldInteractive;$raw=$null}
}
if(!$githubToken){throw 'A GitHub credential with repository issue write permission is required.'}
$ghHeaders=@{Authorization="Bearer $githubToken";Accept='application/vnd.github+json';'User-Agent'='Neo-Backlog-Bootstrap'}
function GitHub($path,$method='Get',$body=$null){
    $request=@{Uri="https://api.github.com/$path";Method=$method;Headers=$ghHeaders;MaximumRedirection=0}
    if($null -ne $body){$request.ContentType='application/json; charset=utf-8';$request.Body=[Text.Encoding]::UTF8.GetBytes(($body|ConvertTo-Json -Depth 8))}
    try{
        $result=Invoke-RestMethod @request
        # Invoke-RestMethod emits a JSON array as one pipeline object. Unroll it
        # explicitly so pagination and marker matching see individual issues.
        foreach($value in $result){$value}
    }catch{throw "GitHub $method failed for $path. Reconcile remote state before retry; do not blindly repeat a write."}
}
try{
    $repo=GitHub "repos/$Repository"
    if($repo.full_name -ine $Repository -or !$repo.has_issues -or !$repo.permissions.push){throw 'Repository identity, enabled Issues and write access must be verified.'}
    $viewer=GitHub 'user'
    $issues=@();$pageNumber=1
    do{
        $batch=@(GitHub "repos/$Repository/issues?state=all&per_page=100&page=$pageNumber")
        $issues+=$batch;$pageNumber++
        if($pageNumber -gt 1000){throw 'Issue pagination exceeded the safety limit.'}
    }while($batch.Count -eq 100)
    foreach($record in $plan){
        $item=$record.Item
        $details=Invoke-RestMethod "$base/items/$($item.id)" -Headers $neoHeaders
        if($details.item.version -ne $item.version){throw "Neo item changed since preview: $($item.key). Re-run after review."}
        $marker="<!-- neo-work-item:$($item.id) -->"
        $matches=@($issues|Where-Object{$_.body -and $_.body.Contains($marker) -and !$_.PSObject.Properties['pull_request']})
        if($matches.Count -gt 1){throw "Duplicate remote mapping for $($item.key). Manual reconciliation required."}
        $created=$false
        if($matches.Count -eq 1){
            $issue=$matches[0]
            if($issue.user.login -ine $viewer.login){throw "Remote marker belongs to another publisher: $($item.key). Review before linking."}
        }else{
            $body="$($record.Public.summary)`n`nTracking key: $($item.key)`n`n$marker"
            $issue=GitHub "repos/$Repository/issues" 'Post' @{title=$record.Public.title;body=$body}
            $created=$true
            if($item.status -in @('Done','Cancelled')){
                $reason=if($item.status -eq 'Done'){'completed'}else{'not_planned'}
                $issue=GitHub "repos/$Repository/issues/$($issue.number)" 'Patch' @{state='closed';state_reason=$reason}
            }
            $issues+=,$issue
        }
        # Link back through normal optimistic concurrency. On uncertain writes,
        # rerunning reconciles the remote marker and the append-only local log.
        $message="GitHub public bootstrap: $($issue.html_url) | source=$($item.id) | Public manifest only; ongoing synchronization pending."
        if(!@($details.logs|Where-Object message -eq $message).Count){
            $payload=@{expectedVersion=$details.item.version;message=$message}|ConvertTo-Json
            $null=Invoke-RestMethod "$base/items/$($item.id)/logs" -Method Post -Headers $neoHeaders -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload))
        }
        [pscustomobject]@{Key=$item.key;Url=$issue.html_url;Created=$created;State=$issue.state}
    }
}finally{$githubToken=$null;$ghHeaders.Clear()}
