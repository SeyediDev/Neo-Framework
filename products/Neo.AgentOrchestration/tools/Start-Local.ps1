#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BinaryDirectory,
    [Parameter(Mandatory)][Guid]$OrganizationId,
    [Parameter(Mandatory)][Guid]$WorkspaceId,
    [string]$Database='NeoAgentOrchestration',
    [ValidateRange(1024,65535)][int]$ApiPort=5180,
    [ValidateRange(1024,65535)][int]$WebPort=5181,
    [string]$LogDirectory=(Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Neo/AgentOrchestration/logs'),
    [ValidateRange(5,180)][int]$ReadinessTimeoutSeconds=60,
    [switch]$Restart
)
$ErrorActionPreference='Stop'
if(!$IsWindows){throw 'This local launcher requires Windows PowerShell 7; use documented host commands on other platforms.'}
if($OrganizationId -eq [Guid]::Empty -or $WorkspaceId -eq [Guid]::Empty -or $ApiPort -eq $WebPort){throw 'Distinct ports and nonempty scope identifiers required.'}
if(!$env:NEO_ORCHESTRATION_SQL){throw 'Set NEO_ORCHESTRATION_SQL privately for the explicitly provisioned destination.'}
$product=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$binary=(Resolve-Path -LiteralPath $BinaryDirectory).Path
$apiDll=Join-Path $binary 'Neo.AgentOrchestration.Api.dll'
$webDll=Join-Path $binary 'Neo.AgentOrchestration.Web.dll'
$cliDll=Join-Path $binary 'neo-agent.dll'
foreach($file in @($apiDll,$webDll,$cliDll)){if(!(Test-Path -LiteralPath $file -PathType Leaf)){throw 'Build API, Web and provisioning into the specified binary directory first.'}}
$dotnet=(Get-Command dotnet -CommandType Application).Source
$mutex=[Threading.Mutex]::new($false,"Local\Neo.AgentOrchestration.Launch.$ApiPort.$WebPort")
$locked=$false
$changed=@{}
try{
    try{$locked=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$locked=$true}
    if(!$locked){throw 'Another launcher is handling these ports; wait and inspect its result.'}
    # Read-only readiness check: never migrate/seed or print a connection string.
    $healthText=& $dotnet $cliDll health $Database 2>$null
    if($LASTEXITCODE -ne 0){throw 'Database is not ready; run explicit provisioning/diagnosis before starting hosts.'}
    $health=($healthText -join "`n")|ConvertFrom-Json
    if(!$health.ready){throw 'Database schema is not ready.'}
    function Existing([int]$port,[string]$dll){
        $listeners=@(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -eq $port)
        if(!$listeners.Count){return $null}
        if(@($listeners|Where-Object {$_.LocalAddress -notin @('127.0.0.1','::1')}).Count){throw "Port $port is not loopback-only; refusing to reuse or stop it."}
        $ids=@($listeners.OwningProcess|Select-Object -Unique)
        if($ids.Count -ne 1){throw "Multiple processes own port $port."}
        $process=Get-CimInstance Win32_Process -Filter "ProcessId=$($ids[0])" -ErrorAction Stop
        # Exact bounded DLL argument, not a substring or a generic dotnet name.
        $pattern='(?i)(?:^|\s)(?:"'+[regex]::Escape($dll)+'"|'+[regex]::Escape($dll)+')(?=\s|$)'
        if($process.Name -ne 'dotnet.exe' -or $process.CommandLine -notmatch $pattern){throw "Port $port belongs to a different process/build. No process was stopped."}
        return $process
    }
    # Inspect both before any restart; an unrelated second listener must not
    # cause the first valid service to be stopped.
    $api=Existing $ApiPort $apiDll
    $web=Existing $WebPort $webDll
    if($Restart){
        foreach($process in @($api,$web)){
            if(!$process){continue}
            $current=Get-CimInstance Win32_Process -Filter "ProcessId=$($process.ProcessId)" -ErrorAction Stop
            if($current.CreationDate -ne $process.CreationDate -or $current.CommandLine -ne $process.CommandLine){throw 'Process identity changed; restart aborted.'}
            Stop-Process -Id $process.ProcessId -ErrorAction Stop
            Wait-Process -Id $process.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
        }
        $api=$null;$web=$null
    }
    $settings=@{
        ASPNETCORE_ENVIRONMENT='Development';DOTNET_ENVIRONMENT='Development';NEO_LOCAL_DEVELOPMENT='true';
        Orchestration__DatabaseName=$Database;OrchestrationApi__BaseUrl="http://127.0.0.1:$ApiPort/";
        OrchestrationApi__DefaultOrganizationId=$OrganizationId.ToString('D');OrchestrationApi__DefaultWorkspaceId=$WorkspaceId.ToString('D')
    }
    foreach($key in $settings.Keys){$changed[$key]=[Environment]::GetEnvironmentVariable($key,'Process');[Environment]::SetEnvironmentVariable($key,$settings[$key],'Process')}
    $logs=[IO.Path]::GetFullPath($LogDirectory)
    $null=New-Item -ItemType Directory -Force -Path $logs
    $stamp=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
    function StartHost([string]$name,[string]$dll,[int]$port){
        Start-Process -FilePath $dotnet -WindowStyle Hidden -ArgumentList @(('"'+$dll+'"'),'--urls',"http://127.0.0.1:$port") -WorkingDirectory (Join-Path $product "src/Neo.AgentOrchestration.$name") -RedirectStandardOutput (Join-Path $logs "$stamp-$name.out.log") -RedirectStandardError (Join-Path $logs "$stamp-$name.err.log") -PassThru
    }
    $apiReused=!!$api;$webReused=!!$web
    if(!$api){$p=StartHost 'Api' $apiDll $ApiPort;$api=[pscustomobject]@{ProcessId=$p.Id}}
    if(!$web){$p=StartHost 'Web' $webDll $WebPort;$web=[pscustomobject]@{ProcessId=$p.Id}}
    $scope="api/orchestration/v1/organizations/$OrganizationId/workspaces/$WorkspaceId"
    $boardUrl="http://127.0.0.1:$WebPort/work/$OrganizationId/$WorkspaceId"
    $deadline=[DateTime]::UtcNow.AddSeconds($ReadinessTimeoutSeconds)
    $ready=$false
    do{
        if(!(Get-Process -Id $api.ProcessId -ErrorAction SilentlyContinue) -or !(Get-Process -Id $web.ProcessId -ErrorAction SilentlyContinue)){throw "A host exited. Inspect logs in $logs; no unrelated process was stopped."}
        try{
            $board=Invoke-RestMethod "http://127.0.0.1:$ApiPort/$scope/items?take=1" -Headers @{'X-Orchestration-Local'='true'} -TimeoutSec 5 -MaximumRedirection 0
            $page=Invoke-WebRequest $boardUrl -TimeoutSec 5 -MaximumRedirection 0
            if($null -ne $board.items -and $page.StatusCode -eq 200 -and $page.Content.Contains('name="ProjectId"')){$ready=$true;break}
        }catch{ }
        Start-Sleep -Milliseconds 500
    }while([DateTime]::UtcNow -lt $deadline)
    if(!$ready){throw "Readiness failed (API scope/schema/auth or Web board). Logs: $logs. Existing hosts are retained for diagnosis; use -Restart only for this exact build."}
    [pscustomobject]@{Ready=$true;ApiPid=$api.ProcessId;WebPid=$web.ProcessId;ApiReused=$apiReused;WebReused=$webReused;Board=$boardUrl;Logs=$logs}
}finally{
    foreach($key in $changed.Keys){[Environment]::SetEnvironmentVariable($key,$changed[$key],'Process')}
    if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()
}
