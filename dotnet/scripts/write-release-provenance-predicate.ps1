#Requires -Version 7.0
<#
.SYNOPSIS
    CLI wrapper: writes New-ReleaseProvenancePredicate's custom SLSA v1.0 predicate JSON to -OutputPath, for
    `dotnet-release-attestation.yml`'s `provenance-attestation` job to pass to `actions/attest`'s
    `-predicate-path` input.

.DESCRIPTION
    See ReleaseProvenancePredicate.ps1's header comment for the full rationale: this predicate exists because the
    stock `actions/attest-build-provenance` auto-provenance mode would bind the attestation to this job's own
    ambient GITHUB_SHA/GITHUB_REF (main's tip, under this workflow's `workflow_run` trigger), not the validated
    release commit that was actually checked out and rebuilt.

    -ValidatedSha/-RepositorySlug/-RunId/-RunAttempt/-IsTagPush/-TagName MUST be passed as proper PowerShell
    parameter values (e.g. from step-level `env:` values sourced from `needs.*.outputs`/`github.*` contexts),
    NEVER by interpolating those expressions directly into a `run: |` script's source text -- the same injection
    rationale documented throughout this repository's release scripts (a ref/tag name is not restricted to
    shell-safe characters by git itself).

.PARAMETER ValidatedSha
    See New-ReleaseProvenancePredicate.

.PARAMETER RepositorySlug
    See New-ReleaseProvenancePredicate.

.PARAMETER RunId
    See New-ReleaseProvenancePredicate.

.PARAMETER RunAttempt
    See New-ReleaseProvenancePredicate.

.PARAMETER IsTagPush
    String "true" or "false" (as emitted by a GitHub Actions step output / `needs.*.outputs.is-tag-push`) --
    parsed strictly; any other value fails fast rather than being silently coerced.

.PARAMETER TagName
    See New-ReleaseProvenancePredicate.

.PARAMETER OutputPath
    File path the predicate JSON is written to (parent directory created if missing).

.EXAMPLE
    pwsh dotnet/scripts/write-release-provenance-predicate.ps1 -ValidatedSha <sha> -RepositorySlug "mongo/ms-agent-framework-mongodb" -RunId 12345 -RunAttempt 1 -IsTagPush true -TagName "dotnet-v1.2.3" -OutputPath artifacts/release-provenance-predicate.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ValidatedSha,
    [Parameter(Mandatory)][string]$RepositorySlug,
    [Parameter(Mandatory)][string]$RunId,
    [Parameter(Mandatory)][string]$RunAttempt,
    [Parameter(Mandatory)][string]$IsTagPush,
    [AllowEmptyString()][string]$TagName = "",
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ReleaseProvenancePredicate.ps1")

if ($IsTagPush -cne 'true' -and $IsTagPush -cne 'false') {
    Write-Host "[FAIL] -IsTagPush must be exactly 'true' or 'false' (got '$IsTagPush') -- refusing to silently coerce an unexpected value" -ForegroundColor Red
    exit 1
}

$predicate = New-ReleaseProvenancePredicate `
    -ValidatedSha $ValidatedSha `
    -RepositorySlug $RepositorySlug `
    -RunId $RunId `
    -RunAttempt $RunAttempt `
    -IsTagPush ($IsTagPush -ceq 'true') `
    -TagName $TagName

$outputDir = Split-Path -Path $OutputPath -Parent
if ($outputDir -and -not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

($predicate | ConvertTo-Json -Depth 10) | Set-Content -Path $OutputPath -NoNewline

Write-Host "[ OK ] Wrote custom release-provenance predicate binding validated commit '$ValidatedSha' to '$OutputPath'" -ForegroundColor Green
exit 0
