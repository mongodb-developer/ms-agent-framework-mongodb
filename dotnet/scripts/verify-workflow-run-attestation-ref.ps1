#Requires -Version 7.0
<#
.SYNOPSIS
    Fails (exit 1) unless a `workflow_run`-triggered privileged attestation run's UPSTREAM event/ref/commit is
    genuinely eligible to reach `dotnet-release-attestation.yml`'s provenance-attestation job.

.DESCRIPTION
    Thin CLI wrapper combining ReleaseVersionTag.ps1's Resolve-WorkflowRunAttestationRef (reconstructs a
    candidate full ref from the workflow_run payload's bare `head_branch`/`event` fields) and
    Test-AttestationRefEligible (the same ref-shape gate `dotnet-sbom-provenance.yml`'s original,
    now-decommissioned in-workflow validator used) -- see both functions' doc comments for the full
    trust-boundary rationale (why the privileged workflow reacts to `workflow_run` instead of
    `push`/`workflow_dispatch`/`release` directly).

    GitHub's `workflow_run` webhook payload never distinguishes "a tag named X" from "a branch named X" by the
    bare `head_branch` field alone. Before trusting a `push`-reconstructed `refs/tags/<name>` candidate, this
    script independently confirms -- using ONLY this trusted checkout's real git history at -RepositoryRoot,
    never any code from the upstream/selected ref -- that a real tag by that exact name exists and dereferences
    to EXACTLY the claimed `-UpstreamHeadSha` commit. This closes two distinct gaps a name-only check would
    leave open: (1) a `head_branch` that is actually a BRANCH name (e.g. "main") rather than a real tag, which
    would otherwise be silently treated as if it were a tag push; and (2) a real tag by that name existing but
    pointing at a DIFFERENT commit than the one the upstream run claims to have built.

    -UpstreamEventName/-UpstreamHeadBranch/-UpstreamHeadSha MUST be passed as proper PowerShell parameter values
    (e.g. from step-level `env:` values sourced from `github.event.workflow_run.*`), NEVER by interpolating those
    expressions directly into a `run: |` script's source text -- see verify-release-tag.ps1's identical
    rationale (a branch/tag name is not restricted to shell-safe characters by git itself).

.PARAMETER UpstreamEventName
    `github.event.workflow_run.event` -- the UPSTREAM (`sbom`-only workflow) run's own triggering event name.

.PARAMETER UpstreamHeadBranch
    `github.event.workflow_run.head_branch` -- the upstream run's bare branch/tag short name.

.PARAMETER UpstreamHeadSha
    `github.event.workflow_run.head_sha` -- the exact commit the upstream run built.

.PARAMETER RepositoryRoot
    Path to a git working tree with full history/tags available (this workflow's own trusted checkout, always
    sourced from the default branch since the enclosing workflow's only trigger is `workflow_run`) -- used only
    to confirm a claimed tag candidate genuinely resolves to -UpstreamHeadSha, never to execute any code from it.

.EXAMPLE
    pwsh dotnet/scripts/verify-workflow-run-attestation-ref.ps1 -UpstreamEventName push -UpstreamHeadBranch "dotnet-v1.2.3" -UpstreamHeadSha <sha> -RepositoryRoot .

.EXAMPLE
    pwsh dotnet/scripts/verify-workflow-run-attestation-ref.ps1 -UpstreamEventName workflow_dispatch -UpstreamHeadBranch "main" -UpstreamHeadSha <sha> -RepositoryRoot .
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][AllowEmptyString()][string]$UpstreamEventName,
    [Parameter(Mandatory)][AllowEmptyString()][string]$UpstreamHeadBranch,
    [Parameter(Mandatory)][AllowEmptyString()][string]$UpstreamHeadSha,
    [Parameter(Mandatory)][string]$RepositoryRoot
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ReleaseVersionTag.ps1")

$candidateRef = Resolve-WorkflowRunAttestationRef -UpstreamEventName $UpstreamEventName -UpstreamHeadBranch $UpstreamHeadBranch

if ($null -eq $candidateRef) {
    Write-Host "[FAIL] upstream event '$UpstreamEventName' never produces an attestation-eligible ref candidate (only 'push' or 'workflow_dispatch' do) -- refusing to attest" -ForegroundColor Red
    exit 1
}

if ($UpstreamEventName -ceq 'push') {
    # git itself forbids '..', '~', '^', ':', '?', '*', '[', '@{', and consecutive slashes inside a single ref
    # name component (see `git check-ref-format`), so $UpstreamHeadBranch -- a name that had to already exist as
    # a real git ref for GitHub to report it here -- cannot smuggle git revision-syntax metacharacters into
    # $candidateRef even though it is passed as a plain string argument, not shell text.
    $resolvedSha = & git -C $RepositoryRoot rev-parse --verify "$candidateRef^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resolvedSha)) {
        Write-Host "[FAIL] '$candidateRef' does not resolve to a real tag in this repository -- refusing to attest for an unverifiable upstream ref (head_branch may name a branch, not a tag)" -ForegroundColor Red
        exit 1
    }

    if ($resolvedSha -cne $UpstreamHeadSha) {
        Write-Host "[FAIL] tag '$candidateRef' resolves to commit '$resolvedSha', which does NOT match the upstream run's claimed head_sha '$UpstreamHeadSha' -- refusing to attest a mismatched commit" -ForegroundColor Red
        exit 1
    }

    Write-Host "[ OK ] '$candidateRef' genuinely resolves to the claimed commit '$UpstreamHeadSha' in this repository's real history"
}

$result = Test-AttestationRefEligible -EventName $UpstreamEventName -Ref $candidateRef

if (-not $result.Eligible) {
    Write-Host "[FAIL] $($result.Reason)" -ForegroundColor Red
    exit 1
}

Write-Host "[ OK ] $($result.Reason)" -ForegroundColor Green
Write-Host "Resolved and verified attestation ref: $candidateRef"
exit 0
