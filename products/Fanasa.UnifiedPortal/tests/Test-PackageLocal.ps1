$ErrorActionPreference = 'Stop'
$packageScript = Join-Path $PSScriptRoot '../tools/Package-Local.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('fanasa-portal-package-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$packageStub = @{ Calls = 0; ExitCode = 0; EmitDll = $true }
$checks = 0
$previousSha = $env:GITHUB_SHA
$previousExit = $global:LASTEXITCODE

# Mock publish only: these tests exercise package guards, not a real .NET build.
function dotnet {
    $packageStub.Calls++
    Set-Variable -Name LASTEXITCODE -Value $packageStub.ExitCode -Scope 1
    if ($packageStub.ExitCode -eq 0 -and $packageStub.EmitDll) {
        $arguments = @($args)
        $outputIndex = [Array]::IndexOf($arguments, '-o')
        if ($outputIndex -lt 0) { throw 'Publish output argument missing.' }
        [IO.File]::WriteAllText((Join-Path $arguments[$outputIndex + 1] 'Fanasa.UnifiedPortal.Web.dll'), 'test-only mock DLL')
    }
}
function Expect-Failure([string]$OutputDirectory, [string]$ExpectedMessage) {
    try { & $packageScript -OutputDirectory $OutputDirectory }
    catch {
        if ($_.Exception.Message -notlike $ExpectedMessage) { throw }
        return
    }
    throw "Expected packaging failure: $ExpectedMessage"
}
try {
    $existingOutput = Join-Path $scratch 'existing-output/package'
    New-Item -ItemType Directory -Path $existingOutput | Out-Null
    $sentinel = Join-Path $existingOutput 'keep.txt'
    [IO.File]::WriteAllText($sentinel, 'preserve-existing-output')
    Expect-Failure $existingOutput 'OutputDirectory already exists*'
    if ([IO.File]::ReadAllText($sentinel) -ne 'preserve-existing-output' -or $packageStub.Calls -ne 0) { throw 'Existing output was modified or publish started.' }
    Write-Output 'PASS existing output is preserved before publish'
    $checks++

    $archiveParent = Join-Path $scratch 'existing-archive'
    New-Item -ItemType Directory -Path $archiveParent | Out-Null
    $existingArchive = Join-Path $archiveParent 'fanasa-unified-portal.zip'
    [IO.File]::WriteAllText($existingArchive, 'preserve-existing-archive')
    $unusedOutput = Join-Path $archiveParent 'package'
    Expect-Failure $unusedOutput 'Archive already exists*'
    if ([IO.File]::ReadAllText($existingArchive) -ne 'preserve-existing-archive' -or (Test-Path -LiteralPath $unusedOutput) -or $packageStub.Calls -ne 0) { throw 'Existing archive was modified or publish started.' }
    Write-Output 'PASS existing archive is preserved before publish'
    $checks++

    $failedOutput = Join-Path $scratch 'failed-publish/package'
    $packageStub.ExitCode = 23
    Expect-Failure $failedOutput 'dotnet publish failed with exit code 23*'
    if ((Test-Path -LiteralPath (Join-Path $failedOutput 'MANIFEST.json')) -or (Test-Path -LiteralPath (Join-Path $failedOutput '../fanasa-unified-portal.zip'))) { throw 'Failed publish produced a release package.' }
    Write-Output 'PASS failed publish cannot produce a manifest or ZIP'
    $checks++

    $missingOutput = Join-Path $scratch 'missing-dll/package'
    $packageStub.ExitCode = 0
    $packageStub.EmitDll = $false
    Expect-Failure $missingOutput 'Published portal DLL is missing*'
    if (Test-Path -LiteralPath (Join-Path $missingOutput '../fanasa-unified-portal.zip')) { throw 'Missing DLL produced a ZIP.' }
    Write-Output 'PASS missing portal DLL cannot produce a ZIP'
    $checks++

    $successOutput = Join-Path $scratch 'successful-publish/package'
    $packageStub.EmitDll = $true
    $env:GITHUB_SHA = 'a' * 40
    & $packageScript -OutputDirectory $successOutput
    $manifest = Get-Content -LiteralPath (Join-Path $successOutput 'MANIFEST.json') -Raw | ConvertFrom-Json
    if ($manifest.commit -ne $env:GITHUB_SHA -or $manifest.containsCredentials -ne $false -or -not (Test-Path -LiteralPath (Join-Path $successOutput '../fanasa-unified-portal.zip'))) { throw 'Successful mock package contract failed.' }
    Write-Output 'PASS successful mock publish produces the expected manifest and ZIP'
    $checks++
    Write-Output "$checks package guard checks passed. Publish was mocked; this is not release validation."
}
finally {
    $env:GITHUB_SHA = $previousSha
    $global:LASTEXITCODE = $previousExit
    # The GUID-named scratch tree is retained: the test itself never recursively deletes files.
    Write-Output "Test-only scratch retained at $scratch"
}
