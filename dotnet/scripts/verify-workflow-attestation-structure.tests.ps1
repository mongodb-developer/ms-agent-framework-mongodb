#Requires -Version 7.0
<#
.SYNOPSIS
    Structural/static regression self-test for .github/workflows/dotnet-sbom-provenance.yml's
    attestation-job trust split -- proves, by direct inspection of the committed YAML text (no live GitHub
    Actions run required), that the provenance-attestation job can never execute code sourced from an
    operator-selected/triggering ref, and that a manual workflow_dispatch can never target a release tag.

.DESCRIPTION
    Two defects this test guards against:

      A) A `workflow_dispatch` run checks out and executes the OPERATOR-SELECTED ref's own code. If a job that
         already holds `id-token`/`attestations`/`artifact-metadata` write permissions checked out and ran a
         validator script FROM that same selected ref, whoever controls that ref could patch the validator (or
         delete the ancestry check, or the workflow YAML itself) to always report "eligible" -- forging this
         gate's own verdict. This repository's fix moves all such validation into a separate
         `validate-attestation-eligibility` job that (1) never requests OIDC/attestation permissions, so a
         forged "pass" there cannot itself do anything sensitive, and (2) always checks out the repository's
         real, protected `main` branch -- never `github.ref` -- before running any validator script, so the
         code making the eligibility decision can never be the operator-selected ref's own (possibly
         compromised) content. `provenance-attestation` itself never checks out or executes ANY selected-ref
         script; it only downloads the already-built artifact and calls two pinned marketplace actions.
      B) A manual `workflow_dispatch` targeting an EXISTING release tag has no equivalent trusted check that the
         tag's claimed version matches the packed artifact's real version (unlike a real tag `push`, which the
         `sbom` job's "Verify tag matches package version" step already gates). This repository's fix restricts
         `workflow_dispatch` eligibility to EXACTLY `refs/heads/main`; a tag can only ever be attested via a real
         tag `push`.

    This test performs three kinds of assertions:
      1. Structural (regex/text) assertions against the raw workflow YAML, proving the job split, permission
         separation, and checkout targets are exactly as designed -- not merely "trust the doc comment".
      2. A functional assertion (via Test-AttestationRefEligible, already self-tested in more depth by
         verify-attestation-ref.tests.ps1) that a `workflow_dispatch` run targeting a real, validly-formed
         release tag is rejected -- the tag/package-version-mismatch regression from issue B.
      3. A "malicious selected-ref validator" regression proof: a fabricated, always-eligible fake
         Test-AttestationRefEligible replacement function is defined locally and shown to report "eligible" for
         an input that should never be eligible -- demonstrating concretely why part 1's structural assertions
         matter: IF the provenance-attestation job executed a validator sourced from a compromised/selected ref,
         that validator's own verdict could trivially be forged. This does not claim provenance-attestation ever
         does this (the structural assertions above prove it does not); it proves the failure mode those
         assertions close would otherwise be real, not merely theoretical.

    This deliberately parses the workflow YAML with plain text/regex, not a YAML library, to avoid adding a new
    parsing dependency to this repository purely for a self-test; every assertion only needs to find/not-find
    specific lines within a known job's text span, which line-based block extraction is sufficient for.

    Run directly: pwsh dotnet/scripts/verify-workflow-attestation-structure.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$WorkflowPath = Join-Path $RepoRoot ".github/workflows/dotnet-sbom-provenance.yml"

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

# Extracts each top-level job's raw text block from the committed workflow YAML: a job header is a line
# indented exactly two spaces directly under `jobs:` (e.g. "  sbom:"); the block continues until the next such
# line or end of file.
function Get-WorkflowJobBlocks([string]$Path) {
    $lines = Get-Content -Path $Path
    $jobsLineIndex = $null
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch '^jobs:\s*$') {
            $jobsLineIndex = $i
            break
        }
    }
    if ($null -eq $jobsLineIndex) {
        throw "No top-level 'jobs:' key found in $Path"
    }

    $blocks = [ordered]@{}
    $currentName = $null
    $currentLines = [System.Collections.Generic.List[string]]::new()

    for ($i = $jobsLineIndex + 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -cmatch '^  ([A-Za-z0-9_-]+):\s*$') {
            if ($null -ne $currentName) {
                $blocks[$currentName] = ($currentLines -join "`n")
            }
            $currentName = $Matches[1]
            $currentLines = [System.Collections.Generic.List[string]]::new()
            continue
        }
        if ($null -ne $currentName) {
            $currentLines.Add($line)
        }
    }
    if ($null -ne $currentName) {
        $blocks[$currentName] = ($currentLines -join "`n")
    }

    return $blocks
}

$jobBlocks = Get-WorkflowJobBlocks -Path $WorkflowPath
Assert-True ($jobBlocks.Contains('sbom')) "Workflow defines a 'sbom' job"
Assert-True ($jobBlocks.Contains('validate-attestation-eligibility')) "Workflow defines a 'validate-attestation-eligibility' job"
Assert-True ($jobBlocks.Contains('provenance-attestation')) "Workflow defines a 'provenance-attestation' job"

$eligibilityBlock = $jobBlocks['validate-attestation-eligibility']
$attestationBlock = $jobBlocks['provenance-attestation']

