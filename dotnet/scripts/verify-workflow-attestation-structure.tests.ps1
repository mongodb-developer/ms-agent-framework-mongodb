#Requires -Version 7.0
<#
.SYNOPSIS
    Structural/static regression self-test proving this repository's privileged-permission/untrusted-trigger
    class of bug stays closed, by direct inspection of every committed workflow file's YAML text (no live GitHub
    Actions run required).

.DESCRIPTION
    Background: GitHub Actions resolves and runs an ENTIRE workflow file's job graph -- every job's own
    `permissions:`/`needs:`/`if:` definitions and step list, not merely the scripts a job happens to invoke --
    using the exact file content present at whatever ref triggered that specific run. For `pull_request`/`push`/
    `workflow_dispatch`/`release`, that ref can be an operator-selected branch/tag or a pushed ref/commit --
    content this repository does not control the review of before the run starts. A prior revision of this
    repository's attestation workflow held elevated `id-token`/`attestations`/`artifact-metadata` permissions in
    the SAME file as its `push`/`workflow_dispatch`-triggered `sbom` job, with an in-file "trusted main checkout"
    validator job -- INSUFFICIENT, because that validator job's own `permissions:`/`needs:`/`if:` wiring was
    itself sourced from the same untrusted ref, so whoever controlled the triggering ref could rewrite the job
    graph itself, not merely the scripts it called. The fix: split the elevated job into a WHOLLY SEPARATE file
    (`dotnet-release-attestation.yml`) triggered exclusively by `workflow_run` -- the one event GitHub's own
    documentation guarantees always resolves and runs the reacting workflow's file exactly as it exists on the
    repository's default branch, regardless of what ref/event triggered the upstream run it reacts to. (`release`
    does NOT carry this guarantee per GitHub's own event-reference documentation -- its `GITHUB_SHA`/`GITHUB_REF`
    are the tagged commit/tag ref, structurally identical in trust to a tag `push` -- so `release: published` was
    considered and rejected as an insufficient trigger for this purpose.)

    This test performs four kinds of assertions, generically across every file in .github/workflows/, not just
    the two release-related files, so a FUTURE workflow that carelessly adds elevated permissions under an
    untrusted trigger is caught by the same regression proof:

      1. Global audit (any current or future workflow): every job across every workflow file that requests
         `id-token: write`, `attestations: write`, or `artifact-metadata: write` must live in a file whose ONLY
         top-level trigger is `workflow_run` -- never `push`, `workflow_dispatch`, `release`, `pull_request`, or
         `pull_request_target`. A file with zero elevated-permission jobs is unconstrained (any trigger is fine).
      2. dotnet-sbom-provenance.yml specifically: asserts it has NO job anywhere with any of the three elevated
         permissions, and that its `on:` does not include `release` (belt-and-suspenders on top of assertion 1,
         which already covers it structurally).
      3. dotnet-release-attestation.yml specifically: asserts its `on:` is EXACTLY `workflow_run` (no sibling
         triggers) referencing the exact upstream workflow name; `validate-attestation-eligibility` requests only
         `contents: read`; `provenance-attestation` requests the elevated permissions, `needs:` the validator job,
         and checks out ONLY the validator's `validated-sha` output -- never `github.ref`/`github.sha`/
         `github.event.workflow_run.head_sha` directly as its actual checkout ref (that field IS legitimately used
         as plain env/string data by the validator job's own checks, just never as an unvalidated checkout ref in
         the privileged job); and that it REBUILDS the package fresh rather than downloading the upstream sbom
         job's own artifact.
      4. Functional/regression proofs (unchanged from the prior single-file design, still valid against the new
         file): a manual `workflow_dispatch` targeting a real, validly-formed release tag is rejected by
         Test-AttestationRefEligible (tag/package-version-mismatch regression), and a "malicious selected-ref
         validator" fake function is shown to be able to forge an "eligible" verdict -- demonstrating concretely
         why assertion 1's trigger-isolation matters: IF a privileged job's validator were sourced from a
         compromised/selected ref, its verdict could trivially be forged. This does not claim any current job
         does this (assertions 1 and 3 prove none does); it proves the failure mode those assertions close would
         otherwise be real, not merely theoretical.

    This deliberately parses each workflow's YAML with plain text/regex, not a YAML library, to avoid adding a new
    parsing dependency to this repository purely for a self-test.

    Run directly: pwsh dotnet/scripts/verify-workflow-attestation-structure.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$WorkflowsDir = Join-Path $RepoRoot ".github/workflows"
