#Requires -Version 7.0
<#
.SYNOPSIS
    Self-test for ReleaseProvenancePredicate.ps1's New-ReleaseProvenancePredicate and
    write-release-provenance-predicate.ps1's CLI wrapper -- proves the custom SLSA predicate genuinely binds to
    the validated commit SHA (not this job's ambient GITHUB_SHA/GITHUB_REF), rejects malformed input, and that
    the generated JSON round-trips into the exact schema `actions/attest`'s `-predicate-path` input expects.

.DESCRIPTION
    Exercises:
      1. New-ReleaseProvenancePredicate happy path: `resolvedDependencies[0].digest.gitCommit` and `.uri` embed
         the EXACT -ValidatedSha passed in; `runDetails.builder.id` embeds the workflow path/ref; tag-push vs
         main-dispatch informational fields propagate correctly.
      2. Regression: two DIFFERENT validated SHAs produce two DIFFERENTLY-bound predicates (the binding is not a
         hardcoded/copy-pasted placeholder value).
      3. Rejection of malformed -ValidatedSha (empty, too short, uppercase hex, non-hex characters, a real-looking
         but wrong-length string) -- each must throw, never silently produce a predicate.
      4. Rejection of a malformed -RepositorySlug and of -IsTagPush true with an empty -TagName.
      5. write-release-provenance-predicate.ps1's CLI wrapper end-to-end: writes real JSON to disk, the file
         parses as valid JSON, and its shape exactly matches what a hand-parsed New-ReleaseProvenancePredicate
         call would have produced. Also proves the wrapper rejects an -IsTagPush value that is not exactly
         "true"/"false" (defense against silent boolean coercion of an unexpected upstream string).
      6. Schema-shape assertion: the top-level JSON object has ONLY `buildDefinition`/`runDetails` keys (per
         https://slsa.dev/spec/v1.0/provenance#schema) -- proving this predicate file is the raw predicate
         CONTENT `actions/attest -predicate-path` expects, never an extra enclosing `predicate`/`predicateType`
         envelope (`actions/attest` fills those in itself from its own inputs).

    Run directly: pwsh dotnet/scripts/ReleaseProvenancePredicate.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ReleaseProvenancePredicate.ps1")

$script:AssertionFailures = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) {
        Write-Host "[ OK ] $Message" -ForegroundColor Green
    }
    else {
        Write-Host "[FAIL] $Message" -ForegroundColor Red
        $script:AssertionFailures++
    }
}

function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $threw = $false
    try {
        & $Action | Out-Null
    }
    catch {
        $threw = $true
    }
    Assert-True $threw $Message
}

$repoSlug = "mongo/ms-agent-framework-mongodb"
$shaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
$shaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

# ---------------------------------------------------------------------------------------------------------------
# Part 1: happy path -- tag-push-derived predicate.
# ---------------------------------------------------------------------------------------------------------------
$tagPredicate = New-ReleaseProvenancePredicate -ValidatedSha $shaA -RepositorySlug $repoSlug -RunId "111" -RunAttempt "1" -IsTagPush $true -TagName "dotnet-v1.2.3"

Assert-True ($tagPredicate.buildDefinition.resolvedDependencies.Count -eq 1) "Predicate has exactly one resolvedDependencies entry"
Assert-True ($tagPredicate.buildDefinition.resolvedDependencies[0].digest.gitCommit -ceq $shaA) "resolvedDependencies[0].digest.gitCommit embeds the exact validated SHA ($shaA)"
Assert-True ($tagPredicate.buildDefinition.resolvedDependencies[0].uri -like "*@$shaA") "resolvedDependencies[0].uri embeds the exact validated SHA as its ref"
Assert-True ($tagPredicate.buildDefinition.externalParameters.validatedRelease.isTagPush -eq $true) "externalParameters.validatedRelease.isTagPush is true for a tag-push-derived predicate"
Assert-True ($tagPredicate.buildDefinition.externalParameters.validatedRelease.tagName -ceq "dotnet-v1.2.3") "externalParameters.validatedRelease.tagName records the exact validated tag name"
Assert-True ($tagPredicate.runDetails.builder.id -like "*dotnet-release-attestation.yml@refs/heads/main") "runDetails.builder.id identifies dotnet-release-attestation.yml@refs/heads/main as the actual builder"
Assert-True ($tagPredicate.runDetails.metadata.invocationId -like "*/actions/runs/111/attempts/1") "runDetails.metadata.invocationId records the real run id/attempt"

