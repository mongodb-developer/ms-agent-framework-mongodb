#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$workflows = @{}
foreach ($name in @('dotnet-quality.yml', 'dotnet-security.yml', 'dotnet-sbom-provenance.yml',
    'dotnet-integration.yml', 'dotnet-release.yml')) {
    $workflows[$name] = Get-Content (Join-Path $root ".github/workflows/$name") -Raw
}
$failures = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "[ OK ] $Message" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Message" -ForegroundColor Red; $script:failures++ }
}

foreach ($name in @('dotnet-quality.yml', 'dotnet-security.yml', 'dotnet-sbom-provenance.yml',
    'dotnet-integration.yml')) {
    Assert-True ($workflows[$name] -match '(?m)^\s+- build/dotnet-packaging-release\s*$') `
        "$name runs on every .NET build-branch push"
}

$quality = $workflows['dotnet-quality.yml']
Assert-True ($quality -match 'dotnet-manifest-readiness:') 'Quality workflow exposes explicit manifest readiness status'
Assert-True ($quality -match '(?m)^\s+name: \.NET manifest release readiness\s*$') `
    'Stable required status check is named .NET manifest release readiness'
Assert-True ($quality -match '(?ms)dotnet-manifest-readiness:.*?if:\s*>-\s+always\(\).*?github\.event_name') `
    'Manifest readiness aggregate runs after failed, skipped, or cancelled dependencies'
Assert-True ($quality -match 'assert-required-job-results\.ps1') `
    'Manifest readiness explicitly rejects every nonsuccess dependency result'
Assert-True (
    $quality -match 'needs\.dotnet-quality\.result' -and
    $quality -match 'needs\.dotnet-agent-framework-compat\.result'
) 'Manifest readiness validates both required dependency results'
Assert-True (
    $quality.IndexOf('assert-required-job-results.ps1') -lt
    $quality.IndexOf('verify-build-release-readiness.ps1') -and
    $quality -notmatch 'continue-on-error'
) 'Dependency assertion is mandatory and runs before manifest validation'
Assert-True ($quality -match 'verify-build-release-readiness\.ps1') 'Manifest readiness uses the tested helper'
Assert-True ($quality -match 'needs:\s*\[?dotnet-quality') 'Manifest readiness depends on complete package quality'
Assert-True ($quality -match 'invoke-test-projects-with-trx\.ps1') 'Quality runs every credential-free project with unique TRX'
Assert-True ($quality -match 'quality-test-results' -and $quality -match 'if:\s*always\(\)') 'Quality retains TRX evidence on failure'

$release = $workflows['dotnet-release.yml']
Assert-True ($release -match '(?ms)push:\s+branches:\s+- main\s+paths:\s+- dotnet/src/MongoDB\.AgentFramework/MongoDB\.AgentFramework\.csproj') `
    'Automatic release runs only for main changes to the .NET manifest'
Assert-True ($release -match 'workflow_dispatch:') 'Manual RELEASE recovery remains available'
Assert-True ($release -match "github\.event_name == 'push'") 'Automatic push mode is explicitly accepted'
Assert-True ($release -match "github\.event_name == 'workflow_dispatch'.*inputs\.confirm_release == 'RELEASE'") `
    'Manual mode still requires RELEASE confirmation'
Assert-True ($release -match 'ref:\s*\$\{\{ github\.sha \}\}') 'Coordinator checks out immutable event SHA'
Assert-True ($release -match 'already-exact') 'Coordinator idempotently accepts an existing tag only at exact SHA'
Assert-True ($release -match 'conflict') 'Coordinator rejects a tag targeting another SHA'
Assert-True ($release -match 'gh workflow run dotnet-sbom-provenance\.yml') 'Coordinator explicitly dispatches trusted chain'

$readinessPath = Join-Path $root '.github/workflows/dotnet-quality.yml'
Assert-True ($quality -notmatch 'git tag|git push|dotnet nuget push|gh release create') `
    'Build readiness workflow has no tag or publication command'
Assert-True ($release -notmatch 'build/dotnet-packaging-release') `
    'Release coordinator cannot trigger from the build branch'

$attestation = Get-Content (Join-Path $root '.github/workflows/dotnet-release-attestation.yml') -Raw
Assert-True (
    $attestation -match "workflow_run\.event == 'workflow_dispatch'" -and
    $attestation -match "startsWith\(github\.event\.workflow_run\.head_branch, 'dotnet-v'\)"
) 'Ordinary build-branch SBOM runs cannot enter the privileged release graph'

if ($failures -gt 0) { exit 1 }
Write-Host 'All automatic readiness workflow self-tests PASSED.' -ForegroundColor Green