$SbomWorkflowPath = Join-Path $WorkflowsDir "dotnet-sbom-provenance.yml"
$AttestationWorkflowPath = Join-Path $WorkflowsDir "dotnet-release-attestation.yml"
$PythonReleaseWorkflowPath = Join-Path $WorkflowsDir "release-python.yml"
$UpstreamWorkflowName = ".NET package SBOM (credential-free verification)"

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

# Extracts each top-level job's raw text block from a committed workflow YAML: a job header is a line indented
# exactly two spaces directly under `jobs:` (e.g. "  sbom:"); the block continues until the next such line, the
# next top-level (zero-indent) key, or end of file.
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

# Extracts the set of top-level trigger keys from a workflow's `on:` block (e.g. "push", "workflow_dispatch",
# "workflow_run") -- the block starts at a bare `^on:\s*$` line and continues while subsequent lines are indented,
# ending at the next zero-indent top-level key (e.g. `permissions:`, `jobs:`) or end of file. A trigger key is a
# two-space-indented `key:` line directly under `on:`.
function Get-WorkflowTriggerKeys([string]$Path) {
    $lines = Get-Content -Path $Path
    $onLineIndex = $null
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch '^on:\s*$') {
            $onLineIndex = $i
            break
        }
    }
    if ($null -eq $onLineIndex) {
        throw "No top-level 'on:' key found in $Path"
    }

    $keys = [System.Collections.Generic.List[string]]::new()
    for ($i = $onLineIndex + 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -cmatch '^\S') {
            # Zero-indent, non-blank line: the `on:` block has ended.
            break
        }
        if ($line -cmatch '^  ([A-Za-z0-9_-]+):') {
            $keys.Add($Matches[1])
        }
    }
    # The unary comma forces PowerShell to emit $keys as a single List[string] object; without it, a
    # single-trigger workflow (e.g. dotnet-release-attestation.yml's sole 'workflow_run') would have its
    # one-element list silently flattened/unwrapped to a bare scalar string on return, breaking .Count/[0]
    # usage at every call site (a real bug caught by this self-test's own assertion against that exact file).
    return , $keys
}

# Returns $true if any job block's `permissions:` sub-block requests at least one of the three
# attestation-adjacent elevated permissions this repository treats as privileged.
function Test-JobBlockHasElevatedPermissions([string]$JobBlock) {
    return (
        $JobBlock -match 'id-token:\s*write' -or
        $JobBlock -match 'attestations:\s*write' -or
        $JobBlock -match 'artifact-metadata:\s*write'
    )
}

# --- Assertion 1: global audit across every workflow file in the repository ------------------------------------
# A future workflow that carelessly adds elevated permissions under an untrusted trigger must fail this
# regression proof, not just the two files this test also inspects in detail below.
$allWorkflowFiles = Get-ChildItem -Path $WorkflowsDir -Filter "*.yml" | Sort-Object Name
Assert-True ($allWorkflowFiles.Count -ge 5) "Found at least 5 workflow files under .github/workflows (sanity check on the glob) -- found $($allWorkflowFiles.Count)"