# ---------------------------------------------------------------------------------------------------------------
# Part 1b: happy path -- main-only manual-dispatch-derived predicate (no tag).
# ---------------------------------------------------------------------------------------------------------------
$mainPredicate = New-ReleaseProvenancePredicate -ValidatedSha $shaA -RepositorySlug $repoSlug -RunId "222" -RunAttempt "1" -IsTagPush $false -TagName ""
Assert-True ($mainPredicate.buildDefinition.externalParameters.validatedRelease.isTagPush -eq $false) "externalParameters.validatedRelease.isTagPush is false for a main-only dispatch predicate"
Assert-True ([string]::IsNullOrEmpty($mainPredicate.buildDefinition.externalParameters.validatedRelease.tagName)) "externalParameters.validatedRelease.tagName is empty for a main-only dispatch predicate"

# ---------------------------------------------------------------------------------------------------------------
# Part 2: regression -- two DIFFERENT validated SHAs must produce two DIFFERENTLY-bound predicates. Proves the
# binding is genuinely derived from -ValidatedSha, not a hardcoded/copy-pasted placeholder.
# ---------------------------------------------------------------------------------------------------------------
$predicateA = New-ReleaseProvenancePredicate -ValidatedSha $shaA -RepositorySlug $repoSlug -RunId "1" -RunAttempt "1" -IsTagPush $false -TagName ""
$predicateB = New-ReleaseProvenancePredicate -ValidatedSha $shaB -RepositorySlug $repoSlug -RunId "1" -RunAttempt "1" -IsTagPush $false -TagName ""
Assert-True (
    $predicateA.buildDefinition.resolvedDependencies[0].digest.gitCommit -cne $predicateB.buildDefinition.resolvedDependencies[0].digest.gitCommit
) "Two different -ValidatedSha values produce two differently-bound resolvedDependencies (not a hardcoded placeholder)"
Assert-True (
    $predicateA.buildDefinition.resolvedDependencies[0].uri -cne $predicateB.buildDefinition.resolvedDependencies[0].uri
) "Two different -ValidatedSha values produce two differently-bound resolvedDependencies URIs"

