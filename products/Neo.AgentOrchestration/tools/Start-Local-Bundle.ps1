#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][Guid]$OrganizationId,
    [Parameter(Mandatory)][Guid]$WorkspaceId,
    [string]$Database = 'NeoAgentOrchestration',
    [ValidateRange(1024,65535)][int]$ApiPort = 5180,
    [ValidateRange(1024,65535)][int]$WebPort = 5181
)
$ErrorActionPreference = 'Stop'
if(!$env:NEO_ORCHESTRATION_SQL){ throw 'Set NEO_ORCHESTRATION_SQL privately before starting the bundle.' }
if($OrganizationId -eq [Guid]::Empty -or $WorkspaceId -eq [Guid]::Empty -or $ApiPort -eq $WebPort){ throw 'Invalid scope or ports.' }
$root = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$dotnet = (Get-Command dotnet -CommandType Application).Source
$cli = Join-Path $root 'cli/neo-agent.dll'; $api = Join-Path $root 'api/Neo.AgentOrchestration.Api.dll'; $web = Join-Path $root 'web/Neo.AgentOrchestration.Web.dll'
foreach($file in @($cli,$api,$web)){ if(!(Test-Path -LiteralPath $file -PathType Leaf)){ throw "Bundle file is missing: $file" } }
$null = & $dotnet $cli health $Database 2>$null
if($LASTEXITCODE -ne 0){ throw 'Database health failed; run explicit provisioning/health first.' }
$settings = @{ ASPNETCORE_ENVIRONMENT='Development'; DOTNET_ENVIRONMENT='Development'; NEO_LOCAL_DEVELOPMENT='true'; Orchestration__DatabaseName=$Database; OrchestrationApi__BaseUrl="http://127.0.0.1:$ApiPort/"; OrchestrationApi__DefaultOrganizationId=$OrganizationId.ToString('D'); OrchestrationApi__DefaultWorkspaceId=$WorkspaceId.ToString('D') }
$old = @{}
try {
    foreach($key in $settings.Keys){ $old[$key] = [Environment]::GetEnvironmentVariable($key,'Process'); [Environment]::SetEnvironmentVariable($key,$settings[$key],'Process') }
    $logs = Join-Path $root 'logs'; New-Item -ItemType Directory -Force -Path $logs | Out-Null; $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $apiProcess = Start-Process $dotnet -WindowStyle Hidden -WorkingDirectory (Join-Path $root 'api') -ArgumentList @($api,'--urls',"http://127.0.0.1:$ApiPort") -RedirectStandardOutput (Join-Path $logs "$stamp-api.out.log") -RedirectStandardError (Join-Path $logs "$stamp-api.err.log") -PassThru
    $webProcess = Start-Process $dotnet -WindowStyle Hidden -WorkingDirectory (Join-Path $root 'web') -ArgumentList @($web,'--urls',"http://127.0.0.1:$WebPort") -RedirectStandardOutput (Join-Path $logs "$stamp-web.out.log") -RedirectStandardError (Join-Path $logs "$stamp-web.err.log") -PassThru
    [pscustomobject]@{ ApiPid=$apiProcess.Id; WebPid=$webProcess.Id; Board="http://127.0.0.1:$WebPort/work/$OrganizationId/$WorkspaceId"; Logs=$logs }
} finally { foreach($key in $old.Keys){ [Environment]::SetEnvironmentVariable($key,$old[$key],'Process') } }