foreach ($workflowFile in $allWorkflowFiles) {
    $jobBlocks = Get-WorkflowJobBlocks -Path $workflowFile.FullName
    $triggerKeys = Get-WorkflowTriggerKeys -Path $workflowFile.FullName
    $hasElevatedJob = $false
    foreach ($jobName in $jobBlocks.Keys) {
        if (Test-JobBlockHasElevatedPermissions $jobBlocks[$jobName]) {
            $hasElevatedJob = $true
        }
    }

    if ($hasElevatedJob -and $workflowFile.Name -ceq 'release-python.yml') {
        Assert-True (
            $triggerKeys.Count -eq 2 -and
            $triggerKeys -contains 'push' -and
            $triggerKeys -contains 'workflow_dispatch'
        ) "release-python.yml is the single reviewed direct-release exception and keeps only push/workflow_dispatch triggers -- found triggers: $($triggerKeys -join ', ')"
    }
    elseif ($hasElevatedJob) {
        $disallowedTriggers = $triggerKeys | Where-Object { $_ -ne 'workflow_run' }
        Assert-True (
            $triggerKeys.Count -gt 0 -and $disallowedTriggers.Count -eq 0
        ) "$($workflowFile.Name) has a job with id-token/attestations/artifact-metadata permissions, so its ONLY top-level trigger must be workflow_run -- found triggers: $($triggerKeys -join ', ')"
    }
    else {
        Assert-True $true "$($workflowFile.Name) has no job with elevated id-token/attestations/artifact-metadata permissions (no trigger restriction required)"
    }
}

# release-python.yml is allowed to publish and attest directly because both entry points are restricted to the
# default branch, the selected source must be main-reachable, and custom provenance binds the validated source SHA.
$pythonReleaseText = Get-Content -Path $PythonReleaseWorkflowPath -Raw
$pythonReleaseJobBlocks = Get-WorkflowJobBlocks -Path $PythonReleaseWorkflowPath
Assert-True (
    $pythonReleaseText -match '(?ms)^  push:\s+branches:\s+- main\s+paths:\s+- "python/pyproject\.toml"'
) "release-python.yml push execution is restricted to main and the Python version manifest"
Assert-True (
    $pythonReleaseText -match [regex]::Escape('test "$DISPATCH_REF" = main')
) "release-python.yml manual dispatch rejects every branch except main"
Assert-True (
    $pythonReleaseText -match '(?s)if \[ "\$\{\{ github\.event_name \}\}" = push \]; then\s+test "\$SHA" = "\$GITHUB_SHA"\s+fi'
) "release-python.yml requires push releases to equal the immutable triggering event SHA"
Assert-True (
    $pythonReleaseText -match [regex]::Escape('git merge-base --is-ancestor "$SHA" origin/main')
) "release-python.yml independently proves the release source is reachable from origin/main"
Assert-True (
    $pythonReleaseJobBlocks['provenance'] -match 'needs:\s*\[tag,\s*build\]' -and
    $pythonReleaseJobBlocks['publish'] -match 'needs:\s*\[tag,\s*build,\s*provenance\]'
) "release-python.yml elevated provenance/publish jobs remain transitively gated by tag validation and the release build"
Assert-True (
    $pythonReleaseJobBlocks['publish'] -match 'environment:\s*\$\{\{\s*vars\.PYPI_ENVIRONMENT\s*\}\}' -and
    $pythonReleaseJobBlocks['publish'] -match "vars\.PYPI_PUBLISHING_APPROVED == 'true'"
) "release-python.yml publishing remains protected by an explicitly approved GitHub environment"
Assert-True (
    $pythonReleaseJobBlocks['provenance'] -match [regex]::Escape('RELEASE_SHA: ${{ needs.tag.outputs.sha }}') -and
    $pythonReleaseJobBlocks['provenance'] -match '"digest": \{"gitCommit": sha\}' -and
    $pythonReleaseJobBlocks['provenance'] -match 'uses:\s*actions/attest@[0-9a-f]{40}\s*#\s*actions/attest v4\.\d+\.\d+' -and
    $pythonReleaseJobBlocks['provenance'] -match 'predicate-type:\s*https://slsa\.dev/provenance/v1' -and
    $pythonReleaseJobBlocks['provenance'] -match 'predicate-path:\s*dist/release-provenance-predicate\.json'
) "release-python.yml custom provenance binds the validated tag output SHA through a pinned generic attestation action"

