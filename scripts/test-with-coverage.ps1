# PowerShell script to run tests with code coverage
param(
    [string]$OutputPath = "coverage",
    [string]$Format = "cobertura,json,opencover",
    [string]$ReportFormat = "Html"
)

Write-Host "=============================================================" -ForegroundColor Cyan
Write-Host "[*] Running Tests with Code Coverage" -ForegroundColor Yellow
Write-Host "=============================================================" -ForegroundColor Cyan

$SolutionPath = Join-Path $PSScriptRoot "..\Neo.sln"
$CoveragePath = Join-Path $PSScriptRoot "..\$OutputPath"
$RunSettingsPath = Join-Path $PSScriptRoot "..\coverlet.runsettings"

# Ensure coverage directory exists
if (-not (Test-Path $CoveragePath)) {
    New-Item -ItemType Directory -Path $CoveragePath -Force | Out-Null
}

Write-Host "`n[1/3] Running tests with coverage collection..." -ForegroundColor Yellow
dotnet test $SolutionPath `
    --settings $RunSettingsPath `
    --collect:"XPlat Code Coverage" `
    --results-directory $CoveragePath `
    --logger "console;verbosity=minimal" `
    --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[ERROR] Tests failed!" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`n[2/3] Finding coverage files..." -ForegroundColor Yellow
$CoverageFiles = Get-ChildItem -Path $CoveragePath -Recurse -Filter "coverage.cobertura.xml" | Select-Object -First 1

if (-not $CoverageFiles) {
    Write-Host "`n[ERROR] Coverage file not found!" -ForegroundColor Red
    exit 1
}

Write-Host "`n[3/3] Generating coverage report..." -ForegroundColor Yellow

# Install reportgenerator if not available
$ReportGenerator = "reportgenerator"
try {
    dotnet tool list --global | Select-String $ReportGenerator | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Installing reportgenerator tool..." -ForegroundColor Yellow
        dotnet tool install --global dotnet-reportgenerator-globaltool
    }
} catch {
    Write-Host "Installing reportgenerator tool..." -ForegroundColor Yellow
    dotnet tool install --global dotnet-reportgenerator-globaltool
}

$ReportPath = Join-Path $CoveragePath "report"
reportgenerator `
    -reports:"$($CoverageFiles.FullName)" `
    -targetdir:"$ReportPath" `
    -reporttypes:"$ReportFormat" `
    -classfilters:"-*Tests*" `
    -verbosity:Info

if ($LASTEXITCODE -eq 0) {
    Write-Host "`n=============================================================" -ForegroundColor Green
    Write-Host "[SUCCESS] Coverage report generated!" -ForegroundColor Green
    Write-Host "=============================================================" -ForegroundColor Green
    Write-Host "`nCoverage Report: $ReportPath" -ForegroundColor Cyan
    
    if ($ReportFormat -eq "Html") {
        $IndexFile = Join-Path $ReportPath "index.html"
        if (Test-Path $IndexFile) {
            Write-Host "Open in browser: file:///$($IndexFile.Replace('\', '/'))" -ForegroundColor Cyan
        }
    }
    
    Write-Host "`nCoverage files:" -ForegroundColor Yellow
    Get-ChildItem -Path $CoveragePath -Filter "*.xml" | ForEach-Object {
        Write-Host "  - $($_.Name)" -ForegroundColor Gray
    }
} else {
    Write-Host "`n[ERROR] Failed to generate coverage report!" -ForegroundColor Red
    exit 1
}

