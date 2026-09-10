#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseReadiness.ps1')
$failures = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "[ OK ] $Message" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Message" -ForegroundColor Red; $script:failures++ }
}

Assert-True ((Get-CanonicalNuGetVersion '1.2.3') -ceq '1.2.3') 'Stable canonical NuGet SemVer is accepted'
Assert-True ((Get-CanonicalNuGetVersion '0.1.0-preview.1') -ceq '0.1.0-preview.1') 'Prerelease canonical NuGet SemVer is accepted'
$nonCanonicalRejected = $false
try { Get-CanonicalNuGetVersion '1.2.3.0' | Out-Null } catch { $nonCanonicalRejected = $true }
Assert-True $nonCanonicalRejected 'NuGet-valid but noncanonical version is rejected'
$unsupportedTagRejected = $false
try { Get-CanonicalDotNetReleaseTag '1.2.3+metadata' | Out-Null } catch { $unsupportedTagRejected = $true }
Assert-True $unsupportedTagRejected 'Canonical NuGet version that cannot form the approved tag grammar is rejected'
Assert-True ((Get-CanonicalDotNetReleaseTag '0.1.0-preview.1') -ceq 'dotnet-v0.1.0-preview.1') 'Canonical version derives the approved release tag'
Assert-True ((Get-ReleaseTagDisposition -ExpectedSha ('a' * 40) -ExistingTagSha '') -ceq 'create') 'Missing tag may be created'
Assert-True ((Get-ReleaseTagDisposition -ExpectedSha ('a' * 40) -ExistingTagSha ('a' * 40)) -ceq 'already-exact') 'Rerun accepts tag at exact SHA'
Assert-True ((Get-ReleaseTagDisposition -ExpectedSha ('a' * 40) -ExistingTagSha ('b' * 40)) -ceq 'conflict') 'Tag at another SHA conflicts'

if ($failures -gt 0) { exit 1 }
Write-Host 'All release readiness self-tests PASSED.' -ForegroundColor Green