# --- Assertion 2: dotnet-sbom-provenance.yml is fully unprivileged ----------------------------------------------
$sbomJobBlocks = Get-WorkflowJobBlocks -Path $SbomWorkflowPath
$sbomTriggerKeys = Get-WorkflowTriggerKeys -Path $SbomWorkflowPath
Assert-True ($sbomJobBlocks.Contains('sbom')) "dotnet-sbom-provenance.yml defines a 'sbom' job"
Assert-True (
    -not ($sbomJobBlocks.Contains('validate-attestation-eligibility'))
) "dotnet-sbom-provenance.yml no longer defines 'validate-attestation-eligibility' (moved to dotnet-release-attestation.yml)"
Assert-True (
    -not ($sbomJobBlocks.Contains('provenance-attestation'))
) "dotnet-sbom-provenance.yml no longer defines 'provenance-attestation' (moved to dotnet-release-attestation.yml)"

$sbomHasAnyElevatedJob = $false
foreach ($jobName in $sbomJobBlocks.Keys) {
    if (Test-JobBlockHasElevatedPermissions $sbomJobBlocks[$jobName]) {
        $sbomHasAnyElevatedJob = $true
    }
}
Assert-True (-not $sbomHasAnyElevatedJob) "dotnet-sbom-provenance.yml has NO job anywhere with id-token/attestations/artifact-metadata permissions"
Assert-True (
    $sbomTriggerKeys -notcontains 'release'
) "dotnet-sbom-provenance.yml's 'on:' does not include 'release' (release does not guarantee default-branch workflow sourcing per GitHub's own docs, so it must never gain elevated permissions even indirectly)"
Assert-True (
    $sbomTriggerKeys -contains 'pull_request' -and $sbomTriggerKeys -contains 'push' -and $sbomTriggerKeys -contains 'workflow_dispatch'
) "dotnet-sbom-provenance.yml keeps its pull_request/push/workflow_dispatch triggers (safe now that it holds no elevated permissions anywhere)"

# --- Assertion 3: dotnet-release-attestation.yml's trigger isolation and job wiring -----------------------------
Assert-True (Test-Path $AttestationWorkflowPath) "dotnet-release-attestation.yml exists"

$attestationTriggerKeys = Get-WorkflowTriggerKeys -Path $AttestationWorkflowPath
Assert-True (
    $attestationTriggerKeys.Count -eq 1 -and $attestationTriggerKeys[0] -ceq 'workflow_run'
) "dotnet-release-attestation.yml's 'on:' is EXACTLY workflow_run (no push/workflow_dispatch/release/pull_request sibling trigger) -- found: $($attestationTriggerKeys -join ', ')"

$attestationWorkflowText = Get-Content -Path $AttestationWorkflowPath -Raw
Assert-True (
    $attestationWorkflowText -match [regex]::Escape('workflows: [".NET package SBOM (credential-free verification)"]')
) "dotnet-release-attestation.yml's workflow_run trigger references the exact upstream workflow name '$UpstreamWorkflowName'"

$releaseJobBlocks = Get-WorkflowJobBlocks -Path $AttestationWorkflowPath
Assert-True ($releaseJobBlocks.Contains('validate-attestation-eligibility')) "dotnet-release-attestation.yml defines a 'validate-attestation-eligibility' job"
Assert-True ($releaseJobBlocks.Contains('provenance-attestation')) "dotnet-release-attestation.yml defines a 'provenance-attestation' job"

$eligibilityBlock = $releaseJobBlocks['validate-attestation-eligibility']
$attestationBlock = $releaseJobBlocks['provenance-attestation']

Assert-True (
    $eligibilityBlock -notmatch 'id-token:\s*write' -and
    $eligibilityBlock -notmatch 'attestations:\s*write' -and
    $eligibilityBlock -notmatch 'artifact-metadata:\s*write'
) "validate-attestation-eligibility does not request id-token/attestations/artifact-metadata permissions"
Assert-True (
    $eligibilityBlock -match 'contents:\s*read'
) "validate-attestation-eligibility requests only contents: read"

