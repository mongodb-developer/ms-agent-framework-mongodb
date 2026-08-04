#Requires -Version 7.0
<#
.SYNOPSIS
    Self-test for ReleaseVersionTag.ps1's Get-NupkgVersion/Test-ReleaseTagMatchesVersion -- proves the
    tag/version gate correctly matches, mismatches, and handles pre-release versions and missing-prefix refs.

.DESCRIPTION
    Exercises:
      1. Test-ReleaseTagMatchesVersion:
         - exact match ("1.2.3" / "dotnet-v1.2.3") -> Matches=$true.
         - mismatch ("1.2.3" / "dotnet-v1.2.4") -> Matches=$false.
         - pre-release exact match ("0.1.0-preview.1" / "dotnet-v0.1.0-preview.1") -> Matches=$true (the hyphen
           in a semver pre-release label must not confuse the comparison).
         - pre-release mismatch ("0.1.0-preview.1" / "dotnet-v0.1.0-preview.2") -> Matches=$false.
         - missing "dotnet-v" prefix ("1.2.3" / "v1.2.3") -> Matches=$false.
         - a non-tag branch ref ("1.2.3" / "main") -> Matches=$false (the record-only, non-enforced path).
         - case sensitivity ("1.2.3" / "DOTNET-V1.2.3") -> Matches=$false (git tags are case-sensitive).
      2. Get-NupkgVersion against a real, minimal in-memory-built .nupkg-shaped zip fixture containing only a
         .nuspec entry with a known <version>, proving the parser reads the exact embedded value.
      3. verify-release-tag.ps1's process exit codes end-to-end for both the enforced (tag-push) and
         record-only (workflow_dispatch) invocation shapes, using that same fixture .nupkg.

    Run directly: pwsh dotnet/scripts/verify-release-tag.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ReleaseVersionTag.ps1")

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
# Part 1: Test-ReleaseTagMatchesVersion, pure string comparison -- no fixture files needed.
# ---------------------------------------------------------------------------------------------------------------
$exactMatch = Test-ReleaseTagMatchesVersion -Version "1.2.3" -RefName "dotnet-v1.2.3"
Assert-True $exactMatch.Matches "Exact match: version 1.2.3 / ref dotnet-v1.2.3 -> Matches"
Assert-True ($exactMatch.ExpectedTag -eq "dotnet-v1.2.3") "Exact match reports the expected tag string"

$mismatch = Test-ReleaseTagMatchesVersion -Version "1.2.3" -RefName "dotnet-v1.2.4"
Assert-True (-not $mismatch.Matches) "Mismatch: version 1.2.3 / ref dotnet-v1.2.4 -> does NOT match"

$preReleaseMatch = Test-ReleaseTagMatchesVersion -Version "0.1.0-preview.1" -RefName "dotnet-v0.1.0-preview.1"
Assert-True $preReleaseMatch.Matches "Pre-release exact match: version 0.1.0-preview.1 / ref dotnet-v0.1.0-preview.1 -> Matches"

$preReleaseMismatch = Test-ReleaseTagMatchesVersion -Version "0.1.0-preview.1" -RefName "dotnet-v0.1.0-preview.2"
Assert-True (-not $preReleaseMismatch.Matches) "Pre-release mismatch: version 0.1.0-preview.1 / ref dotnet-v0.1.0-preview.2 -> does NOT match"

$missingPrefix = Test-ReleaseTagMatchesVersion -Version "1.2.3" -RefName "v1.2.3"
Assert-True (-not $missingPrefix.Matches) "Missing 'dotnet-' prefix: version 1.2.3 / ref v1.2.3 -> does NOT match"

$branchRef = Test-ReleaseTagMatchesVersion -Version "1.2.3" -RefName "main"
Assert-True (-not $branchRef.Matches) "Non-tag branch ref: version 1.2.3 / ref main -> does NOT match (record-only path)"

$caseMismatch = Test-ReleaseTagMatchesVersion -Version "1.2.3" -RefName "DOTNET-V1.2.3"
Assert-True (-not $caseMismatch.Matches) "Case sensitivity: version 1.2.3 / ref DOTNET-V1.2.3 -> does NOT match (git tags are case-sensitive)"

