# Run elevated. Preserve unrelated mappings; do not alter certificate trust.
$ErrorActionPreference = 'Stop'
$taskHostName = 'platform.fanasa.net.local'
$taskServerIp = '62.60.199.135'
$taskHostsPath = Join-Path $env:SystemRoot 'System32/drivers/etc/hosts'
$taskEntries = [System.IO.File]::ReadAllLines($taskHostsPath)
$taskExisting = @($taskEntries | Where-Object {
    (($_ -split '#', 2)[0] -split '\s+') -contains $taskHostName
})
if ($taskExisting.Count -gt 0) {
    if (@($taskExisting | Where-Object { $_ -notmatch ('^\s*' + [regex]::Escape($taskServerIp) + '\s+') }).Count -gt 0) {
        throw 'A conflicting platform hostname mapping exists; no changes made.'
    }
    Write-Output 'Platform hostname mapping already exists.'
    exit 0
}
$taskBackup = $taskHostsPath + '.platform-' + (Get-Date -Format 'yyyyMMddTHHmmss') + '.bak'
Copy-Item -LiteralPath $taskHostsPath -Destination $taskBackup
[System.IO.File]::AppendAllText($taskHostsPath, "`r`n$taskServerIp $taskHostName`r`n", [System.Text.Encoding]::ASCII)
Write-Output "Added $taskHostName; backup: $taskBackup"