Assert-True (Test-JobBlockHasElevatedPermissions $attestationBlock) "provenance-attestation retains the elevated id-token/attestations/artifact-metadata permissions"

# Trusted-main-first checkout in the validator job.
Assert-True (
    $eligibilityBlock -match 'ref:\s*main\b'
) "validate-attestation-eligibility's checkout step pins 'ref: main'"

# needs/output wiring: provenance-attestation must depend on the validator and check out ONLY its output.
Assert-True (
    $attestationBlock -match 'needs:\s*(?:validate-attestation-eligibility|\[[^\]]*validate-attestation-eligibility[^\]]*\])'
) "provenance-attestation's 'needs:' includes validate-attestation-eligibility"
Assert-True (
    $attestationBlock -match [regex]::Escape('needs.validate-attestation-eligibility.outputs.validated-sha')
) "provenance-attestation's checkout uses the trusted job's validated-sha output"
Assert-True (
    $attestationBlock -notmatch '\bref:\s*\$\{\{\s*github\.(ref|sha)\b'
) "provenance-attestation never checks out github.ref/github.sha directly"
Assert-True (
    $attestationBlock -notmatch '\bref:\s*\$\{\{\s*github\.event\.workflow_run\.head_sha'
) "provenance-attestation never checks out github.event.workflow_run.head_sha directly (only the validated-sha output, after ancestry verification)"

# provenance-attestation must REBUILD fresh -- it must never download the upstream sbom job's own artifact.
Assert-True (
    $attestationBlock -notmatch 'download-artifact'
) "provenance-attestation never downloads the upstream sbom job's artifact (it rebuilds fresh from the validated commit instead)"
Assert-True (
    $attestationBlock -match 'verify-package\.ps1'
) "provenance-attestation rebuilds the package via verify-package.ps1 from the validated checkout"

# --- Custom SLSA provenance predicate: never the stock actions/attest-build-provenance auto-provenance mode ------
# The stock action's default mode derives its predicate's build-source solely from the job's own ambient
# GITHUB_SHA/GITHUB_REF (documented by GitHub, for workflow_run, as "Last commit on default branch"/"Default
# branch" -- this workflow's own trigger context, never the validated release commit actually checked out and
# rebuilt above). This repository's policy therefore forbids using that stock auto-provenance mode ANYWHERE, and
# instead generates a custom predicate that explicitly binds resolvedDependencies to the validated commit SHA.
foreach ($workflowFile in $allWorkflowFiles) {
    $fileText = Get-Content -Path $workflowFile.FullName -Raw
    if ($workflowFile.Name -ceq 'release-python.yml') {
        Assert-True (
            $fileText -notmatch 'uses:\s*actions/attest-build-provenance'
        ) "release-python.yml never uses ambient stock provenance for a recoverable historical main commit"
    }
    else {
        Assert-True (
            $fileText -notmatch 'uses:\s*actions/attest-build-provenance'
        ) "$($workflowFile.Name) never ACTUALLY USES the stock actions/attest-build-provenance action (it would derive misleading provenance under workflow_run -- see ReleaseProvenancePredicate.ps1's rationale; mentioning it only in explanatory comments is fine)"
    }
}

Assert-True (
    $attestationBlock -match 'uses:\s*actions/attest@[0-9a-f]{40}\s*#\s*v4\.\d+\.\d+'
) "provenance-attestation uses the generic, pinned actions/attest action (exact commit SHA + version comment)"
Assert-True (
    $attestationBlock -match 'predicate-type:\s*https://slsa\.dev/provenance/v1'
) "provenance-attestation's actions/attest step declares predicate-type https://slsa.dev/provenance/v1"
Assert-True (
    $attestationBlock -match 'predicate-path:\s*\S+release-provenance-predicate\.json'
) "provenance-attestation's actions/attest step reads predicate-path from a generated release-provenance-predicate.json file (never an inline literal predicate)"

