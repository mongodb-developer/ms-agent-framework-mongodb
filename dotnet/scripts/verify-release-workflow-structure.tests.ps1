#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$coordinator = Get-Content (Join-Path $root '.github/workflows/dotnet-release.yml') -Raw
$attestation = Get-Content (Join-Path $root '.github/workflows/dotnet-release-attestation.yml') -Raw
$failures = 0
function Assert-Text([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "[ OK ] $Message" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Message" -ForegroundColor Red; $script:failures++ }
}

Assert-Text ($coordinator -match "github\.ref == 'refs/heads/main'") 'Coordinator is explicit-main only'
Assert-Text ($coordinator -match 'git merge-base --is-ancestor') 'Coordinator verifies main reachability'
Assert-Text ($coordinator -match 'git tag -a') 'Only Actions creates the annotated tag'
Assert-Text ($coordinator -match 'gh workflow run dotnet-sbom-provenance\.yml --ref main') 'Coordinator explicitly dispatches instead of assuming token push recursion'
Assert-Text ($coordinator -notmatch 'dotnet nuget push') 'Coordinator cannot publish NuGet packages'
Assert-Text ($attestation -match 'workflow_run:') 'Privileged attestation remains workflow_run isolated'
Assert-Text ($attestation -match 'environment: \$\{\{ vars\.NUGET_ENVIRONMENT \}\}') 'NuGet publish uses the configured protected environment'
Assert-Text ($attestation -match 'secrets\.NUGET_API_KEY') 'NuGet credential comes only from the environment secret'
Assert-Text ($attestation -match "vars\.NUGET_SOURCE_URL \|\| 'https://api\.nuget\.org/v3/index\.json'") 'Official NuGet feed is the documented configuration default'

$workflowFiles = Get-ChildItem (Join-Path $root '.github/workflows') -Filter '*.yml'
foreach ($file in $workflowFiles) {
    $text = Get-Content $file.FullName -Raw
    $mutableUses = [regex]::Matches($text, 'uses:\s+[^@\s]+@(?![0-9a-f]{40}(?:\s|$))\S+')
    Assert-Text ($mutableUses.Count -eq 0) "$($file.Name) pins every action to a full SHA"
}

if ($failures -gt 0) { exit 1 }
Write-Host 'All release workflow structure self-tests PASSED.' -ForegroundColor Green
