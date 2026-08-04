#Requires -Version 7.0
<#
.SYNOPSIS
    Self-test for assert-trx-executed.ps1 -- proves the CLI wrapper actually exits nonzero for every "no proof
    of execution" case (missing file, malformed file, zero-executed file) and exits zero only when the TRX
    proves a positive executed count, by invoking the real script as a child process (not just calling the
    underlying function in-process).

.DESCRIPTION
    Run directly: pwsh dotnet/scripts/verify-assert-trx-executed.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "assert-trx-executed.ps1"
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

$fixtureDir = Join-Path $PSScriptRoot "../artifacts/assert-trx-executed-test-fixtures"
if (Test-Path $fixtureDir) {
    Remove-Item $fixtureDir -Recurse -Force
}
New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null

function New-FixtureTrx([string]$FileName, [string]$Content) {
    $path = Join-Path $fixtureDir $FileName
    Set-Content -Path $path -Value $Content -Encoding UTF8
    return $path
}

function New-TrxCountersXml([string]$Total, [string]$Executed) {
    return @"
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="$Total" executed="$Executed" passed="$Executed" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
"@
}

function Invoke-AssertTrxExecuted([string]$TrxPath, [string]$Label) {
    & pwsh -NoProfile -File $scriptPath -TrxPath $TrxPath -Label $Label 2>&1 | Out-Null
    return $LASTEXITCODE
}

# ---------------------------------------------------------------------------------------------------------------
# Nonzero executed -> exit 0.
# ---------------------------------------------------------------------------------------------------------------
$nonZeroTrx = New-FixtureTrx "nonzero.trx" (New-TrxCountersXml -Total "4" -Executed "4")
Assert-True ((Invoke-AssertTrxExecuted -TrxPath $nonZeroTrx -Label "nonzero-case") -eq 0) "A TRX with executed=`"4`" exits 0"

# ---------------------------------------------------------------------------------------------------------------
# Zero executed (all skipped) -> exit 1. This is the credentialed-integration-job-with-no-secrets failure mode.
# ---------------------------------------------------------------------------------------------------------------
$zeroExecutedTrx = New-FixtureTrx "zero-executed.trx" (New-TrxCountersXml -Total "2" -Executed "0")
Assert-True ((Invoke-AssertTrxExecuted -TrxPath $zeroExecutedTrx -Label "zero-executed-case") -eq 1) "A TRX with executed=`"0`" (all matched tests skipped) exits 1"

# ---------------------------------------------------------------------------------------------------------------
# Missing file -> exit 1.
# ---------------------------------------------------------------------------------------------------------------
$missingTrxPath = Join-Path $fixtureDir "does-not-exist.trx"
Assert-True ((Invoke-AssertTrxExecuted -TrxPath $missingTrxPath -Label "missing-case") -eq 1) "A missing TRX file exits 1"

# ---------------------------------------------------------------------------------------------------------------
# Malformed file -> exit 1.
# ---------------------------------------------------------------------------------------------------------------
$malformedTrx = New-FixtureTrx "malformed.trx" "not xml at all <<<"
Assert-True ((Invoke-AssertTrxExecuted -TrxPath $malformedTrx -Label "malformed-case") -eq 1) "A malformed (non-XML) TRX file exits 1"

Remove-Item $fixtureDir -Recurse -Force

# ---------------------------------------------------------------------------------------------------------------
Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All assert-trx-executed.ps1 self-test assertions PASSED." -ForegroundColor Green
exit 0
