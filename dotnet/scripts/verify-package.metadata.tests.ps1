#Requires -Version 7.0
<#
.SYNOPSIS
    Self-test for PackageMetadataAssertions.ps1's Test-NuspecAssertion/Get-NuspecMetadataAssertions -- proves the
    nuspec metadata check actually fails when a required value is wrong or missing, using plain PSCustomObject
    fixtures (no packed artifact required).

.DESCRIPTION
    This self-test exists because verify-package.ps1's previous `Invoke-Checked` helper discarded every
    assertion scriptblock's return value and only ever failed if the scriptblock THREW -- an assertion that
    legitimately evaluated to the boolean $false (e.g. a wrong package id, or a missing README) still printed
    "[ OK ]" and was silently treated as passing. Exercises:

      1. Test-NuspecAssertion's own contract in isolation (no nuspec/metadata involved at all):
         - a scriptblock returning $true -> Passed=$true.
         - a scriptblock returning the boolean $false -> Passed=$false (the exact case the old helper missed).
         - a scriptblock that throws -> Passed=$false, with the exception message captured.
         - a scriptblock returning a non-boolean "truthy" value (a non-empty string) -> Passed=$false (no
           implicit truthiness coercion is allowed to silently pass).
      2. Get-NuspecMetadataAssertions against a fully valid metadata fixture -- every required assertion must
         PASS.
      3. Get-NuspecMetadataAssertions against a fixture with each single required field, one at a time, mutated
         to a wrong or missing value -- the corresponding named assertion must FAIL, while every other assertion
         on that same mutated fixture continues to PASS (proving the failure is attributable to exactly the
         mutated field, not a fixture-construction mistake).

    Run directly: pwsh dotnet/scripts/verify-package.metadata.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PackageMetadataAssertions.ps1")

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

# ---------------------------------------------------------------------------------------------------------------
# Part 1: Test-NuspecAssertion's own contract, independent of any nuspec/metadata shape.
# ---------------------------------------------------------------------------------------------------------------
$passResult = Test-NuspecAssertion -Description "always true" -Body { $true }
Assert-True $passResult.Passed "A scriptblock returning `$true is reported as Passed"

# This is the direct regression test for the original bug: the scriptblock returns the boolean $false without
# throwing. The previous Invoke-Checked implementation discarded this return value and reported "[ OK ]" anyway.
$falseResult = Test-NuspecAssertion -Description "always false" -Body { $false }
Assert-True (-not $falseResult.Passed) "A scriptblock returning `$false (without throwing) is reported as Passed=`$false"
Assert-True (-not [string]::IsNullOrWhiteSpace($falseResult.Message)) "A `$false result carries a non-empty failure message"

$throwResult = Test-NuspecAssertion -Description "throws" -Body { throw "boom" }
Assert-True (-not $throwResult.Passed) "A scriptblock that throws is reported as Passed=`$false"
Assert-True ($throwResult.Message -like "*boom*") "A thrown exception's message is captured in the result"

# A non-boolean "truthy" return (non-empty string) must NOT be silently treated as a pass -- no implicit
# truthiness coercion is allowed, since that would reintroduce a variant of the same bug.
$nonBooleanResult = Test-NuspecAssertion -Description "non-boolean truthy" -Body { "some non-empty string" }
Assert-True (-not $nonBooleanResult.Passed) "A non-boolean 'truthy' return value is reported as Passed=`$false, not silently coerced to true"

$nullResult = Test-NuspecAssertion -Description "null" -Body { $null }
Assert-True (-not $nullResult.Passed) "A `$null return value is reported as Passed=`$false"

# ---------------------------------------------------------------------------------------------------------------
# Part 2: a fully valid metadata fixture must pass every required assertion.
# ---------------------------------------------------------------------------------------------------------------
function New-ValidNuspecMetadataFixture {
    # A fresh object graph every call, so mutating one test's fixture can never leak into another's via a shared
    # reference to a nested object (license/repository/dependencies).
    return [pscustomobject]@{
        id           = "MongoDB.AgentFramework"
        version      = "0.1.0-preview.1"
        authors      = "MongoDB"
        license      = [pscustomobject]@{ type = "expression"; '#text' = "MIT" }
        licenseUrl   = "https://licenses.nuget.org/MIT"
        readme       = "README.md"
        projectUrl   = "https://github.com/mongo/ms-agent-framework-mongodb"
        description  = "MongoDB integrations for Microsoft Agent Framework."
        releaseNotes = "See CHANGELOG.md."
        copyright    = "Copyright (c) MongoDB, Inc."
        tags         = "mongodb agent-framework ai"
        repository   = [pscustomobject]@{ url = "https://github.com/mongo/ms-agent-framework-mongodb"; commit = "abc123def456"; branch = "main" }
        dependencies = [pscustomobject]@{ group = @([pscustomobject]@{ targetFramework = "net10.0" }) }
    }
}

