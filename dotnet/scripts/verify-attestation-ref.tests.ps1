#Requires -Version 7.0
<#
.SYNOPSIS
    Self-test for ReleaseVersionTag.ps1's Test-AttestationRefEligible -- proves the provenance-attestation job's
    workflow-logic ref gate accepts only a trusted release-tag push or a workflow_dispatch run targeting EXACTLY
    'refs/heads/main', and rejects every other event/ref combination, including an arbitrary
    workflow_dispatch-selected feature branch, a workflow_dispatch-selected release tag (the tag/package-version
    mismatch regression), and refs crafted with shell metacharacters.

.DESCRIPTION
    This gate exists because a `workflow_dispatch` run lets its operator select ANY existing branch/tag, and the
    `environment: dotnet-release-attestation` GitHub Environment protection rule that also guards the
    attestation job is an OWNER-SIDE configuration step this repository's workflow YAML cannot enforce or verify
    -- if that environment happened to have no protection rule configured, `workflow_dispatch` with
    `confirm_attestation: yes` against an arbitrary selected ref would otherwise still reach attestation. This
    self-test exercises Test-AttestationRefEligible directly (no real GitHub Actions run required):

      1. A trusted push of a valid `refs/tags/dotnet-v<version>` ref -> Eligible.
      2. A push of a non-tag ref, or a tag not matching the release-tag grammar -> NOT eligible.
      3. workflow_dispatch targeting `refs/heads/main` -> Eligible.
      4. workflow_dispatch targeting an arbitrary OTHER branch (the exact fail-open scenario this gate exists
         for) -> NOT eligible.
      5. workflow_dispatch targeting a validly-formed release tag -> NOT eligible (manual dispatch is
         main-only: there is no trusted tag/package-version match check for this path the way there is for a
         real tag push, so a `workflow_dispatch` against an existing `dotnet-v1.2.3` tag whose packed artifact
         actually contains a different version, e.g. `0.1.0-preview.1`, must never reach attestation).
      6. workflow_dispatch targeting a malformed/garbage tag-shaped ref (including one embedding `$()`, a quote,
         or a semicolon) -> NOT eligible (subsumed by case 5, but kept explicit).
      7. Any other event (e.g. pull_request) -> NOT eligible, regardless of ref.
      8. Case sensitivity: 'refs/heads/MAIN' and 'REFS/HEADS/main' are NOT eligible (git refs are case-sensitive;
         only the exact 'refs/heads/main' is the protected branch).

    Run directly: pwsh dotnet/scripts/verify-attestation-ref.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ReleaseVersionTag.ps1")

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

function Assert-Eligible([string]$EventName, [string]$Ref, [string]$Label) {
    $result = Test-AttestationRefEligible -EventName $EventName -Ref $Ref
    Assert-True $result.Eligible "$Label -- ELIGIBLE (event='$EventName', ref='$Ref') -- reason: $($result.Reason)"
}

function Assert-NotEligible([string]$EventName, [string]$Ref, [string]$Label) {
    $result = Test-AttestationRefEligible -EventName $EventName -Ref $Ref
    Assert-True (-not $result.Eligible) "$Label -- NOT eligible (event='$EventName', ref='$Ref') -- reason: $($result.Reason)"
}

# ---------------------------------------------------------------------------------------------------------------
# Case 1/2: push event.
# ---------------------------------------------------------------------------------------------------------------
Assert-Eligible "push" "refs/tags/dotnet-v1.2.3" "Push of a valid release tag"
Assert-Eligible "push" "refs/tags/dotnet-v0.1.0-preview.1" "Push of a valid pre-release-shaped release tag"
Assert-NotEligible "push" "refs/heads/main" "Push of the main branch itself (not a tag push)"
Assert-NotEligible "push" "refs/tags/v1.2.3" "Push of a tag missing the required 'dotnet-v' prefix"
Assert-NotEligible "push" "refs/tags/dotnet-v" "Push of a tag with an empty version suffix (grammar requires at least one char after 'dotnet-v')"
Assert-NotEligible "push" "refs/tags/dotnet-v1.2.3-actually-a-different-branch/../../etc" "Push of a maliciously crafted ref that is not a clean tag path"

