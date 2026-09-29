# Live, read-only database use. Requires private NEO_ORCHESTRATION_SQL and a build.
param([Parameter(Mandatory)][string]$BinaryDirectory,[Parameter(Mandatory)][Guid]$OrganizationId,[Parameter(Mandatory)][Guid]$WorkspaceId)
$ErrorActionPreference='Stop'
$tool=Join-Path $PSScriptRoot '../../tools/Start-Local.ps1'
$parseErrors=$null;$tokens=$null
$null=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path $tool).Path,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Launcher parsing failed'}
$options=@{BinaryDirectory=$BinaryDirectory;OrganizationId=$OrganizationId;WorkspaceId=$WorkspaceId}
$before=$env:ASPNETCORE_ENVIRONMENT
$one=& $tool @options
$two=& $tool @options
if(!$one.Ready -or !$two.Ready -or !$two.ApiReused -or !$two.WebReused -or $one.ApiPid -ne $two.ApiPid -or $one.WebPid -ne $two.WebPid){throw 'Replay must preserve ready host identities'}
if([string]$env:ASPNETCORE_ENVIRONMENT -ne [string]$before){throw 'Parent environment changed'}
# Reserve an arbitrary private port in this test process. The launcher must
# reject it without stopping this process or starting its peer host.
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start()
try{
    $port=$listener.LocalEndpoint.Port
    if($port -in @(5180,5181)){throw 'Unexpected reserved port'}
    $rejected=$false
    try{& $tool @options -ApiPort $port -WebPort 5181|Out-Null}catch{
        if($_.Exception.Message -like '*different process/build*'){$rejected=$true}else{throw}
    }
    if(!$rejected -or !$listener.Server.IsBound){throw 'Foreign port guard failed'}
}finally{$listener.Stop()}
'PASS: syntax, live readiness, idempotent PID reuse, environment restoration, foreign-port refusal without termination.'
