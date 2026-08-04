#Requires -Version 7.0
<#
.SYNOPSIS
    Self-test for ConsumerCacheVerification.ps1 -- proves the "restored package hash matches the locally-packed
    .nupkg" check actually fails when the recorded hash is wrong/stale, the library entry is missing, or the
    library type is not "package", using a real (small, disposable) fixture file and plain PSCustomObject-shaped
    project.assets.json fixtures (no real `dotnet restore` required).

.DESCRIPTION
    Run directly: pwsh dotnet/scripts/verify-consumer-cache.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ConsumerCacheVerification.ps1")

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

$fixtureDir = Join-Path $PSScriptRoot "../artifacts/consumer-cache-test-fixtures"
if (Test-Path $fixtureDir) {
    Remove-Item $fixtureDir -Recurse -Force
}
New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null

# A small, deterministic fixture "nupkg" (its actual bytes are irrelevant -- this test only proves the hash
# comparison logic itself, not real NuGet packing).
$fixtureNupkgPath = Join-Path $fixtureDir "MongoDB.AgentFramework.0.1.0-preview.1.nupkg"
Set-Content -Path $fixtureNupkgPath -Value "fixture nupkg content" -NoNewline -Encoding UTF8

# ---------------------------------------------------------------------------------------------------------------
# Part 1: Get-Sha512Base64 is deterministic and matches .NET's own SHA512 computation for the same bytes.
# ---------------------------------------------------------------------------------------------------------------
$expectedHash = [Convert]::ToBase64String([System.Security.Cryptography.SHA512]::HashData([System.IO.File]::ReadAllBytes($fixtureNupkgPath)))
$actualHash = Get-Sha512Base64 -FilePath $fixtureNupkgPath
Assert-True ($actualHash -ceq $expectedHash) "Get-Sha512Base64 reproduces .NET's own SHA512 hash for the same file bytes"

# ---------------------------------------------------------------------------------------------------------------
# Part 2: Test-ConsumerCacheResolvedPackedPackage against plain PSCustomObject project.assets.json fixtures.
# ---------------------------------------------------------------------------------------------------------------
function New-ValidProjectAssetsFixture([string]$Sha512) {
    return [pscustomobject]@{
        libraries = [pscustomobject]@{
            "MongoDB.AgentFramework/0.1.0-preview.1" = [pscustomobject]@{
                type   = "package"
                sha512 = $Sha512
                path   = "mongodb.agentframework/0.1.0-preview.1"
            }
        }
    }
}

$validAssets = New-ValidProjectAssetsFixture -Sha512 $expectedHash
Assert-True (Test-ConsumerCacheResolvedPackedPackage -ProjectAssets $validAssets -PackageId "MongoDB.AgentFramework" -PackageVersion "0.1.0-preview.1" -NupkgPath $fixtureNupkgPath) `
    "A restored library entry whose recorded sha512 matches the packed .nupkg's actual hash PASSES"

$wrongHashAssets = New-ValidProjectAssetsFixture -Sha512 "d29lc29tZS1vdGhlci1wYWNrYWdlLWhhc2gtdGhhdC1kb2VzLW5vdC1tYXRjaA=="
Assert-True (-not (Test-ConsumerCacheResolvedPackedPackage -ProjectAssets $wrongHashAssets -PackageId "MongoDB.AgentFramework" -PackageVersion "0.1.0-preview.1" -NupkgPath $fixtureNupkgPath)) `
    "A restored library entry whose recorded sha512 does NOT match the packed .nupkg's hash FAILS (e.g. a stale cache entry or a spoofed same-id/version package from the wrong source)"

$missingLibraryAssets = [pscustomobject]@{ libraries = [pscustomobject]@{} }
Assert-True (-not (Test-ConsumerCacheResolvedPackedPackage -ProjectAssets $missingLibraryAssets -PackageId "MongoDB.AgentFramework" -PackageVersion "0.1.0-preview.1" -NupkgPath $fixtureNupkgPath)) `
    "A project.assets.json with no matching library entry at all FAILS"

$wrongTypeAssets = New-ValidProjectAssetsFixture -Sha512 $expectedHash
$wrongTypeAssets.libraries."MongoDB.AgentFramework/0.1.0-preview.1".type = "project"
Assert-True (-not (Test-ConsumerCacheResolvedPackedPackage -ProjectAssets $wrongTypeAssets -PackageId "MongoDB.AgentFramework" -PackageVersion "0.1.0-preview.1" -NupkgPath $fixtureNupkgPath)) `
    "A restored library entry whose type is not 'package' (e.g. resolved as a project reference) FAILS"

$missingSha512Assets = New-ValidProjectAssetsFixture -Sha512 ""
Assert-True (-not (Test-ConsumerCacheResolvedPackedPackage -ProjectAssets $missingSha512Assets -PackageId "MongoDB.AgentFramework" -PackageVersion "0.1.0-preview.1" -NupkgPath $fixtureNupkgPath)) `
    "A restored library entry with no recorded sha512 at all FAILS"

$wrongVersionAssets = New-ValidProjectAssetsFixture -Sha512 $expectedHash
Assert-True (-not (Test-ConsumerCacheResolvedPackedPackage -ProjectAssets $wrongVersionAssets -PackageId "MongoDB.AgentFramework" -PackageVersion "9.9.9" -NupkgPath $fixtureNupkgPath)) `
    "Looking up a different package version than what is actually recorded FAILS (no entry at that key)"

Remove-Item $fixtureDir -Recurse -Force

# ---------------------------------------------------------------------------------------------------------------
Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All consumer-cache self-test assertions PASSED." -ForegroundColor Green
exit 0
