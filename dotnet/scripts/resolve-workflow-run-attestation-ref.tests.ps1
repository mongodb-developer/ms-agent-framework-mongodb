#Requires -Version 7.0
<#
.SYNOPSIS
    Self-test for ReleaseVersionTag.ps1's Resolve-WorkflowRunAttestationRef -- the pure, no-IO function that
    reconstructs a candidate full ref from a `workflow_run` event payload's bare `head_branch`/`event` fields.

.DESCRIPTION
    Proves: a `push` upstream event reconstructs `refs/tags/<head_branch>`; a `workflow_dispatch` upstream event
    reconstructs `refs/heads/<head_branch>`; every other upstream event (including `pull_request` -- the fork-PR
    scenario a malicious contributor could otherwise exploit to have their successful, credential-free `sbom` run
    trigger `workflow_run`) produces `$null`, which the caller (verify-workflow-run-attestation-ref.ps1) must
    treat as "never eligible" -- the same catch-all Test-AttestationRefEligible itself already applies for its
    own `-EventName` parameter. Case-sensitivity matches Test-AttestationRefEligible's own `-ceq` throughout.

    Run directly: pwsh dotnet/scripts/resolve-workflow-run-attestation-ref.tests.ps1
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

# ---------------------------------------------------------------------------------------------------------------
# push -> refs/tags/<head_branch>
# ---------------------------------------------------------------------------------------------------------------
$pushResult = Resolve-WorkflowRunAttestationRef -UpstreamEventName "push" -UpstreamHeadBranch "dotnet-v1.2.3"
Assert-True ($pushResult -ceq "refs/tags/dotnet-v1.2.3") "push upstream event reconstructs 'refs/tags/dotnet-v1.2.3' (got '$pushResult')"

$pushMainResult = Resolve-WorkflowRunAttestationRef -UpstreamEventName "push" -UpstreamHeadBranch "main"
Assert-True ($pushMainResult -ceq "refs/tags/main") "push upstream event with head_branch 'main' reconstructs 'refs/tags/main' -- a candidate the caller's real-tag-resolution check must then reject, since no tag named 'main' should ever exist (got '$pushMainResult')"

# ---------------------------------------------------------------------------------------------------------------
# workflow_dispatch -> refs/heads/<head_branch>
# ---------------------------------------------------------------------------------------------------------------
$dispatchResult = Resolve-WorkflowRunAttestationRef -UpstreamEventName "workflow_dispatch" -UpstreamHeadBranch "main"
Assert-True ($dispatchResult -ceq "refs/heads/main") "workflow_dispatch upstream event reconstructs 'refs/heads/main' (got '$dispatchResult')"

$dispatchTagResult = Resolve-WorkflowRunAttestationRef -UpstreamEventName "workflow_dispatch" -UpstreamHeadBranch "dotnet-v1.2.3"
Assert-True ($dispatchTagResult -ceq "refs/tags/dotnet-v1.2.3") "coordinator workflow_dispatch against an immutable release tag reconstructs the tag ref (got '$dispatchTagResult')"

$dispatchFeatureResult = Resolve-WorkflowRunAttestationRef -UpstreamEventName "workflow_dispatch" -UpstreamHeadBranch "feature/some-topic-branch"
Assert-True ($dispatchFeatureResult -ceq "refs/heads/feature/some-topic-branch") "workflow_dispatch upstream event reconstructs the full ref for an arbitrary branch too -- rejection of non-main branches is Test-AttestationRefEligible's job, not this function's (got '$dispatchFeatureResult')"

# ---------------------------------------------------------------------------------------------------------------
# Every other upstream event -> $null (the fork pull_request scenario, and any unrecognized event)
# ---------------------------------------------------------------------------------------------------------------
$pullRequestResult = Resolve-WorkflowRunAttestationRef -UpstreamEventName "pull_request" -UpstreamHeadBranch "main"
Assert-True ($null -eq $pullRequestResult) "pull_request upstream event (including a fork PR's successful sbom run) produces no ref candidate at all -- never eligible, regardless of head_branch (got '$pullRequestResult')"

$scheduleResult = Resolve-WorkflowRunAttestationRef -UpstreamEventName "schedule" -UpstreamHeadBranch "main"
Assert-True ($null -eq $scheduleResult) "An unrecognized/other upstream event name produces no ref candidate (got '$scheduleResult')"

$emptyEventResult = Resolve-WorkflowRunAttestationRef -UpstreamEventName "" -UpstreamHeadBranch "main"
Assert-True ($null -eq $emptyEventResult) "An empty upstream event name produces no ref candidate (got '$emptyEventResult')"

# ---------------------------------------------------------------------------------------------------------------
# Case sensitivity: Test-AttestationRefEligible itself uses -ceq/-cmatch throughout, so this function's own
# event-name comparison must match that exactness rather than silently normalizing case.
# ---------------------------------------------------------------------------------------------------------------
$wrongCaseResult = Resolve-WorkflowRunAttestationRef -UpstreamEventName "PUSH" -UpstreamHeadBranch "dotnet-v1.2.3"
Assert-True ($null -eq $wrongCaseResult) "An upstream event name compared with the wrong case ('PUSH') produces no ref candidate (exact-case match required, got '$wrongCaseResult')"

# ---------------------------------------------------------------------------------------------------------------
Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All Resolve-WorkflowRunAttestationRef self-test assertions PASSED." -ForegroundColor Green
exit 0
