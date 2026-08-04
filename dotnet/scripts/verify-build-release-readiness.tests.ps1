#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$text = Get-Content (Join-Path $PSScriptRoot 'verify-build-release-readiness.ps1') -Raw
$failures = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "[ OK ] $Message" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Message" -ForegroundColor Red; $script:failures++ }
}
Assert-True ($text -match 'Get-CanonicalNuGetVersion') 'Build readiness validates canonical NuGet SemVer'
Assert-True ($text -match 'verify-package\.ps1') 'Build readiness validates the package'
Assert-True ($text -match 'verify-release-tag\.ps1') 'Build readiness reuses package/tag agreement helper'
Assert-True ($text -match 'ls-remote --tags --refs') 'Build readiness checks remote tag conflicts'
Assert-True ($text -notmatch 'git\s+tag|git\s+push|dotnet\s+nuget\s+push|gh\s+release') 'Build readiness cannot tag or publish'
Assert-True ($text -match 'tagged = \$false' -and $text -match 'published = \$false') 'Readiness report explicitly records no release side effects'
if ($failures -gt 0) { exit 1 }
Write-Host 'All build release readiness structure self-tests PASSED.' -ForegroundColor Green
