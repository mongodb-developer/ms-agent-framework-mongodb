#Requires -Version 7.0
<#
.SYNOPSIS
    Self-test for PackageAllowlist.ps1's Test-PackageContentAllowlist -- proves the exact-match, multiplicity-
    aware allowlist check actually fails the shapes of breakage it is meant to catch, using fixture entry lists
    (no packed artifact required).

.DESCRIPTION
    Exercises Test-PackageContentAllowlist with:
      1. The real expected nupkg/snupkg entry sets themselves (with a plausible random psmdcp GUID) -- must PASS.
      2. A required entry removed (e.g. README.md, or one TFM's assembly) -- must FAIL, reported as Missing.
      3. An extra, unexpected entry added (e.g. a stray sample/test file) -- must FAIL, reported as Unexpected.
      4. A required entry duplicated (multiplicity 2 where exactly 1 is expected) -- must FAIL, reported as
         MultiplicityMismatch.
      5. Two different valid pack runs using two different random psmdcp GUIDs -- both must still PASS, proving
         GUID normalization is doing its job rather than accidentally requiring a fixed GUID.
      6. A malformed core-properties filename that does not match the real 32-hex-digit GUID shape -- must FAIL,
         proving normalization does not silently wave through anything under core-properties/.

    Run directly: pwsh dotnet/scripts/verify-package.allowlist.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PackageAllowlist.ps1")

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

function New-FakePsmdcpName {
    # A syntactically real 32-hex-digit GUID-shaped psmdcp filename, matching what NuGet.Client actually emits.
    return "package/services/metadata/core-properties/$((New-Guid).ToString('N')).psmdcp"
}

# ---------------------------------------------------------------------------------------------------------------
# Fixture 1: exact match of the real expected sets (with a genuine random-GUID psmdcp name) must PASS.
# ---------------------------------------------------------------------------------------------------------------
function Get-ValidNupkgFixture {
    return @($script:NupkgExpectedEntries | ForEach-Object {
        if ($_ -eq 'package/services/metadata/core-properties/{guid}.psmdcp') { New-FakePsmdcpName } else { $_ }
    })
}

function Get-ValidSnupkgFixture {
    return @($script:SnupkgExpectedEntries | ForEach-Object {
        if ($_ -eq 'package/services/metadata/core-properties/{guid}.psmdcp') { New-FakePsmdcpName } else { $_ }
    })
}

$result = Test-PackageContentAllowlist -ActualEntries (Get-ValidNupkgFixture) -ExpectedEntries $script:NupkgExpectedEntries -Label "nupkg-valid"
Assert-True $result.Passed "Fixture 1a (valid nupkg entry set) passes"
Assert-True ($result.Missing.Count -eq 0) "Fixture 1a reports zero missing entries"
Assert-True ($result.Unexpected.Count -eq 0) "Fixture 1a reports zero unexpected entries"

$result = Test-PackageContentAllowlist -ActualEntries (Get-ValidSnupkgFixture) -ExpectedEntries $script:SnupkgExpectedEntries -Label "snupkg-valid"
Assert-True $result.Passed "Fixture 1b (valid snupkg entry set) passes"

# ---------------------------------------------------------------------------------------------------------------
# Fixture 2: a required entry is completely absent (README.md removed) -- must FAIL, reported as Missing.
# ---------------------------------------------------------------------------------------------------------------
$missingReadme = @(Get-ValidNupkgFixture | Where-Object { $_ -ne 'README.md' })
$result = Test-PackageContentAllowlist -ActualEntries $missingReadme -ExpectedEntries $script:NupkgExpectedEntries -Label "nupkg-missing-readme"
Assert-True (-not $result.Passed) "Fixture 2 (README.md absent) FAILS the allowlist"
Assert-True ($result.Missing -contains 'README.md') "Fixture 2 reports README.md specifically as Missing"

# A required TFM assembly absent must also fail -- this is the exact 'required files absent fails' case.
$missingNet9Dll = @(Get-ValidNupkgFixture | Where-Object { $_ -ne 'lib/net9.0/MongoDB.AgentFramework.dll' })
$result = Test-PackageContentAllowlist -ActualEntries $missingNet9Dll -ExpectedEntries $script:NupkgExpectedEntries -Label "nupkg-missing-net9-dll"
Assert-True (-not $result.Passed) "Fixture 2b (net9.0 dll absent) FAILS the allowlist"
Assert-True ($result.Missing -contains 'lib/net9.0/MongoDB.AgentFramework.dll') "Fixture 2b reports the missing net9.0 dll specifically"

# ---------------------------------------------------------------------------------------------------------------
# Fixture 3: an extra, unexpected entry is present (e.g. leaked sample/test content) -- must FAIL as Unexpected.
# ---------------------------------------------------------------------------------------------------------------
$withStrayFile = @(Get-ValidNupkgFixture) + @('lib/net10.0/MongoDB.AgentFramework.Samples.RagVectorAnn.dll')
$result = Test-PackageContentAllowlist -ActualEntries $withStrayFile -ExpectedEntries $script:NupkgExpectedEntries -Label "nupkg-stray-file"
Assert-True (-not $result.Passed) "Fixture 3 (stray sample assembly leaked into package) FAILS the allowlist"
Assert-True (($result.Unexpected | Where-Object { $_ -like 'lib/net10.0/MongoDB.AgentFramework.Samples.RagVectorAnn.dll*' }).Count -eq 1) "Fixture 3 reports the stray file specifically as Unexpected"

# ---------------------------------------------------------------------------------------------------------------
# Fixture 4: a required entry is duplicated (multiplicity 2 where exactly 1 is expected) -- must FAIL as
# MultiplicityMismatch, even though every distinct name is individually "allowed".
# ---------------------------------------------------------------------------------------------------------------
$duplicatedDll = @(Get-ValidNupkgFixture) + @('lib/net8.0/MongoDB.AgentFramework.dll')
$result = Test-PackageContentAllowlist -ActualEntries $duplicatedDll -ExpectedEntries $script:NupkgExpectedEntries -Label "nupkg-duplicated-dll"
Assert-True (-not $result.Passed) "Fixture 4 (net8.0 dll duplicated) FAILS the allowlist"
Assert-True (($result.MultiplicityMismatch | Where-Object { $_ -like 'lib/net8.0/MongoDB.AgentFramework.dll*' }).Count -eq 1) "Fixture 4 reports the duplicate specifically as a MultiplicityMismatch"
Assert-True ($result.Missing.Count -eq 0 -and $result.Unexpected.Count -eq 0) "Fixture 4's duplicate is classified as MultiplicityMismatch, not Missing/Unexpected"

# ---------------------------------------------------------------------------------------------------------------
# Fixture 5: two different packs use two different random psmdcp GUIDs -- both must still PASS (normalization).
# ---------------------------------------------------------------------------------------------------------------
$runA = Test-PackageContentAllowlist -ActualEntries (Get-ValidNupkgFixture) -ExpectedEntries $script:NupkgExpectedEntries -Label "run-a"
$runB = Test-PackageContentAllowlist -ActualEntries (Get-ValidNupkgFixture) -ExpectedEntries $script:NupkgExpectedEntries -Label "run-b"
Assert-True ($runA.Passed -and $runB.Passed) "Fixture 5 (two independent random-GUID psmdcp fixtures) both pass identically"

# ---------------------------------------------------------------------------------------------------------------
# Fixture 6: a malformed core-properties filename (not a real 32-hex-digit GUID) must NOT be silently normalized
# away -- it must still show up and fail the allowlist (as both Missing the real slot and Unexpected itself).
# ---------------------------------------------------------------------------------------------------------------
$malformedPsmdcp = @(Get-ValidNupkgFixture | Where-Object { $_ -notlike '*.psmdcp' }) + @('package/services/metadata/core-properties/not-a-guid.psmdcp')
$result = Test-PackageContentAllowlist -ActualEntries $malformedPsmdcp -ExpectedEntries $script:NupkgExpectedEntries -Label "nupkg-malformed-psmdcp"
Assert-True (-not $result.Passed) "Fixture 6 (malformed psmdcp filename) FAILS the allowlist"
Assert-True ($result.Missing -contains 'package/services/metadata/core-properties/{guid}.psmdcp') "Fixture 6 reports the real psmdcp slot as Missing"
Assert-True (($result.Unexpected | Where-Object { $_ -like 'package/services/metadata/core-properties/not-a-guid.psmdcp*' }).Count -eq 1) "Fixture 6 reports the malformed filename itself as Unexpected"

# ---------------------------------------------------------------------------------------------------------------
Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All package-allowlist self-test assertions PASSED." -ForegroundColor Green
exit 0
