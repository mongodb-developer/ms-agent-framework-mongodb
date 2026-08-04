<#
.SYNOPSIS
    Shared, dependency-free release-tag/package-version matching logic for MongoDB.AgentFramework's .nupkg.

.DESCRIPTION
    docs/spec/quality-release.md's tag-triggered release path (`dotnet-v<version>`) must never let a mismatched
    tag/version pair reach upload or attestation: a maintainer accidentally pushing `dotnet-v1.2.3` against a
    .csproj whose <Version> is actually `1.2.4` (or vice versa) would attest/publish provenance for an artifact
    that does not correspond to the ref that triggered the release. Get-NupkgVersion parses the exact <version>
    NuGet itself embedded in the packed .nuspec (not the tracked .csproj source, which could theoretically drift
    from what was actually packed), and Test-ReleaseTagMatchesVersion is the pure, testable comparison used by
    both the real `dotnet-sbom-provenance.yml` workflow (see verify-release-tag.ps1) and
    verify-release-tag.tests.ps1's self-test (exact match, mismatch, pre-release version, and missing-prefix
    fixtures).
#>

<#
.SYNOPSIS
    Reads the <version> element from a packed .nupkg's embedded .nuspec.

.PARAMETER NupkgPath
    Path to a .nupkg file.

.OUTPUTS
    [string] the exact version text NuGet embedded in the .nuspec (e.g. "0.1.0-preview.1").
#>
function Get-NupkgVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$NupkgPath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($NupkgPath)
    try {
        $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -like "*.nuspec" } | Select-Object -First 1
        if (-not $nuspecEntry) {
            throw "No .nuspec entry found in '$NupkgPath'."
        }

        $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
        try {
            $nuspecText = $reader.ReadToEnd()
        }
        finally {
            $reader.Close()
        }
    }
    finally {
        $zip.Dispose()
    }

    [xml]$nuspec = $nuspecText
    $version = $nuspec.package.metadata.version
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "'$NupkgPath''s .nuspec has no <version> value."
    }

    return $version
}

<#
.SYNOPSIS
    Compares a package version against a git ref name for this repository's `dotnet-v<version>` tag convention.

.PARAMETER Version
    The exact package version (e.g. from Get-NupkgVersion), such as "0.1.0-preview.1" or "1.2.3".

.PARAMETER RefName
    The ref name to check against (GitHub Actions' `github.ref_name`: the tag name for a tag push, or the
    branch name for a branch push/workflow_dispatch -- never the full `refs/...` ref).

.OUTPUTS
    [pscustomobject] with: Version, RefName, ExpectedTag ("dotnet-v" + Version), Matches ($RefName -ceq
    ExpectedTag, case-sensitive since git tags are case-sensitive).
#>
function Test-ReleaseTagMatchesVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][AllowEmptyString()][string]$RefName
    )

    $expectedTag = "dotnet-v$Version"
    return [pscustomobject]@{
        Version     = $Version
        RefName     = $RefName
        ExpectedTag = $expectedTag
        Matches     = ($RefName -ceq $expectedTag)
    }
}

<#
.SYNOPSIS
    Validates a ref name against this repository's `dotnet-v<version>` tag grammar, as a defense-in-depth check
    independent of (and in addition to) never interpolating the ref name into a shell/PowerShell script body.

.DESCRIPTION
    `dotnet-sbom-provenance.yml`'s tag/version-match step previously interpolated `github.ref_name` directly into
    a `run: |` PowerShell script's source text (`-RefName "${{ github.ref_name }}"`), which GitHub Actions
    substitutes BEFORE the shell ever parses the script -- a ref name crafted with an embedded quote, `$()`
    subexpression, or semicolon could break out of the intended string literal and execute arbitrary code in the
    runner. The fix passes the ref through a step-level `env:` value instead (`$env:RELEASE_TAG`), which is never
    re-parsed as script syntax, so injection via that path is structurally impossible.

    This function is the SEPARATE, additional safeguard the review also required: even with the injection vector
    closed, a malformed/garbage ref name should never silently reach the version-match comparison as if it were
    a plausible tag. `-cmatch` is case-sensitive (git tags are case-sensitive; "DOTNET-V1.2.3" is not this tag).

.OUTPUTS
    [bool] $true only if $RefName matches `^dotnet-v[0-9A-Za-z][0-9A-Za-z.-]*$` exactly (anchored both ends).
#>
function Test-ValidReleaseTagGrammar {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$RefName
    )

    return [bool]($RefName -cmatch '^dotnet-v[0-9A-Za-z][0-9A-Za-z.-]*$')
}

<#
.SYNOPSIS
    Decides whether a triggering event/ref pair is eligible to reach `dotnet-sbom-provenance.yml`'s
    `provenance-attestation` job, as an explicit, testable workflow-logic gate independent of (and in addition
    to) that job's `environment: dotnet-release-attestation` protection rule.

