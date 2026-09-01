#Requires -Version 7.0
<#
.SYNOPSIS
    Fails (exit 1) unless a VSTest TRX file exists and reports a strictly positive executed-test count.

.DESCRIPTION
    Reusable CLI wrapper around TrxResults.ps1's Get-TrxExecutedCount, for any CI job (or local run) that must
    prove a `dotnet test` invocation actually executed at least one test -- not merely exited 0. See
    TrxResults.ps1's header comment for why a console/exit-code check alone is insufficient: a project that
    failed to restore, or a `--filter` that matched nothing, can both "succeed" having executed zero tests.

    dotnet-integration.yml's per-category credentialed jobs use this after each `dotnet test --filter
    "Category=<category>" --logger trx` invocation: a credentialed CI run (real MONGODB_URI/MONGODB_DATABASE
    configured) that skips every test in its filtered category must fail here, since skipped tests are excluded
    from "executed". An unconfigured local contributor run is expected to skip everything and is never routed
    through this script in that mode (see the workflow's preflight credential-presence check, which runs first).

.PARAMETER TrxPath
    Path to the TRX file a preceding `dotnet test --logger "trx;LogFileName=..."` step produced.

.PARAMETER Label
    A short label for this check's log output (e.g. the category/job name), so a matrix job's output is easy to
    attribute to the right filtered test run.

.EXAMPLE
    pwsh dotnet/scripts/assert-trx-executed.ps1 -TrxPath artifacts/test-results/integration-memory.trx -Label integration-memory
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TrxPath,
    [Parameter(Mandatory)][string]$Label
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "TrxResults.ps1")

$executedCount = Get-TrxExecutedCount -TrxPath $TrxPath

if ($null -eq $executedCount) {
    Write-Host "[FAIL] $Label -- no TRX result file found (or it was malformed) at '$TrxPath'; cannot confirm any test actually executed" -ForegroundColor Red
    exit 1
}

if ($executedCount -le 0) {
    Write-Host "[FAIL] $Label -- TRX reports zero executed tests at '$TrxPath'. If this is a credentialed integration job, the required MONGODB_URI/MONGODB_DATABASE secrets are likely missing or every test in this category was skipped; a passing exit code alone does not prove any test ran." -ForegroundColor Red
    exit 1
}

Write-Host "[ OK ] $Label -- $executedCount test(s) executed per TRX ('$TrxPath')" -ForegroundColor Green
exit 0