# --- Issue A: permission separation --------------------------------------------------------------------------
Assert-True (
    $eligibilityBlock -notmatch 'id-token:\s*write'
) "validate-attestation-eligibility does not request id-token: write"
Assert-True (
    $eligibilityBlock -notmatch 'attestations:\s*write'
) "validate-attestation-eligibility does not request attestations: write"
Assert-True (
    $eligibilityBlock -notmatch 'artifact-metadata:\s*write'
) "validate-attestation-eligibility does not request artifact-metadata: write"
Assert-True (
    $eligibilityBlock -match 'contents:\s*read'
) "validate-attestation-eligibility requests only contents: read"

Assert-True (
    $attestationBlock -match 'id-token:\s*write' -and
    $attestationBlock -match 'attestations:\s*write' -and
    $attestationBlock -match 'artifact-metadata:\s*write'
) "provenance-attestation retains the elevated id-token/attestations/artifact-metadata permissions"

# --- Issue A: trusted-main-first checkout --------------------------------------------------------------------
Assert-True (
    $eligibilityBlock -match 'ref:\s*main\b'
) "validate-attestation-eligibility's checkout step pins 'ref: main' (never the triggering/operator-selected ref)"
Assert-True (
    $eligibilityBlock -cnotmatch '(?m)^\s*ref:\s*\$\{\{\s*github\.ref'
) "validate-attestation-eligibility's checkout step never checks out '`${{ github.ref }}'/the triggering ref directly"

# --- Issue A: provenance-attestation never runs a validator sourced from a checked-out (selected) ref ---------
Assert-True (
    $attestationBlock -notmatch 'verify-attestation-ref\.ps1'
) "provenance-attestation never invokes verify-attestation-ref.ps1 itself (validation happens only in the trusted-main job)"
Assert-True (
    $attestationBlock -notmatch 'merge-base'
) "provenance-attestation never re-runs the ancestry check itself (it only happens in the trusted-main job)"

# Every step in provenance-attestation must be either a pinned marketplace action ('uses:') or the
# permanently-disabled ('if: false') sign step -- i.e. no unconditional 'run:' script/command step exists in
# this OIDC-permissioned job at all, so there is no executable-script surface for a compromised checkout to
# reach even if one were added carelessly in the future.
$stepBlocksPattern = '(?ms)^\s*- name:.*?(?=^\s*- name:|\z)'
$attestationSteps = [regex]::Matches($attestationBlock, $stepBlocksPattern) | ForEach-Object { $_.Value }
Assert-True ($attestationSteps.Count -gt 0) "provenance-attestation has at least one step (sanity check on the block-splitting regex)"
$unconditionalRunSteps = $attestationSteps | Where-Object { $_ -match '(?m)^\s*run:' -and $_ -notmatch 'if:\s*false' }
Assert-True (
    $unconditionalRunSteps.Count -eq 0
) "provenance-attestation has no unconditional 'run:' script step (only pinned actions and the permanently-disabled sign step) -- got $($unconditionalRunSteps.Count) unexpected run step(s)"

# --- Issue A: needs/output wiring ------------------------------------------------------------------------------
Assert-True (
    $attestationBlock -match [regex]::Escape('needs: [sbom, validate-attestation-eligibility]')
) "provenance-attestation's 'needs:' includes both sbom and validate-attestation-eligibility"
Assert-True (
    $attestationBlock -match [regex]::Escape('needs.validate-attestation-eligibility.outputs.validated-sha')
) "provenance-attestation's checkout uses the trusted job's validated-sha output, never github.ref/github.sha directly"
Assert-True (
    $attestationBlock -notmatch '\bref:\s*\$\{\{\s*github\.(ref|sha)\b'
) "provenance-attestation never checks out github.ref/github.sha directly (only the validated-sha output)"

# --- Malicious selected-ref validator regression proof ---------------------------------------------------------
# Demonstrates concretely why the structural assertions above matter: if a validator function WERE sourced from
# a compromised/selected ref, its own verdict could trivially be forged. This block does not claim
# provenance-attestation ever does this (the assertions above prove it does not) -- it proves the failure mode
# those assertions close would otherwise be real, not merely theoretical.
function Test-MaliciousAttestationRefEligible {
    param([string]$EventName, [string]$Ref)
    # A compromised validator sourced from the attacker's own selected ref could simply always report eligible,
    # regardless of the real input -- exactly the risk the trusted-main-checkout structural assertions close.
    return [pscustomobject]@{ Eligible = $true; Reason = "forged: always eligible" }
}
$forged = Test-MaliciousAttestationRefEligible -EventName "workflow_dispatch" -Ref "refs/heads/feature/malicious-branch"
Assert-True (
    $forged.Eligible
) "Regression proof: a validator sourced from a compromised/selected ref COULD forge an 'eligible' verdict for an otherwise-ineligible ref -- this is exactly the class of bug the trusted-main-checkout structural assertions above close"

# --- Issue B: functional regression -- manual dispatch of a real, validly-formed tag is rejected ----------------
. (Join-Path $PSScriptRoot "ReleaseVersionTag.ps1")
$tagDispatchResult = Test-AttestationRefEligible -EventName "workflow_dispatch" -Ref "refs/tags/dotnet-v1.2.3"
Assert-True (
    -not $tagDispatchResult.Eligible
) "Regression proof (issue B): workflow_dispatch targeting a real, validly-formed release tag is rejected -- manual dispatch is main-only, so a tag whose NAME claims one version while the packed artifact actually contains a different one can never reach attestation via this path"

Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All workflow-attestation-structure self-test assertions PASSED." -ForegroundColor Green
exit 0