# ---------------------------------------------------------------------------------------------------------------
# Part 1b: Test-ValidReleaseTagGrammar -- the defense-in-depth check applied independently of (never as a
# substitute for) passing -RefName as a proper parameter value rather than interpolating it into script text.
# ---------------------------------------------------------------------------------------------------------------
Assert-True (Test-ValidReleaseTagGrammar -RefName "dotnet-v1.2.3") "Grammar: 'dotnet-v1.2.3' is a valid release tag"
Assert-True (Test-ValidReleaseTagGrammar -RefName "dotnet-v0.1.0-preview.1") "Grammar: 'dotnet-v0.1.0-preview.1' is a valid release tag (pre-release label)"
Assert-True (-not (Test-ValidReleaseTagGrammar -RefName "main")) "Grammar: a plain branch name 'main' is not a valid release tag"
Assert-True (-not (Test-ValidReleaseTagGrammar -RefName "")) "Grammar: an empty ref name is not a valid release tag"
Assert-True (-not (Test-ValidReleaseTagGrammar -RefName "dotnet-v")) "Grammar: 'dotnet-v' with no version suffix is not a valid release tag"
Assert-True (-not (Test-ValidReleaseTagGrammar -RefName "DOTNET-V1.2.3")) "Grammar: wrong case 'DOTNET-V1.2.3' is not a valid release tag (case-sensitive)"

