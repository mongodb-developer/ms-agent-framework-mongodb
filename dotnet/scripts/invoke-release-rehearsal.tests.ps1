#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'invoke-release-rehearsal.ps1'
$text = Get-Content $path -Raw
$failures = 0
function Assert-Contains([string]$Pattern, [string]$Message) {
    if ($text -match $Pattern) { Write-Host "[ OK ] $Message" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Message" -ForegroundColor Red; $script:failures++ }
}
function Assert-Excludes([string]$Pattern, [string]$Message) {
    if ($text -notmatch $Pattern) { Write-Host "[ OK ] $Message" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Message" -ForegroundColor Red; $script:failures++ }
}

Assert-Contains 'dotnet format' 'Rehearsal validates formatting'
Assert-Contains 'invoke-test-projects-with-trx\.ps1' 'Rehearsal runs every credential-free test project with unique TRX'
Assert-Contains 'resolve-agent-framework-versions\.ps1' 'Rehearsal resolves dynamic compatibility versions'
Assert-Contains 'verify-agent-framework-compatibility\.ps1' 'Rehearsal runs compatibility checks'
Assert-Contains 'verify-package\.ps1' 'Rehearsal reuses full package validation'
Assert-Contains 'checksums\.sha256\.txt' 'Rehearsal writes checksums'
Assert-Excludes 'dotnet\s+nuget\s+push' 'Rehearsal contains no NuGet publication'
Assert-Excludes 'git\s+push' 'Rehearsal contains no git push'

if ($failures -gt 0) { exit 1 }
Write-Host 'All release rehearsal structure self-tests PASSED.' -ForegroundColor Green
