#Requires -Version 7.0
<#
.SYNOPSIS
    Fails (exit 1) unless the triggering GitHub Actions event/ref pair is eligible to reach
    dotnet-sbom-provenance.yml's provenance-attestation job.

.DESCRIPTION
    Thin CLI wrapper around ReleaseVersionTag.ps1's Test-AttestationRefEligible -- see that function's doc
    comment for the full rationale (workflow_dispatch on an arbitrary selected ref must never reach attestation
    just because a GitHub Environment protection rule may not be configured; this is the independent
    workflow-logic gate, not a substitute for that environment).

    -EventName/-Ref MUST be passed as proper PowerShell parameter values (e.g. from step-level `env:` values),
    NEVER by interpolating `${{ github.event_name }}`/`${{ github.ref }}` directly into a `run: |` script's
    source text -- see dotnet/scripts/verify-release-tag.ps1's identical rationale.

.PARAMETER EventName
    `github.event_name`.

.PARAMETER Ref
    The full `github.ref` (e.g. "refs/heads/main" or "refs/tags/dotnet-v1.2.3").

.EXAMPLE
    pwsh dotnet/scripts/verify-attestation-ref.ps1 -EventName push -Ref "refs/tags/dotnet-v1.2.3"

.EXAMPLE
    pwsh dotnet/scripts/verify-attestation-ref.ps1 -EventName workflow_dispatch -Ref "refs/heads/main"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][AllowEmptyString()][string]$EventName,
    [Parameter(Mandatory)][AllowEmptyString()][string]$Ref
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ReleaseVersionTag.ps1")

$result = Test-AttestationRefEligible -EventName $EventName -Ref $Ref

if ($result.Eligible) {
    Write-Host "[ OK ] $($result.Reason)" -ForegroundColor Green
    exit 0
}

Write-Host "[FAIL] $($result.Reason)" -ForegroundColor Red
exit 1