# Injection-shaped strings must be rejected by grammar (they contain characters outside [0-9A-Za-z.-]), proving
# the grammar check itself provides a second, independent layer of defense even if some future call site ever
# reintroduced string interpolation by mistake.
$injectionShapedRefNames = @(
    'dotnet-v1.2.3"; Remove-Item -Recurse -Force C:\; "'
    'dotnet-v$(Remove-Item -Recurse -Force C:\)'
    'dotnet-v1.2.3`; whoami'
    "dotnet-v1.2.3'; malicious-command; '"
    "dotnet-v1.2.3;malicious-command"
    "dotnet-v1.2.3 && malicious-command"
)
foreach ($injectionShapedRefName in $injectionShapedRefNames) {
    Assert-True (-not (Test-ValidReleaseTagGrammar -RefName $injectionShapedRefName)) `
        "Grammar: injection-shaped ref name is rejected: $injectionShapedRefName"
}

# ---------------------------------------------------------------------------------------------------------------
# Part 2: Get-NupkgVersion against a real, minimal fixture zip (only a .nuspec entry, no dll/xml needed).
# ---------------------------------------------------------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem

$fixtureDir = Join-Path $PSScriptRoot "../artifacts/release-tag-test-fixtures"
if (Test-Path $fixtureDir) {
    Remove-Item $fixtureDir -Recurse -Force
}
New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null

function New-FixtureNupkg([string]$FileName, [string]$Version) {
    $nuspecXml = @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>MongoDB.AgentFramework</id>
    <version>$Version</version>
  </metadata>
</package>
"@

    $zipPath = Join-Path $fixtureDir $FileName
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry("MongoDB.AgentFramework.nuspec")
        $writer = New-Object System.IO.StreamWriter($entry.Open())
        try {
            $writer.Write($nuspecXml)
        }
        finally {
            $writer.Close()
        }
    }
    finally {
        $zip.Dispose()
    }

    return $zipPath
}

$fixtureNupkgMatching = New-FixtureNupkg -FileName "MongoDB.AgentFramework.1.2.3.nupkg" -Version "1.2.3"
$parsedVersion = Get-NupkgVersion -NupkgPath $fixtureNupkgMatching
Assert-True ($parsedVersion -eq "1.2.3") "Get-NupkgVersion reads the exact <version> embedded in a fixture .nuspec ('1.2.3')"

$fixtureNupkgPreRelease = New-FixtureNupkg -FileName "MongoDB.AgentFramework.0.1.0-preview.1.nupkg" -Version "0.1.0-preview.1"
$parsedPreReleaseVersion = Get-NupkgVersion -NupkgPath $fixtureNupkgPreRelease
Assert-True ($parsedPreReleaseVersion -eq "0.1.0-preview.1") "Get-NupkgVersion reads a pre-release <version> exactly ('0.1.0-preview.1')"

# ---------------------------------------------------------------------------------------------------------------
# Part 3: verify-release-tag.ps1's end-to-end exit codes for both invocation shapes.
# ---------------------------------------------------------------------------------------------------------------
$verifyScript = Join-Path $PSScriptRoot "verify-release-tag.ps1"

& pwsh -NoProfile -File $verifyScript -NupkgPath $fixtureNupkgMatching -RefName "dotnet-v1.2.3" -EnforceMatch | Out-Null
Assert-True ($LASTEXITCODE -eq 0) "verify-release-tag.ps1 -EnforceMatch exits 0 when ref matches the packed version (tag-push shape)"

& pwsh -NoProfile -File $verifyScript -NupkgPath $fixtureNupkgMatching -RefName "dotnet-v9.9.9" -EnforceMatch | Out-Null
Assert-True ($LASTEXITCODE -eq 1) "verify-release-tag.ps1 -EnforceMatch exits 1 when ref does NOT match the packed version (tag-push shape)"

& pwsh -NoProfile -File $verifyScript -NupkgPath $fixtureNupkgMatching -RefName "main" | Out-Null
Assert-True ($LASTEXITCODE -eq 0) "verify-release-tag.ps1 without -EnforceMatch exits 0 even when ref does not match (workflow_dispatch record-only shape)"

& pwsh -NoProfile -File $verifyScript -NupkgPath $fixtureNupkgPreRelease -RefName "dotnet-v0.1.0-preview.1" -EnforceMatch | Out-Null
Assert-True ($LASTEXITCODE -eq 0) "verify-release-tag.ps1 -EnforceMatch exits 0 for a matching pre-release version/tag"

& pwsh -NoProfile -File $verifyScript -NupkgPath $fixtureNupkgMatching -RefName "main" -EnforceMatch | Out-Null
Assert-True ($LASTEXITCODE -eq 1) "verify-release-tag.ps1 -EnforceMatch exits 1 for a grammar-invalid ref ('main') before even comparing versions"

# ---------------------------------------------------------------------------------------------------------------
# Part 4: injection-shaped -RefName values passed to the real script as a PROPER PARAMETER (never interpolated
# into script source text, exactly matching how the fixed dotnet-sbom-provenance.yml workflow now invokes this
# script via `$env:RELEASE_TAG`). Proves these values are treated purely as inert data: the process must not
# execute anything embedded in the string, must not throw unexpectedly, and must report a grammar-invalid,
# non-matching result. Each payload attempts to create a marker file if it were ever re-parsed as PowerShell
# source -- the marker's absence afterward is the actual proof of safety, not merely the exit code.
# ---------------------------------------------------------------------------------------------------------------
$injectionMarkerPath = Join-Path $fixtureDir "injection-marker.txt"
$injectionPayloadRefNames = @(
    "dotnet-v1.2.3`"; Set-Content -Path '$injectionMarkerPath' -Value 'pwned'; `""
    "dotnet-v`$(Set-Content -Path '$injectionMarkerPath' -Value 'pwned')"
    "dotnet-v1.2.3``; Set-Content -Path '$injectionMarkerPath' -Value 'pwned'"
    "dotnet-v1.2.3'; Set-Content -Path '$injectionMarkerPath' -Value 'pwned'; '"
    "dotnet-v1.2.3;Set-Content -Path '$injectionMarkerPath' -Value 'pwned'"
)

foreach ($injectionPayloadRefName in $injectionPayloadRefNames) {
    if (Test-Path $injectionMarkerPath) {
        Remove-Item $injectionMarkerPath -Force
    }

    & pwsh -NoProfile -File $verifyScript -NupkgPath $fixtureNupkgMatching -RefName $injectionPayloadRefName -EnforceMatch | Out-Null
    $exitCode = $LASTEXITCODE
    $markerCreated = Test-Path $injectionMarkerPath

    Assert-True (-not $markerCreated) "Injection-shaped ref name passed as a real parameter never executes embedded commands (no marker file created): $injectionPayloadRefName"
    Assert-True ($exitCode -eq 1) "Injection-shaped ref name passed as a real parameter is treated as inert data and fails grammar/match (-EnforceMatch exits 1): $injectionPayloadRefName"
}

if (Test-Path $injectionMarkerPath) {
    Remove-Item $injectionMarkerPath -Force
}

Remove-Item $fixtureDir -Recurse -Force

# ---------------------------------------------------------------------------------------------------------------
Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All release-tag self-test assertions PASSED." -ForegroundColor Green
exit 0
