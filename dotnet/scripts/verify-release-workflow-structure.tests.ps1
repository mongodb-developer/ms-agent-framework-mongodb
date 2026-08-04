#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$coordinator = Get-Content (Join-Path $root '.github/workflows/dotnet-release.yml') -Raw
$attestation = Get-Content (Join-Path $root '.github/workflows/dotnet-release-attestation.yml') -Raw
$readiness = Get-Content (Join-Path $root 'dotnet/scripts/ReleaseReadiness.ps1') -Raw
$failures = 0
function Assert-Text([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "[ OK ] $Message" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Message" -ForegroundColor Red; $script:failures++ }
}

Assert-Text ($coordinator -match "github\.ref == 'refs/heads/main'") 'Coordinator is explicit-main only'
Assert-Text ($coordinator -match 'git merge-base --is-ancestor') 'Coordinator verifies main reachability'
Assert-Text ($coordinator -match 'ref: \$\{\{ github\.sha \}\}') 'Coordinator checks out immutable workflow_dispatch SHA'
Assert-Text ($coordinator -match '\$headSha -cne \$sha') 'Coordinator verifies checkout exactly equals workflow SHA'
Assert-Text (
    $coordinator -match 'Get-CanonicalNuGetVersion' -and
    $readiness -match 'NuGet\.Versioning\.NuGetVersion\]::Parse'
) 'Coordinator parses manifest version with NuGet.Versioning'
Assert-Text (
    $coordinator -match 'Get-CanonicalNuGetVersion' -and
    $readiness -match '\$canonical -cne \$Version'
) 'Coordinator rejects noncanonical NuGet versions'
Assert-Text ($coordinator -match 'git tag -a') 'Only Actions creates the annotated tag'
Assert-Text ($coordinator -match 'git tag -a "\$RELEASE_TAG" "\$\{\{ steps\.release\.outputs\.sha \}\}"') 'Tag targets immutable workflow SHA explicitly'
Assert-Text ($coordinator -match 'gh workflow run dotnet-sbom-provenance\.yml --ref "\$RELEASE_TAG"') 'Coordinator dispatches exact tag instead of mutable main'
Assert-Text ($coordinator -match 'release_sha="\$RELEASE_SHA"') 'Coordinator passes exact release SHA downstream'
Assert-Text ($coordinator -notmatch 'dotnet nuget push') 'Coordinator cannot publish NuGet packages'
Assert-Text ($attestation -match 'workflow_run:') 'Privileged attestation remains workflow_run isolated'
Assert-Text ($attestation -match "vars\.NUGET_PUBLISHING_APPROVED == 'true'") 'Publication requires explicit governance approval variable'
Assert-Text ($attestation -match 'environment: \$\{\{ vars\.NUGET_ENVIRONMENT \}\}') 'NuGet publish uses the configured protected environment'
Assert-Text ($attestation -match 'secrets\.NUGET_API_KEY') 'NuGet credential comes only from the environment secret'
Assert-Text ($attestation -match "\$env:NUGET_SOURCE_URL -cne 'https://api\.nuget\.org/v3/index\.json'") 'Publishing rejects every noncanonical NuGet source before push'
Assert-Text (
    $attestation.IndexOf('Validate canonical NuGet source before exposing publishing secret') -lt
    $attestation.IndexOf('NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}')
) 'Canonical source validation occurs before any step receives the API key'
Assert-Text ($attestation -match 'resolve-release-compatibility:') 'Protected graph dynamically resolves release compatibility'
Assert-Text ($attestation -match 'release-compatibility:') 'Protected graph tests dynamic release compatibility'
Assert-Text ($attestation -match 'needs: \[validate-attestation-eligibility, release-compatibility, provenance-attestation\]') 'Publish depends on dynamic release compatibility'
Assert-Text ($attestation -match 'invoke-test-projects-with-trx\.ps1') 'Release uses unique per-project TRX runner'

$workflowFiles = Get-ChildItem (Join-Path $root '.github/workflows') -Filter '*.yml'
foreach ($file in $workflowFiles) {
    $text = Get-Content $file.FullName -Raw
    $mutableUses = [regex]::Matches($text, 'uses:\s+[^@\s]+@(?![0-9a-f]{40}(?:\s|$))\S+')
    Assert-Text ($mutableUses.Count -eq 0) "$($file.Name) pins every action to a full SHA"
}

if ($failures -gt 0) { exit 1 }
Write-Host 'All release workflow structure self-tests PASSED.' -ForegroundColor Green
