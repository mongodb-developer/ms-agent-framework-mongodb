#Requires -Version 7.0
<#
.SYNOPSIS
    Verifies a packed MongoDB.AgentFramework .nupkg's embedded version matches the triggering release ref, or
    records it without enforcement for a non-tag trigger.

.DESCRIPTION
    Implements the tag/version gate docs/spec/quality-release.md requires before a release artifact ever reaches
    upload or provenance attestation: on a trusted push of a `dotnet-v*` tag, the .nuspec's embedded <version>
    (see dotnet/scripts/ReleaseVersionTag.ps1's Get-NupkgVersion) MUST exactly match `dotnet-v<version>` for the
    tag that triggered the run (`github.ref_name`), or the run fails before anything is uploaded/attested -- a
    maintainer pushing `dotnet-v1.2.3` against a package actually packed as `1.2.4` (or vice versa) must never
    silently attest/publish provenance for a mismatched artifact.

    For a `workflow_dispatch` (or any other non-tag-push) trigger there is no tag to validate against, so this
    script only records/prints the package version and the ref it was invoked with -- it never fails for a
    mismatch in that mode, per this repository's "manual dispatch can record/check without enforcing tag match"
    design. Pass -EnforceMatch only for the trusted tag-push trigger path.

.PARAMETER NupkgPath
    Path to the packed .nupkg to read the version from.

.PARAMETER RefName
    The triggering ref name (GitHub Actions' `github.ref_name`).

.PARAMETER EnforceMatch
    When set, fails (exit 1) if RefName does not exactly equal `dotnet-v<version>`. When not set, mismatches are
    printed as a note and the script still exits 0 (record-only mode, for workflow_dispatch).

.EXAMPLE
    pwsh dotnet/scripts/verify-release-tag.ps1 -NupkgPath artifacts/packages/MongoDB.AgentFramework.1.2.3.nupkg -RefName "dotnet-v1.2.3" -EnforceMatch

.EXAMPLE
    pwsh dotnet/scripts/verify-release-tag.ps1 -NupkgPath artifacts/packages/MongoDB.AgentFramework.1.2.3.nupkg -RefName "main"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NupkgPath,
    [Parameter(Mandatory)][AllowEmptyString()][string]$RefName,
    [switch]$EnforceMatch
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ReleaseVersionTag.ps1")

if (-not (Test-Path $NupkgPath)) {
    Write-Host "[FAIL] No .nupkg found at '$NupkgPath'." -ForegroundColor Red
    exit 1
}

$version = Get-NupkgVersion -NupkgPath $NupkgPath
$check = Test-ReleaseTagMatchesVersion -Version $version -RefName $RefName

Write-Host "Package version (from packed .nuspec): $($check.Version)"
Write-Host "Triggering ref name: $($check.RefName)"
Write-Host "Expected tag for this version: $($check.ExpectedTag)"

if ($check.Matches) {
    Write-Host "[ OK ] ref '$($check.RefName)' matches the expected release tag for package version '$($check.Version)'" -ForegroundColor Green
    exit 0
}

if ($EnforceMatch) {
    Write-Host "[FAIL] ref '$($check.RefName)' does NOT match the expected release tag '$($check.ExpectedTag)' for package version '$($check.Version)' -- refusing to upload/attest a mismatched artifact" -ForegroundColor Red
    exit 1
}

Write-Host "[ NOTE ] ref '$($check.RefName)' does not match '$($check.ExpectedTag)' -- not enforced for this trigger (record-only; pass -EnforceMatch on a trusted dotnet-v* tag push)" -ForegroundColor Yellow
exit 0