.DESCRIPTION
    A `workflow_dispatch` run lets its operator pick ANY existing branch or tag as the ref to run against --
    including an arbitrary, non-`main` feature branch. If the `dotnet-release-attestation` GitHub Environment
    happens to have no reviewer/branch protection rule configured (an owner-side configuration step this
    workflow cannot enforce or verify from inside the YAML), a `workflow_dispatch` run with
    `confirm_attestation: yes` against an arbitrary selected ref would otherwise still reach and execute the
    attestation job -- the environment gate would have "failed open" rather than acting as a second, independent
    safeguard. This function is the workflow-LOGIC gate that does not depend on that external configuration:

      - A `push` event is only ever eligible for a `refs/tags/dotnet-v<version>` ref matching this repository's
        release-tag grammar (see Test-ValidReleaseTagGrammar) -- this mirrors the workflow's own `on: push:
        tags:` trigger filter, but is re-validated here explicitly rather than assumed, since `startsWith(...)`
        checks elsewhere in the workflow are prefix-only and would also accept a ref like
        `refs/tags/dotnet-v1.2.3-actually-a-different-branch` or one containing `$()`/quote/semicolon shell
        metacharacters that happens to still start with the expected prefix.
      - A `workflow_dispatch` event is only ever eligible for EITHER `refs/heads/main` (the one branch this
        repository's ordinary branch-protection rules are expected to guard) OR a `refs/tags/dotnet-v<version>`
        ref matching the same release-tag grammar -- never an arbitrary feature/topic branch, regardless of
        what the operator selected in the manual dispatch form.
      - Every other event (`pull_request`, or anything else) is never eligible; a fork pull_request can never
        reach this function with a `push`/`workflow_dispatch` event name in the first place, since this job's
        own `if:` condition already restricts to those two events, but this function still fails closed on
        every other input as defense in depth. `-EventName` and every ref comparison use case-sensitive
        (`-ceq`/`-cmatch`) matching throughout -- GitHub Actions itself always emits lowercase event names and
        exact-case refs, so this never rejects a legitimate real trigger, but it does mean this function never
        silently treats e.g. "PUSH" or "refs/heads/MAIN" as equivalent to the real, lowercase values.

    This is deliberately a pure decision function with no I/O, so it can be exercised directly by
    verify-attestation-ref.tests.ps1 without needing a real GitHub Actions run. The workflow additionally runs an
    ANCESTRY check (via `git merge-base --is-ancestor`) after this function passes, confirming the actual
    triggering commit is reachable from `origin/main` -- catching the case where a tag was pushed against a
    commit that was never actually merged to `main`, which this pure ref-shape check alone cannot detect.

.PARAMETER EventName
    `github.event_name` (e.g. "push", "workflow_dispatch", "pull_request").

.PARAMETER Ref
    The FULL ref (`github.ref`, e.g. "refs/heads/main" or "refs/tags/dotnet-v1.2.3") -- never just `ref_name`,
    since only the full form unambiguously distinguishes a branch from a tag of the same short name.

.OUTPUTS
    [pscustomobject] with: Eligible [bool], Reason [string] (a human-readable explanation either way).
#>
function Test-AttestationRefEligible {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$EventName,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Ref
    )

    $releaseTagRefPattern = '^refs/tags/(dotnet-v[0-9A-Za-z][0-9A-Za-z.-]*)$'

    if ($EventName -ceq 'push') {
        if ($Ref -cmatch $releaseTagRefPattern) {
            return [pscustomobject]@{
                Eligible = $true
                Reason   = "push event targeting ref '$Ref', which matches the required 'refs/tags/dotnet-v<version>' release-tag grammar"
            }
        }

        return [pscustomobject]@{
            Eligible = $false
            Reason   = "push event targeting ref '$Ref', which is NOT a valid 'refs/tags/dotnet-v<version>' release tag -- refusing to attest"
        }
    }

    if ($EventName -ceq 'workflow_dispatch') {
        if ($Ref -ceq 'refs/heads/main') {
            return [pscustomobject]@{
                Eligible = $true
                Reason   = "workflow_dispatch targeting the protected 'refs/heads/main' branch"
            }
        }

        if ($Ref -cmatch $releaseTagRefPattern) {
            return [pscustomobject]@{
                Eligible = $true
                Reason   = "workflow_dispatch targeting ref '$Ref', which matches the required 'refs/tags/dotnet-v<version>' release-tag grammar"
            }
        }

        return [pscustomobject]@{
            Eligible = $false
            Reason   = "workflow_dispatch targeting ref '$Ref', which is neither 'refs/heads/main' nor a valid 'refs/tags/dotnet-v<version>' release tag -- refusing to attest for an arbitrary selected ref"
        }
    }

    return [pscustomobject]@{
        Eligible = $false
        Reason   = "event '$EventName' is not an attestation-eligible trigger (only 'push' of a release tag or 'workflow_dispatch' are ever eligible) -- refusing to attest"
    }
}