function Test-AllAssertions([pscustomobject]$Metadata) {
    $assertions = Get-NuspecMetadataAssertions -Metadata $Metadata
    $results = [ordered]@{}
    foreach ($name in $assertions.Keys) {
        $results[$name] = Test-NuspecAssertion -Description $name -Body $assertions[$name]
    }

    return $results
}

$validResults = Test-AllAssertions (New-ValidNuspecMetadataFixture)
$expectedAssertionNames = @(
    "id equals MongoDB.AgentFramework"
    "version is set"
    "authors is set"
    "license expression is MIT"
    "licenseUrl is set (legacy consumer fallback)"
    "readme is set"
    "projectUrl is set"
    "description is set"
    "releaseNotes is set"
    "copyright is set"
    "tags is set"
    "repository url is embedded (SourceLink)"
    "repository commit is embedded (SourceLink)"
    "at least one per-TFM dependency group"
)

Assert-True ((($validResults.Keys | Sort-Object) -join '|') -eq (($expectedAssertionNames | Sort-Object) -join '|')) `
    "Get-NuspecMetadataAssertions returns exactly the expected set of named assertions"

$allValidPass = -not ($validResults.Values | Where-Object { -not $_.Passed })
Assert-True $allValidPass "Every required assertion PASSES against a fully valid metadata fixture"
if (-not $allValidPass) {
    foreach ($failed in ($validResults.Values | Where-Object { -not $_.Passed })) {
        Write-Host "         unexpectedly failed: $($failed.Description) -- $($failed.Message)" -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Part 3: one field mutated to a wrong/missing value at a time -- the corresponding assertion must fail, and
# every other assertion on that same mutated fixture must continue to pass.
# ---------------------------------------------------------------------------------------------------------------
function Test-SingleFieldMutation([string]$MutationLabel, [string]$ExpectedFailingAssertion, [scriptblock]$Mutate) {
    $fixture = New-ValidNuspecMetadataFixture
    & $Mutate $fixture

    $results = Test-AllAssertions $fixture
    $failingNames = @($results.Keys | Where-Object { -not $results[$_].Passed })

    Assert-True ($failingNames.Count -eq 1 -and $failingNames[0] -eq $ExpectedFailingAssertion) `
        "$MutationLabel -- exactly and only '$ExpectedFailingAssertion' fails (got: $($failingNames -join ', '))"
}

Test-SingleFieldMutation "Wrong id" "id equals MongoDB.AgentFramework" { param($m) $m.id = "Some.Other.Package" }
Test-SingleFieldMutation "Missing version" "version is set" { param($m) $m.version = "" }
Test-SingleFieldMutation "Missing authors" "authors is set" { param($m) $m.authors = "   " }
Test-SingleFieldMutation "Wrong license type" "license expression is MIT" { param($m) $m.license.type = "file" }
Test-SingleFieldMutation "Wrong license text" "license expression is MIT" { param($m) $m.license.'#text' = "Apache-2.0" }
Test-SingleFieldMutation "Missing licenseUrl" "licenseUrl is set (legacy consumer fallback)" { param($m) $m.licenseUrl = "" }
Test-SingleFieldMutation "Missing readme" "readme is set" { param($m) $m.readme = "" }
Test-SingleFieldMutation "Missing projectUrl" "projectUrl is set" { param($m) $m.projectUrl = "" }
Test-SingleFieldMutation "Missing description" "description is set" { param($m) $m.description = "" }
Test-SingleFieldMutation "Missing releaseNotes" "releaseNotes is set" { param($m) $m.releaseNotes = "" }
Test-SingleFieldMutation "Missing copyright" "copyright is set" { param($m) $m.copyright = "" }
Test-SingleFieldMutation "Missing tags" "tags is set" { param($m) $m.tags = "" }
Test-SingleFieldMutation "Missing repository url" "repository url is embedded (SourceLink)" { param($m) $m.repository.url = "" }
Test-SingleFieldMutation "Missing repository commit" "repository commit is embedded (SourceLink)" { param($m) $m.repository.commit = "" }
Test-SingleFieldMutation "Zero dependency groups" "at least one per-TFM dependency group" { param($m) $m.dependencies.group = @() }

# ---------------------------------------------------------------------------------------------------------------
Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All package-metadata self-test assertions PASSED." -ForegroundColor Green
exit 0
