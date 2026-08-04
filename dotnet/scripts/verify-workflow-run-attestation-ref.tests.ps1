#Requires -Version 7.0
<#
.SYNOPSIS
    Integration self-test for verify-workflow-run-attestation-ref.ps1 against a REAL scratch git repository --
    proves the tag-genuinely-resolves-to-the-claimed-commit check is a meaningful, non-vacuous guard, not merely
    a string comparison.

.DESCRIPTION
    Resolve-WorkflowRunAttestationRef alone cannot tell "a tag named X" from "a branch named X" from GitHub's
    `workflow_run` payload's bare `head_branch` field -- verify-workflow-run-attestation-ref.ps1 closes that gap
    by independently resolving the reconstructed `refs/tags/<name>` candidate against this repository's own real
    git history. This test creates a disposable scratch git repository under dotnet/artifacts (never /tmp, per
    this repository's engineering constraints) with a real commit and a real annotated tag, then invokes the
    actual CLI script as a subprocess (never dot-sourcing it, so this test genuinely exercises argument parsing,
    exit codes, and the real `git rev-parse` subprocess call) for each of:

      1. push + a real tag name + the tag's real commit SHA -> eligible (exit 0).
      2. push + a real tag name + a DIFFERENT (wrong) commit SHA -- the "upstream run claims a commit the tag
         does not actually point at" mismatch -> rejected (exit 1).
      3. push + a name that is NOT a real tag at all (no such ref exists) -> rejected (exit 1).
      4. push + "main" (a real BRANCH, not a tag, in this scratch repo) -> rejected (exit 1) -- the core
         branch/tag name-collision guard this script exists for; a name-only check would have wrongly accepted
         this as if "main" were a release tag.
      5. workflow_dispatch + "main" -> eligible (exit 0) -- no git resolution needed for this path; string
         equality against Test-AttestationRefEligible's own 'refs/heads/main' requirement is sufficient, since
         the workflow's separate ancestry check independently verifies the actual commit.
      6. workflow_dispatch + an arbitrary non-main branch -> rejected (exit 1).
      7. pull_request upstream event -> rejected (exit 1), regardless of head_branch/head_sha (the fork-PR
         scenario: Resolve-WorkflowRunAttestationRef itself already returns no candidate for this event).

    Run directly: pwsh dotnet/scripts/verify-workflow-run-attestation-ref.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

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

$scriptPath = Join-Path $PSScriptRoot "verify-workflow-run-attestation-ref.ps1"
$scratchDir = Join-Path $PSScriptRoot "../artifacts/workflow-run-attestation-ref-test-fixtures"

if (Test-Path $scratchDir) {
    Remove-Item -Path $scratchDir -Recurse -Force
}
New-Item -ItemType Directory -Path $scratchDir -Force | Out-Null

try {
    # --- Build a real, disposable scratch git repository with a genuine commit and a genuine annotated tag ----
    & git -C $scratchDir init --quiet --initial-branch=main
    & git -C $scratchDir config user.email "test@example.invalid"
    & git -C $scratchDir config user.name "Workflow Run Attestation Ref Test"
    Set-Content -Path (Join-Path $scratchDir "file.txt") -Value "fixture content"
    & git -C $scratchDir add file.txt
    & git -C $scratchDir commit --quiet -m "Fixture commit"
    $realTagSha = (& git -C $scratchDir rev-parse HEAD).Trim()
    & git -C $scratchDir tag -a "dotnet-v9.9.9-fixture" -m "Fixture release tag"

    # A second, different commit -- used as the "wrong claimed sha" for the mismatch case below.
    Set-Content -Path (Join-Path $scratchDir "file.txt") -Value "different content"
    & git -C $scratchDir add file.txt
    & git -C $scratchDir commit --quiet -m "Second fixture commit"
    $differentSha = (& git -C $scratchDir rev-parse HEAD).Trim()

    Assert-True (-not [string]::IsNullOrWhiteSpace($realTagSha)) "Scratch repository's first fixture commit has a real SHA"
    Assert-True (-not [string]::IsNullOrWhiteSpace($differentSha)) "Scratch repository's second fixture commit has a real, different SHA"
    Assert-True ($realTagSha -cne $differentSha) "The two fixture commits are genuinely different commits"

    function Invoke-VerifyWorkflowRunRef([string]$EventName, [string]$HeadBranch, [string]$HeadSha) {
        & pwsh -NoProfile -File $scriptPath `
            -UpstreamEventName $EventName `
            -UpstreamHeadBranch $HeadBranch `
            -UpstreamHeadSha $HeadSha `
            -RepositoryRoot $scratchDir | Out-Null
        return $LASTEXITCODE
    }

    # --- Case 1: push + a real tag name + the tag's real commit SHA -> eligible -------------------------------
    $exit1 = Invoke-VerifyWorkflowRunRef -EventName "push" -HeadBranch "dotnet-v9.9.9-fixture" -HeadSha $realTagSha
    Assert-True ($exit1 -eq 0) "push + real tag 'dotnet-v9.9.9-fixture' + its own real commit SHA -> eligible (exit 0, got $exit1)"

    # --- Case 2: push + a real tag name + a DIFFERENT (wrong) commit SHA -> rejected --------------------------
    $exit2 = Invoke-VerifyWorkflowRunRef -EventName "push" -HeadBranch "dotnet-v9.9.9-fixture" -HeadSha $differentSha
    Assert-True ($exit2 -ne 0) "push + real tag 'dotnet-v9.9.9-fixture' + a DIFFERENT claimed commit SHA -> rejected (the upstream-run-claims-a-commit-the-tag-does-not-point-at mismatch, got exit $exit2)"

    # --- Case 3: push + a name that is not a real tag at all -> rejected --------------------------------------
    $exit3 = Invoke-VerifyWorkflowRunRef -EventName "push" -HeadBranch "dotnet-v0.0.0-does-not-exist" -HeadSha $realTagSha
    Assert-True ($exit3 -ne 0) "push + a tag name with no matching real tag in the repository -> rejected (got exit $exit3)"

    # --- Case 4: push + 'main' (a real BRANCH, not a tag) -> rejected (the core name-collision guard) ---------
    $exit4 = Invoke-VerifyWorkflowRunRef -EventName "push" -HeadBranch "main" -HeadSha $differentSha
    Assert-True ($exit4 -ne 0) "push + head_branch 'main' (a real BRANCH in this scratch repo, not a tag) -> rejected -- a name-only check would have wrongly accepted this as a release-tag push (got exit $exit4)"

    # --- Case 5: workflow_dispatch + 'main' -> eligible (no git resolution needed for this path) --------------
    $exit5 = Invoke-VerifyWorkflowRunRef -EventName "workflow_dispatch" -HeadBranch "main" -HeadSha $differentSha
    Assert-True ($exit5 -eq 0) "workflow_dispatch + head_branch 'main' -> eligible (got exit $exit5)"

    # --- Case 6: workflow_dispatch + an arbitrary non-main branch -> rejected ---------------------------------
    $exit6 = Invoke-VerifyWorkflowRunRef -EventName "workflow_dispatch" -HeadBranch "feature/some-topic-branch" -HeadSha $differentSha
    Assert-True ($exit6 -ne 0) "workflow_dispatch + an arbitrary non-main branch -> rejected (got exit $exit6)"

    # --- Case 7: pull_request upstream event -> rejected regardless of head_branch/head_sha ------------------
    $exit7 = Invoke-VerifyWorkflowRunRef -EventName "pull_request" -HeadBranch "main" -HeadSha $realTagSha
    Assert-True ($exit7 -ne 0) "pull_request upstream event (the fork-PR scenario) -> rejected regardless of head_branch/head_sha (got exit $exit7)"
}
finally {
    if (Test-Path $scratchDir) {
        Remove-Item -Path $scratchDir -Recurse -Force
    }
}

Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All verify-workflow-run-attestation-ref integration self-test assertions PASSED." -ForegroundColor Green
exit 0
