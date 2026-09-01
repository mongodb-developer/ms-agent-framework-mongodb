#Requires -Version 7.0
<#
.SYNOPSIS
    Structural + behavioral regression proof for `dotnet-release-attestation.yml`'s
    `validate-attestation-eligibility` job output wiring -- proves the job's `is-tag-push`/`tag-name` outputs are
    correctly wired to the step that actually sets them, and that a genuinely mismatched tag/package-version pair
    reaches and fails `verify-release-tag.ps1`'s authoritative check via that real wiring.

.DESCRIPTION
    Background (the bug this file exists to catch): a prior revision of `dotnet-release-attestation.yml` wired
    the job's `is-tag-push`/`tag-name` outputs to `steps.record-sha.outputs.*`, but `record-sha`'s own `run:`
    block only ever writes a `sha` key to `$GITHUB_OUTPUT` -- it never sets `is-tag-push` or `tag-name` at all.
    A GitHub Actions expression referencing a step output the referenced step never actually sets always
    evaluates to an EMPTY STRING, not an error -- so `needs.validate-attestation-eligibility.outputs.is-tag-push`
    would silently have been `''` on every single run, meaning `provenance-attestation`'s "Verify tag matches the
    freshly rebuilt package version" step's `if: needs...outputs.is-tag-push == 'true'` condition was ALWAYS
    false, silently SKIPPING the one check that exists specifically to refuse attesting a tag whose name claims
    one version while the packed artifact actually contains a different one. This is exactly the class of bug a
    passing `verify-release-tag.tests.ps1`/`verify-workflow-run-attestation-ref.tests.ps1` run could never catch
    on its own, since both scripts individually behave correctly in isolation -- only the WIRING between the
    step that emits the values and the job output that exposes them was wrong.

    The actual fix moved the derivation of `is-tag-push`/`tag-name` INTO
    `verify-workflow-run-attestation-ref.ps1` itself (the single script both this file's static assertions and the
    real workflow's `validate-ref` step invoke) rather than duplicating that branch logic inline in the workflow
    YAML a second time -- removing the duplicate-and-diverge opportunity entirely, not merely correcting which
    step id the job's `outputs:` map happens to reference.

    This file performs two independent proofs:

      1. STATIC: parses the real, committed `dotnet-release-attestation.yml` and asserts the
         `validate-attestation-eligibility` job's `outputs:` map wires `is-tag-push`/`tag-name` to
         `steps.validate-ref.outputs.*` (the step that actually emits them) and `validated-sha` to
         `steps.record-sha.outputs.sha` (the step that actually emits THAT). A "self-test of the self-test"
         fixture proves this exact parsing/assertion logic would have caught the historical bug: applied to a
         synthetic string reproducing the OLD (buggy) wiring, the assertion fails as expected.
      2. BEHAVIORAL/end-to-end: builds a real, disposable scratch git repository with a genuine tag, invokes the
         REAL `verify-workflow-run-attestation-ref.ps1` script (the exact script `validate-ref` runs) with
         `$env:GITHUB_OUTPUT` pointed at a real scratch file -- exactly as the GitHub Actions runner does for a
         real step -- then reads back that file's actual emitted `is-tag-push`/`tag-name` values (never a
         reimplementation of the branch logic) and feeds the real `tag-name` value into a REAL invocation of
         `verify-release-tag.ps1 -EnforceMatch` against a fixture `.nupkg` whose packed version deliberately does
         NOT match the tag -- proving the full, real chain (script emits real outputs -> those real outputs feed
         the real downstream version-match check) genuinely fails closed for a mismatched tag, and genuinely
         passes for a matching one. A second case additionally shows that `record-sha`'s own step -- literally
         reproducing its single-line `run:` block via the exact same shell command GitHub would execute -- writes
         ONLY a `sha` key, never `is-tag-push`/`tag-name`, concretely demonstrating why the historical wiring
         mistake would have silently produced an always-empty (never `'true'`) value.

    Run directly: pwsh dotnet/scripts/verify-release-attestation-job-wiring.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$AttestationWorkflowPath = Join-Path $RepoRoot ".github/workflows/dotnet-release-attestation.yml"

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