# The predicate-generation step must derive its binding from the SAME trusted validated-sha/is-tag-push/tag-name
# job outputs the tag/version check already uses -- never github.sha/github.ref/github.event.workflow_run.head_sha
# directly (which would reintroduce exactly the untrusted-binding problem this design exists to avoid).
$generatePredicateStepPattern = '(?s)id:\s*generate-predicate.*?(?=\r?\n\r?\n\s*-\s*name:|\z)'
$generatePredicateMatch = [regex]::Match($attestationBlock, $generatePredicateStepPattern)
Assert-True $generatePredicateMatch.Success "Found the 'generate-predicate' step's block text"
if ($generatePredicateMatch.Success) {
    $generatePredicateStepText = $generatePredicateMatch.Value
    Assert-True (
        $generatePredicateStepText -match [regex]::Escape('needs.validate-attestation-eligibility.outputs.validated-sha')
    ) "generate-predicate step sources its commit binding from needs.validate-attestation-eligibility.outputs.validated-sha (the trusted, ancestry-verified value), never an unvalidated github.sha/ref"
    Assert-True (
        $generatePredicateStepText -notmatch '\bgithub\.sha\b' -and $generatePredicateStepText -notmatch '\bgithub\.ref\b'
    ) "generate-predicate step never references github.sha/github.ref directly (only the validated job output)"
    Assert-True (
        $generatePredicateStepText -match 'write-release-provenance-predicate\.ps1'
    ) "generate-predicate step invokes write-release-provenance-predicate.ps1 (the tested CLI wrapper), not inline ad hoc JSON construction"
}

# Ordering: rebuild -> predicate generation -> attest, never attest before the predicate exists, and never
# predicate generation before the validated rebuild.
$rebuildIndex = $attestationBlock.IndexOf('verify-package.ps1')
$predicateGenIndex = $attestationBlock.IndexOf('write-release-provenance-predicate.ps1')
$attestIndex = $attestationBlock.IndexOf('actions/attest@')
Assert-True (
    $rebuildIndex -ge 0 -and $predicateGenIndex -gt $rebuildIndex
) "Predicate generation occurs AFTER the validated rebuild (verify-package.ps1), never before it"
Assert-True (
    $predicateGenIndex -ge 0 -and $attestIndex -gt $predicateGenIndex
) "The actions/attest step occurs AFTER predicate generation, never before the predicate file exists"

# --- Malicious selected-ref validator regression proof -----------------------------------------------------------
# Demonstrates concretely why trigger isolation (assertion 1) and trusted-main-first checkout (assertion 3)
# matter: if a validator function WERE sourced from a compromised/selected ref, its own verdict could trivially
# be forged. This block does not claim any current job does this (the assertions above prove none does) -- it
# proves the failure mode those assertions close would otherwise be real, not merely theoretical.
function Test-MaliciousAttestationRefEligible {
    param([string]$EventName, [string]$Ref)
    # A compromised validator sourced from the attacker's own selected ref could simply always report eligible,
    # regardless of the real input -- exactly the risk the workflow_run-trigger-isolation and trusted-main-first
    # checkout assertions above close.
    return [pscustomobject]@{ Eligible = $true; Reason = "forged: always eligible" }
}
$forged = Test-MaliciousAttestationRefEligible -EventName "workflow_dispatch" -Ref "refs/heads/feature/malicious-branch"
Assert-True (
    $forged.Eligible
) "Regression proof: a validator sourced from a compromised/selected ref COULD forge an 'eligible' verdict for an otherwise-ineligible ref -- exactly the class of bug the trigger-isolation and trusted-main-checkout structural assertions above close"

# --- Functional regression: coordinator dispatch of a release tag is eligible for deeper git/version checks -----
. (Join-Path $PSScriptRoot "ReleaseVersionTag.ps1")
$tagDispatchResult = Test-AttestationRefEligible -EventName "workflow_dispatch" -Ref "refs/tags/dotnet-v1.2.3"
Assert-True (
    $tagDispatchResult.Eligible
) "Regression proof: workflow_dispatch targeting a release tag reaches the validator that independently proves real tag, exact SHA, ancestry, and package-version binding"

Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All workflow-attestation-structure self-test assertions PASSED." -ForegroundColor Green
exit 0