# ---------------------------------------------------------------------------------------------------------------
# Case 3/4/5/6: workflow_dispatch event -- the primary scenario this gate exists for.
# ---------------------------------------------------------------------------------------------------------------
Assert-Eligible "workflow_dispatch" "refs/heads/main" "workflow_dispatch targeting the protected main branch"

# This is the direct regression proof: an operator (or anyone with workflow_dispatch permission) selecting an
# arbitrary, non-main feature branch must NOT be eligible for attestation, even with confirm_attestation=yes,
# regardless of whether the dotnet-release-attestation GitHub Environment has a protection rule configured.
Assert-NotEligible "workflow_dispatch" "refs/heads/feature/some-topic-branch" "workflow_dispatch targeting an ARBITRARY selected feature branch (the fail-open scenario)"
Assert-NotEligible "workflow_dispatch" "refs/heads/release/1.x" "workflow_dispatch targeting an arbitrary release-looking-but-not-main branch"

# This is the tag/package-version-mismatch regression proof: a workflow_dispatch run targeting an EXISTING,
# validly-formed release tag must still be rejected, because manual dispatch has no equivalent trusted check
# (unlike a real tag push) that the tag's claimed version actually matches the packed artifact's real version --
# e.g. a `dotnet-v1.2.3` tag could point at a main-branch commit whose packed .nuspec is really `0.1.0-preview.1`.
# Main-only manual dispatch closes this gap entirely rather than requiring a second, parallel version-match check.
Assert-NotEligible "workflow_dispatch" "refs/tags/dotnet-v1.2.3" "workflow_dispatch targeting a validly-formed release tag (tag/package-version mismatch regression -- manual dispatch is main-only)"
Assert-NotEligible "workflow_dispatch" "refs/tags/dotnet-v0.1.0-preview.1" "workflow_dispatch targeting a validly-formed pre-release-shaped tag (still rejected -- manual dispatch is main-only)"
Assert-NotEligible "workflow_dispatch" "refs/tags/v1.2.3" "workflow_dispatch targeting a tag missing the required 'dotnet-v' prefix"
Assert-NotEligible "workflow_dispatch" "refs/tags/dotnet-v1.2.3`$(rm -rf /)" "workflow_dispatch targeting a tag-shaped ref embedding a `$() subexpression"
Assert-NotEligible "workflow_dispatch" 'refs/tags/dotnet-v1.2.3"; rm -rf /' "workflow_dispatch targeting a tag-shaped ref embedding a quote and semicolon"
Assert-NotEligible "workflow_dispatch" "refs/tags/dotnet-v1.2.3 extra" "workflow_dispatch targeting a tag-shaped ref with a trailing space and extra token"

# ---------------------------------------------------------------------------------------------------------------
# Case 7: every other event is never eligible, regardless of ref.
# ---------------------------------------------------------------------------------------------------------------
Assert-NotEligible "pull_request" "refs/heads/main" "pull_request event is never eligible, even against 'main'"
Assert-NotEligible "pull_request" "refs/tags/dotnet-v1.2.3" "pull_request event is never eligible, even against a valid release tag"
Assert-NotEligible "schedule" "refs/heads/main" "An unrecognized/other event name is never eligible"
Assert-NotEligible "" "refs/heads/main" "An empty event name is never eligible"

# ---------------------------------------------------------------------------------------------------------------
# Case 8: case sensitivity -- git refs are case-sensitive; only the exact 'refs/heads/main' qualifies.
# ---------------------------------------------------------------------------------------------------------------
Assert-NotEligible "workflow_dispatch" "refs/heads/MAIN" "workflow_dispatch targeting 'refs/heads/MAIN' (wrong case) is not eligible"
Assert-NotEligible "workflow_dispatch" "REFS/HEADS/main" "workflow_dispatch targeting 'REFS/HEADS/main' (wrong case) is not eligible"
Assert-NotEligible "PUSH" "refs/tags/dotnet-v1.2.3" "An event name compared with the wrong case ('PUSH') is not eligible (exact string match required)"

# ---------------------------------------------------------------------------------------------------------------
Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All attestation-ref-eligibility self-test assertions PASSED." -ForegroundColor Green
exit 0