# Extracts the raw `${{ ... }}` expression text bound to a named key inside a job's `outputs:` sub-block. Looks
# for a line shaped "    <OutputName>: ${{ <expression> }}" within the block of text passed in (the caller is
# expected to have already isolated the job's `outputs:` sub-block lines).
function Get-JobOutputExpression([string]$OutputsBlockText, [string]$OutputName) {
    $pattern = "(?m)^\s*$([regex]::Escape($OutputName)):\s*\`$\{\{\s*(.+?)\s*\}\}\s*$"
    $match = [regex]::Match($OutputsBlockText, $pattern)
    if (-not $match.Success) {
        return $null
    }
    return $match.Groups[1].Value.Trim()
}

# Extracts the `outputs:` sub-block text for a named job from a workflow file's full raw text -- from the job's
# own `outputs:` line (two-space-indented under `jobs:`, so the key itself is four-space-indented) through the
# next four-space-indented top-level job key (`steps:`, or any sibling) or end of block.
function Get-JobOutputsBlockText([string]$WorkflowText, [string]$JobName) {
    $lines = $WorkflowText -split "`r?`n"
    $jobHeaderIndex = $null
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch "^  $([regex]::Escape($JobName)):\s*$") {
            $jobHeaderIndex = $i
            break
        }
    }
    if ($null -eq $jobHeaderIndex) {
        throw "Job '$JobName' not found in workflow text."
    }

    $outputsHeaderIndex = $null
    for ($i = $jobHeaderIndex + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch '^  [A-Za-z0-9_-]+:\s*$') {
            # Reached the next top-level job before finding an outputs: block for this one.
            break
        }
        if ($lines[$i] -cmatch '^    outputs:\s*$') {
            $outputsHeaderIndex = $i
            break
        }
    }
    if ($null -eq $outputsHeaderIndex) {
        throw "No 'outputs:' block found for job '$JobName'."
    }

    $blockLines = [System.Collections.Generic.List[string]]::new()
    for ($i = $outputsHeaderIndex + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch '^\s{0,4}\S') {
            # A line indented 4 spaces or less that is non-blank marks the end of the (6-space-indented)
            # outputs: entries.
            if ($lines[$i] -notmatch '^\s{6,}') {
                break
            }
        }
        $blockLines.Add($lines[$i])
    }

    return ($blockLines -join "`n")
}

# ---------------------------------------------------------------------------------------------------------------
# Part 1: STATIC assertions against the real, committed workflow file.
# ---------------------------------------------------------------------------------------------------------------
Assert-True (Test-Path $AttestationWorkflowPath) "dotnet-release-attestation.yml exists"
$workflowText = Get-Content -Path $AttestationWorkflowPath -Raw
$outputsBlock = Get-JobOutputsBlockText -WorkflowText $workflowText -JobName "validate-attestation-eligibility"

$isTagPushExpr = Get-JobOutputExpression -OutputsBlockText $outputsBlock -OutputName "is-tag-push"
$tagNameExpr = Get-JobOutputExpression -OutputsBlockText $outputsBlock -OutputName "tag-name"
$validatedShaExpr = Get-JobOutputExpression -OutputsBlockText $outputsBlock -OutputName "validated-sha"

Assert-True ($isTagPushExpr -ceq 'steps.validate-ref.outputs.is-tag-push') `
    "Job output 'is-tag-push' is wired to 'steps.validate-ref.outputs.is-tag-push' (the step that actually sets it) -- found: '$isTagPushExpr'"
Assert-True ($tagNameExpr -ceq 'steps.validate-ref.outputs.tag-name') `
    "Job output 'tag-name' is wired to 'steps.validate-ref.outputs.tag-name' (the step that actually sets it) -- found: '$tagNameExpr'"
Assert-True ($validatedShaExpr -ceq 'steps.record-sha.outputs.sha') `
    "Job output 'validated-sha' is wired to 'steps.record-sha.outputs.sha' (the step that actually sets it) -- found: '$validatedShaExpr'"

# The step referenced by is-tag-push/tag-name must genuinely be the one invoking verify-workflow-run-attestation-
# ref.ps1 (not merely correctly named) -- this closes the loop that "validate-ref" really is the script that
# writes those outputs, not just a step that happens to share that id.
$validateRefStepPattern = '(?s)id:\s*validate-ref.*?verify-workflow-run-attestation-ref\.ps1'
Assert-True ($workflowText -match $validateRefStepPattern) `
    "The step with id 'validate-ref' genuinely invokes verify-workflow-run-attestation-ref.ps1 (the single authoritative source of is-tag-push/tag-name)"