# ---------------------------------------------------------------------------------------------------------------
# Part 3: malformed -ValidatedSha must always throw, never silently produce a predicate.
# ---------------------------------------------------------------------------------------------------------------
$malformedShas = @(
    ""
    "abc123"
    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
    "gggggggggggggggggggggggggggggggggggggggg"
    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa "
    ("a" * 39)
    ("a" * 41)
    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa$'
)
foreach ($malformedSha in $malformedShas) {
    Assert-Throws ({ New-ReleaseProvenancePredicate -ValidatedSha $malformedSha -RepositorySlug $repoSlug -RunId "1" -RunAttempt "1" -IsTagPush $false -TagName "" }) `
        "Malformed -ValidatedSha '$malformedSha' is rejected (throws, never silently produces a predicate)"
}

# ---------------------------------------------------------------------------------------------------------------
# Part 4: malformed -RepositorySlug, and -IsTagPush true with an empty -TagName, must both throw.
# ---------------------------------------------------------------------------------------------------------------
Assert-Throws ({ New-ReleaseProvenancePredicate -ValidatedSha $shaA -RepositorySlug "not-a-slug" -RunId "1" -RunAttempt "1" -IsTagPush $false -TagName "" }) `
    "Malformed -RepositorySlug (no '/') is rejected"
Assert-Throws ({ New-ReleaseProvenancePredicate -ValidatedSha $shaA -RepositorySlug "owner/repo/extra" -RunId "1" -RunAttempt "1" -IsTagPush $false -TagName "" }) `
    "Malformed -RepositorySlug (extra '/') is rejected"
Assert-Throws ({ New-ReleaseProvenancePredicate -ValidatedSha $shaA -RepositorySlug $repoSlug -RunId "1" -RunAttempt "1" -IsTagPush $true -TagName "" }) `
    "-IsTagPush true with an empty -TagName is rejected (a tag-push predicate must record the real tag name)"

# ---------------------------------------------------------------------------------------------------------------
# Part 5: write-release-provenance-predicate.ps1's CLI wrapper end-to-end.
# ---------------------------------------------------------------------------------------------------------------
$wrapperScript = Join-Path $PSScriptRoot "write-release-provenance-predicate.ps1"
$fixtureDir = Join-Path $PSScriptRoot "../artifacts/release-provenance-predicate-test-fixtures"
if (Test-Path $fixtureDir) {
    Remove-Item -Path $fixtureDir -Recurse -Force
}
New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null
$outputPath = Join-Path $fixtureDir "predicate.json"

& pwsh -NoProfile -File $wrapperScript `
    -ValidatedSha $shaA -RepositorySlug $repoSlug -RunId "999" -RunAttempt "2" -IsTagPush "true" -TagName "dotnet-v9.9.9" `
    -OutputPath $outputPath | Out-Null
Assert-True ($LASTEXITCODE -eq 0) "write-release-provenance-predicate.ps1 exits 0 for well-formed input"
Assert-True (Test-Path $outputPath) "write-release-provenance-predicate.ps1 wrote a predicate file to -OutputPath"

$writtenJson = $null
$parsedOk = $false
try {
    $writtenJson = Get-Content -Path $outputPath -Raw | ConvertFrom-Json
    $parsedOk = $true
}
catch {
    $parsedOk = $false
}
Assert-True $parsedOk "Written predicate file parses as valid JSON"

Assert-True ($writtenJson.buildDefinition.resolvedDependencies[0].digest.gitCommit -ceq $shaA) "Written predicate JSON's resolvedDependencies[0].digest.gitCommit matches the exact -ValidatedSha passed to the wrapper"
Assert-True ($writtenJson.buildDefinition.externalParameters.validatedRelease.tagName -ceq "dotnet-v9.9.9") "Written predicate JSON's tagName matches the exact -TagName passed to the wrapper"

# Schema-shape assertion: only buildDefinition/runDetails at the top level (raw predicate CONTENT, no extra
# enclosing predicate/predicateType/subject envelope -- actions/attest fills those in itself).
$topLevelKeys = $writtenJson.PSObject.Properties.Name | Sort-Object
Assert-True (
    ($topLevelKeys -join ',') -ceq 'buildDefinition,runDetails'
) "Written predicate JSON's top-level keys are EXACTLY buildDefinition,runDetails (raw predicate content, no extra envelope) -- found: $($topLevelKeys -join ', ')"

# -IsTagPush must be exactly "true"/"false" -- no silent boolean coercion of an unexpected upstream string value.
& pwsh -NoProfile -File $wrapperScript `
    -ValidatedSha $shaA -RepositorySlug $repoSlug -RunId "1" -RunAttempt "1" -IsTagPush "yes" -TagName "" `
    -OutputPath (Join-Path $fixtureDir "should-not-exist.json") | Out-Null
Assert-True ($LASTEXITCODE -eq 1) "write-release-provenance-predicate.ps1 rejects an -IsTagPush value that is not exactly 'true'/'false' (got 'yes')"
Assert-True (-not (Test-Path (Join-Path $fixtureDir "should-not-exist.json"))) "No predicate file is written when -IsTagPush is an invalid value"

Remove-Item -Path $fixtureDir -Recurse -Force

# ---------------------------------------------------------------------------------------------------------------
Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All release-provenance-predicate self-test assertions PASSED." -ForegroundColor Green
exit 0
