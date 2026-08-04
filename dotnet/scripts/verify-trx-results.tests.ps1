#Requires -Version 7.0
<#
.SYNOPSIS
    Self-test for TrxResults.ps1's Get-TrxExecutedCount -- proves the TRX-executed-count parsing actually
    distinguishes "tests really executed", "zero executed (all skipped)", "missing file", and "malformed file",
    using real, disposable fixture .trx files (no `dotnet test` run required).

.DESCRIPTION
    Run directly: pwsh dotnet/scripts/verify-trx-results.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "TrxResults.ps1")

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

$fixtureDir = Join-Path $PSScriptRoot "../artifacts/trx-results-test-fixtures"
if (Test-Path $fixtureDir) {
    Remove-Item $fixtureDir -Recurse -Force
}
New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null

function New-FixtureTrx([string]$FileName, [string]$Content) {
    $path = Join-Path $fixtureDir $FileName
    Set-Content -Path $path -Value $Content -Encoding UTF8
    return $path
}

function New-TrxCountersXml([string]$Total, [string]$Executed, [string]$Passed) {
    return @"
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="$Total" executed="$Executed" passed="$Passed" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
"@
}

# ---------------------------------------------------------------------------------------------------------------
# Part 1: a well-formed TRX with a nonzero executed count is parsed exactly.
# ---------------------------------------------------------------------------------------------------------------
$nonZeroTrx = New-FixtureTrx "nonzero.trx" (New-TrxCountersXml -Total "5" -Executed "5" -Passed "5")
Assert-True ((Get-TrxExecutedCount -TrxPath $nonZeroTrx) -eq 5) "A well-formed TRX with executed=`"5`" returns 5"

# ---------------------------------------------------------------------------------------------------------------
# Part 2: a well-formed TRX where every matched test was SKIPPED (executed=0, total>0) returns 0, not $null and
# not the total -- this is the exact scenario an unconfigured credentialed integration job must fail on.
# ---------------------------------------------------------------------------------------------------------------
$allSkippedTrx = New-FixtureTrx "all-skipped.trx" (New-TrxCountersXml -Total "3" -Executed "0" -Passed "0")
Assert-True ((Get-TrxExecutedCount -TrxPath $allSkippedTrx) -eq 0) "A TRX where every matched test was skipped (executed=`"0`", total=`"3`") returns 0, not the total or `$null"

# ---------------------------------------------------------------------------------------------------------------
# Part 3: a missing TRX file (e.g. `dotnet test` silently no-op'd against an unrestored project) returns `$null`.
# ---------------------------------------------------------------------------------------------------------------
$missingTrxPath = Join-Path $fixtureDir "does-not-exist.trx"
Assert-True ($null -eq (Get-TrxExecutedCount -TrxPath $missingTrxPath)) "A missing TRX file returns `$null (never silently treated as zero executed, which callers must distinguish from a real zero-executed run)"

# ---------------------------------------------------------------------------------------------------------------
# Part 4: a malformed/non-XML TRX file returns `$null` rather than throwing.
# ---------------------------------------------------------------------------------------------------------------
$malformedTrx = New-FixtureTrx "malformed.trx" "this is not valid xml <<<"
Assert-True ($null -eq (Get-TrxExecutedCount -TrxPath $malformedTrx)) "A malformed (non-XML) TRX file returns `$null instead of throwing"

# ---------------------------------------------------------------------------------------------------------------
# Part 5: well-formed XML that is missing the <Counters> element entirely (e.g. a truncated/corrupted TRX)
# returns `$null`.
# ---------------------------------------------------------------------------------------------------------------
$noCountersTrx = New-FixtureTrx "no-counters.trx" @"
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed" />
</TestRun>
"@
Assert-True ($null -eq (Get-TrxExecutedCount -TrxPath $noCountersTrx)) "Well-formed XML missing the <Counters> element returns `$null"

Remove-Item $fixtureDir -Recurse -Force

# ---------------------------------------------------------------------------------------------------------------
Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All TRX-results self-test assertions PASSED." -ForegroundColor Green
exit 0
