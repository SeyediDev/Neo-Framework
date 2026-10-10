param(
    [string]$BaseUrl = 'http://127.0.0.1:15190',
    [ValidateRange(1, 60)][int]$TimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
$portalBaseUri = $null
if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$portalBaseUri) -or
    $portalBaseUri.Scheme -notin @('http', 'https') -or $portalBaseUri.UserInfo -or
    $portalBaseUri.Query -or $portalBaseUri.Fragment -or $portalBaseUri.AbsolutePath -ne '/') {
    throw 'BaseUrl must be the trusted portal HTTP(S) origin, without credentials or query.'
}
$portalOrigin = $portalBaseUri.GetLeftPart([UriPartial]::Authority)
function Get-PortalResponse([string]$Path) {
    $result = Invoke-WebRequest -UseBasicParsing -Uri ($portalOrigin + $Path) -TimeoutSec $TimeoutSeconds
    if ($result.StatusCode -ne 200) { throw "Unexpected HTTP status for $Path" }
    return $result
}
$checks = 0
foreach ($case in @(
    @{ Path = '/?tenantId=00000000-0000-0000-0000-000000000000'; Home = $true },
    @{ Path = '/Index?tenantId=00000000-0000-0000-0000-000000000000'; Home = $true },
    @{ Path = '/index?tenantId=00000000-0000-0000-0000-000000000000'; Home = $true },
    @{ Path = '/Error'; Home = $false }
)) {
    $response = Get-PortalResponse $case.Path
    $navigation = [Regex]::Match($response.Content, '<nav\b[^>]*aria-label="ناوبری اصلی"[^>]*>(?<nav>[\s\S]*?)</nav>').Groups['nav'].Value
    if (-not $navigation) { throw "Main navigation missing on $($case.Path)" }
    if ($case.Home) {
        foreach ($target in @('#top', '#workspace', '#centers')) {
            if (-not $navigation.Contains('href="' + $target + '"')) { throw "Same-page link $target missing on $($case.Path)" }
        }
        if ($navigation -match 'href="/#(workspace|centers)"') { throw "Navigation reloads the document on $($case.Path)" }
    } else {
        foreach ($target in @('/', '/#workspace', '/#centers')) {
            if (-not $navigation.Contains('href="' + $target + '"')) { throw "Portal fallback link $target missing on $($case.Path)" }
        }
    }
    Write-Output "PASS navigation $($case.Path)"
    $checks++
}
$health = (Get-PortalResponse '/health/live').Content | ConvertFrom-Json
if ($health.status -ne 'Healthy') { throw 'Portal live health failed.' }
Write-Output 'PASS live health'
$checks++
$workspaceResponse = Get-PortalResponse '/?handler=Workspace'
$workspace = $workspaceResponse.Content | ConvertFrom-Json
if ($workspace.status -ne 'SignInRequired' -or $workspace.organizations.Count -ne 0 -or
    $workspace.products.Count -ne 0 -or $workspaceResponse.Headers['Cache-Control'] -ne 'no-store') {
    throw 'Anonymous workspace exposes data or lacks sign-in/no-store protection.'
}
Write-Output 'PASS anonymous workspace: no organizations/products and no-store'
$checks++
if ((Get-PortalResponse '/portal.js').Content -notmatch 'workspaceTimeoutMs = 50000') { throw 'Refresh deadline asset missing.' }
Write-Output 'PASS refresh deadline asset'
$checks++
if ((Get-PortalResponse '/portal.css').Content -notmatch 'max-width:380px') { throw 'Narrow-mobile stylesheet missing.' }
Write-Output 'PASS narrow-mobile stylesheet asset (not a visual layout test)'
$checks++
Write-Output "$checks HTTP checks passed. Requests were anonymous; authorized SSO/catalog and visual layout were not tested."