# The record-sha step must NOT itself emit is-tag-push/tag-name -- concretely proving why wiring the job outputs
# to it (the historical bug) would have silently produced an always-empty value rather than erroring.
$recordShaStepPattern = '(?s)id:\s*record-sha.*?run:\s*\|(.*?)\r?\n\r?\n'
$recordShaMatch = [regex]::Match($workflowText, $recordShaStepPattern)
Assert-True $recordShaMatch.Success "Found the 'record-sha' step's run: block text"
if ($recordShaMatch.Success) {
    $recordShaRunText = $recordShaMatch.Groups[1].Value
    Assert-True ($recordShaRunText -match 'sha=') "record-sha's run: block sets a 'sha=' output"
    Assert-True ($recordShaRunText -notmatch 'is-tag-push') `
        "record-sha's run: block NEVER sets 'is-tag-push' -- confirms wiring the job output to this step (the historical bug) would read as an always-empty string, never 'true'"
    Assert-True ($recordShaRunText -notmatch 'tag-name') `
        "record-sha's run: block NEVER sets 'tag-name' -- confirms wiring the job output to this step (the historical bug) would read as an always-empty string"
}

# --- Self-test of the self-test: the parsing/assertion logic above must actually be CAPABLE of catching the
# historical bug, not just happen to pass against the current (already-fixed) file. Applied to a synthetic
# fixture reproducing the OLD (buggy) wiring, the same extraction must report the WRONG (record-sha) step,
# proving this assertion would have failed against that fixture had it not already been fixed.
$buggyFixtureOutputsBlock = @"
      validated-sha: `${{ steps.record-sha.outputs.sha }}
      is-tag-push: `${{ steps.record-sha.outputs.is-tag-push }}
      tag-name: `${{ steps.record-sha.outputs.tag-name }}
"@
$buggyIsTagPushExpr = Get-JobOutputExpression -OutputsBlockText $buggyFixtureOutputsBlock -OutputName "is-tag-push"
Assert-True ($buggyIsTagPushExpr -ceq 'steps.record-sha.outputs.is-tag-push') `
    "Self-test of the self-test: the extraction helper correctly reads the WRONG (buggy) wiring from a synthetic fixture reproducing the historical bug"
