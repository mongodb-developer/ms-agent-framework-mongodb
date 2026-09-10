<#
.SYNOPSIS
    Shared, dependency-free VSTest TRX result-count parsing, used by every script/workflow that must prove a
    `dotnet test` invocation actually EXECUTED at least one test, not merely exited 0.

.DESCRIPTION
    `dotnet test --no-build --no-restore` (or, more subtly, a filtered `--filter "Category=..."` run against a
    project that failed to restore/build) can exit 0 having executed zero tests: MSBuild silently no-ops the
    VSTest target when it cannot evaluate the test SDK's `IsTestProject` property, and an empty/impossible
    `--filter` matches nothing and still "succeeds". A console-output/exit-code check alone never catches either
    case. Get-TrxExecutedCount reads the produced TRX file's `<ResultSummary><Counters executed="N">` attribute
    -- "executed" deliberately excludes skipped tests (Counters also has a `total` attribute that includes them),
    so a credentialed integration job that skips every test it filtered for (because MONGODB_URI/MONGODB_DATABASE
    are unset) still correctly reports zero executed and fails this check, exactly as intended: an unconfigured
    credentialed CI run must fail loudly, not silently report a false "0 tests, all green".

    Originally factored out of verify-agent-framework-compatibility.ps1 (which first introduced this exact check
    for the Agent Framework compatibility matrix) so dotnet-integration.yml's per-category credentialed jobs can
    apply the identical, single-source-of-truth parsing logic instead of re-implementing (and potentially
    drifting from) it.
#>

<#
.SYNOPSIS
    Parses a VSTest TRX file's <ResultSummary><Counters> element and returns the executed-test count.

.OUTPUTS
    [int] the "executed" counter, or $null if the file is missing, not well-formed XML, or has no
    <ResultSummary><Counters executed="..."> attribute.
#>
function Get-TrxExecutedCount {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TrxPath
    )

    if (-not (Test-Path $TrxPath)) {
        return $null
    }

    try {
        [xml]$trx = Get-Content $TrxPath -Raw
    }
    catch {
        return $null
    }

    $countersNode = $trx.TestRun.ResultSummary.Counters
    if (-not $countersNode -or -not $countersNode.executed) {
        return $null
    }

    return [int]$countersNode.executed
}
