#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'assert-required-job-results.ps1'
$failures = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "[ OK ] $Message" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Message" -ForegroundColor Red; $script:failures++ }
}
function Invoke-ResultCheck([string]$Quality, [string]$Compatibility) {
    & pwsh -NoProfile -File $scriptPath `
        -DotNetQualityResult $Quality -CompatibilityResult $Compatibility 2>&1 | Out-Null
    return $LASTEXITCODE
}

Assert-True ((Invoke-ResultCheck success success) -eq 0) 'Aggregate accepts only all-success dependencies'
foreach ($result in @('failure', 'skipped', 'cancelled')) {
    Assert-True ((Invoke-ResultCheck $result success) -ne 0) "Aggregate rejects $result quality dependency"
    Assert-True ((Invoke-ResultCheck success $result) -ne 0) "Aggregate rejects $result compatibility dependency"
}

if ($failures -gt 0) { exit 1 }
Write-Host 'All required-job-result self-tests PASSED.' -ForegroundColor Green