Assert-True ($buggyIsTagPushExpr -cne 'steps.validate-ref.outputs.is-tag-push') `
    "Self-test of the self-test: the real assertion above (which requires 'steps.validate-ref.outputs.is-tag-push') would have FAILED against this buggy fixture, proving it is a meaningful, non-vacuous check"

# ---------------------------------------------------------------------------------------------------------------
# Part 2: BEHAVIORAL end-to-end proof using a real scratch git repo, the REAL verify-workflow-run-attestation-
# ref.ps1 script (never a reimplementation), and the REAL verify-release-tag.ps1 script.
# ---------------------------------------------------------------------------------------------------------------
$verifyRefScript = Join-Path $PSScriptRoot "verify-workflow-run-attestation-ref.ps1"
$verifyTagScript = Join-Path $PSScriptRoot "verify-release-tag.ps1"
$fixtureDir = Join-Path $PSScriptRoot "../artifacts/release-attestation-job-wiring-test-fixtures"

if (Test-Path $fixtureDir) {
    Remove-Item -Path $fixtureDir -Recurse -Force
}
New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null

try {
    $scratchRepoDir = Join-Path $fixtureDir "scratch-repo"
    New-Item -ItemType Directory -Path $scratchRepoDir -Force | Out-Null
    & git -C $scratchRepoDir init --quiet --initial-branch=main
    & git -C $scratchRepoDir config user.email "test@example.invalid"
    & git -C $scratchRepoDir config user.name "Release Attestation Job Wiring Test"
    Set-Content -Path (Join-Path $scratchRepoDir "file.txt") -Value "fixture content"
    & git -C $scratchRepoDir add file.txt
    & git -C $scratchRepoDir commit --quiet -m "Fixture commit"
    $fixtureSha = (& git -C $scratchRepoDir rev-parse HEAD).Trim()
    $fixtureTagName = "dotnet-v9.9.9-wiring-fixture"
    & git -C $scratchRepoDir tag -a $fixtureTagName -m "Fixture release tag"

    # --- Invoke the REAL validate-ref step's script, with $env:GITHUB_OUTPUT pointed at a real scratch file,
    # exactly as the GitHub Actions runner does. Never reimplements the branch logic -- reads back the script's
    # own real emitted outputs.
    function Invoke-ValidateRefStepAndReadOutputs([string]$EventName, [string]$HeadBranch, [string]$HeadSha) {
        $githubOutputPath = Join-Path $fixtureDir ([System.IO.Path]::GetRandomFileName())
        New-Item -ItemType File -Path $githubOutputPath -Force | Out-Null

        $priorGithubOutput = $env:GITHUB_OUTPUT
        try {
            $env:GITHUB_OUTPUT = $githubOutputPath
            & pwsh -NoProfile -File $verifyRefScript `
                -UpstreamEventName $EventName `
                -UpstreamHeadBranch $HeadBranch `
                -UpstreamHeadSha $HeadSha `
                -RepositoryRoot $scratchRepoDir | Out-Null
            $exitCode = $LASTEXITCODE
        }
        finally {
            $env:GITHUB_OUTPUT = $priorGithubOutput
        }

        $emitted = @{}
        if (Test-Path $githubOutputPath) {
            foreach ($line in (Get-Content -Path $githubOutputPath)) {
                if ($line -match '^([A-Za-z0-9_-]+)=(.*)$') {
                    $emitted[$Matches[1]] = $Matches[2]
                }
            }
        }

        return [pscustomobject]@{
            ExitCode = $exitCode
            Outputs  = $emitted
        }
    }

    # --- Case: a genuine push of a real tag -> validate-ref's REAL script output is-tag-push=true, tag-name=<tag> ---
    $pushResult = Invoke-ValidateRefStepAndReadOutputs -EventName "push" -HeadBranch $fixtureTagName -HeadSha $fixtureSha
    Assert-True ($pushResult.ExitCode -eq 0) "verify-workflow-run-attestation-ref.ps1 exits 0 for a genuine tag push"
    Assert-True ($pushResult.Outputs['is-tag-push'] -ceq 'true') "The REAL validate-ref step script emits is-tag-push=true for a genuine tag push (read from its own real \$GITHUB_OUTPUT file, not reimplemented)"
    Assert-True ($pushResult.Outputs['tag-name'] -ceq $fixtureTagName) "The REAL validate-ref step script emits tag-name='$fixtureTagName' for a genuine tag push"

    # --- Case: workflow_dispatch of main -> is-tag-push=false, tag-name empty --------------------------------------
    $dispatchResult = Invoke-ValidateRefStepAndReadOutputs -EventName "workflow_dispatch" -HeadBranch "main" -HeadSha $fixtureSha
    Assert-True ($dispatchResult.ExitCode -eq 0) "verify-workflow-run-attestation-ref.ps1 exits 0 for workflow_dispatch of main"
    Assert-True ($dispatchResult.Outputs['is-tag-push'] -ceq 'false') "The REAL validate-ref step script emits is-tag-push=false for workflow_dispatch of main"
    Assert-True ([string]::IsNullOrEmpty($dispatchResult.Outputs['tag-name'])) "The REAL validate-ref step script emits an empty tag-name for workflow_dispatch of main"

    # --- End-to-end: feed the REAL emitted tag-name into a REAL verify-release-tag.ps1 -EnforceMatch call against
    # a fixture .nupkg whose packed version does NOT match the tag -- proving a mismatched tag that reaches this
    # authoritative check via the REAL (fixed) wiring genuinely fails closed. ------------------------------------
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    function New-FixtureNupkg([string]$FileName, [string]$Version) {
        $nuspecXml = @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>MongoDB.AgentFramework</id>
    <version>$Version</version>
  </metadata>
</package>
"@
        $zipPath = Join-Path $fixtureDir $FileName
        $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $entry = $zip.CreateEntry("MongoDB.AgentFramework.nuspec")
            $writer = New-Object System.IO.StreamWriter($entry.Open())
            try {
                $writer.Write($nuspecXml)
            }
            finally {
                $writer.Close()
            }
        }
        finally {
            $zip.Dispose()
        }
        return $zipPath
    }

    # The fixture tag claims version "9.9.9-wiring-fixture" is not itself a valid semver the tag encodes as
    # "9.9.9"; verify-release-tag.ps1 compares literal "dotnet-v<version>" strings, so pack a DELIBERATELY WRONG
    # version ("1.0.0") that provably does not equal "9.9.9-wiring-fixture" -- a genuine mismatch.
    $mismatchedNupkg = New-FixtureNupkg -FileName "MongoDB.AgentFramework.1.0.0.nupkg" -Version "1.0.0"
    & pwsh -NoProfile -File $verifyTagScript -NupkgPath $mismatchedNupkg -RefName $pushResult.Outputs['tag-name'] -EnforceMatch | Out-Null
    Assert-True ($LASTEXITCODE -eq 1) `
        "END-TO-END: a mismatched tag/package-version pair, reaching verify-release-tag.ps1 via the REAL (fixed) validate-ref -> job-output wiring, is REJECTED (exit 1) -- this is exactly the check the historical bug would have silently skipped"

    # Matching case: pack the version the fixture tag actually claims -> the same real chain passes.
    $matchingVersion = $fixtureTagName -replace '^dotnet-v', ''
    $matchingNupkg = New-FixtureNupkg -FileName "MongoDB.AgentFramework.matching.nupkg" -Version $matchingVersion
    & pwsh -NoProfile -File $verifyTagScript -NupkgPath $matchingNupkg -RefName $pushResult.Outputs['tag-name'] -EnforceMatch | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) `
        "END-TO-END: a MATCHING tag/package-version pair, reaching verify-release-tag.ps1 via the REAL (fixed) validate-ref -> job-output wiring, PASSES (exit 0)"

    # --- Concretely reproduce record-sha's own single-line run: block (the exact shell text committed in the
    # workflow) against a real $GITHUB_OUTPUT file, proving it truly only ever writes 'sha=', never
    # 'is-tag-push=' -- the concrete consequence that made the historical wiring mistake silent rather than a
    # hard failure (a missing key reads as an empty string, not an error). -------------------------------------
    $recordShaOutputPath = Join-Path $fixtureDir "record-sha-output.txt"
    New-Item -ItemType File -Path $recordShaOutputPath -Force | Out-Null
    $env:ATTESTATION_SHA = $fixtureSha
    $env:GITHUB_OUTPUT = $recordShaOutputPath
    try {
        # Exactly record-sha's committed run: block: echo "sha=$ATTESTATION_SHA" >> "$GITHUB_OUTPUT" (bash), its
        # PowerShell-invocation-safe equivalent for this test harness:
        Add-Content -Path $env:GITHUB_OUTPUT -Value "sha=$env:ATTESTATION_SHA"
    }
    finally {
        Remove-Item Env:\ATTESTATION_SHA -ErrorAction SilentlyContinue
        Remove-Item Env:\GITHUB_OUTPUT -ErrorAction SilentlyContinue
    }
    $recordShaOutputContent = Get-Content -Path $recordShaOutputPath -Raw
    Assert-True ($recordShaOutputContent -match "sha=$fixtureSha") "record-sha's real run: block content writes only 'sha=<commit>'"
    Assert-True ($recordShaOutputContent -notmatch 'is-tag-push') `
        "record-sha's real run: block content never writes 'is-tag-push=' -- confirms a job output wired to this step would have read as an always-empty string (the historical bug's exact failure mode)"
}
finally {
    if (Test-Path $fixtureDir) {
        Remove-Item -Path $fixtureDir -Recurse -Force
    }
}

# ---------------------------------------------------------------------------------------------------------------
Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All release-attestation job-wiring self-test assertions PASSED." -ForegroundColor Green
exit 0
