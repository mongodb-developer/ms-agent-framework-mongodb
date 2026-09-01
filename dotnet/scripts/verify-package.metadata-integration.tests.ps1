#Requires -Version 7.0
<#
.SYNOPSIS
    Integration self-test for PackageMetadataAssertions.ps1 against a REAL, freshly-packed .nuspec -- not the
    synthetic PSCustomObject fixtures verify-package.metadata.tests.ps1 uses.

.DESCRIPTION
    A prior "complete verifier" reported that verify-package.ps1's production metadata phase (Step 3) failed with
    "the term 'Get-NuspecDependencyGroupsByTfm' is not recognized" for all three per-TFM dependency-group
    assertions, even though verify-package.metadata.tests.ps1's self-test passed every one of those same named
    assertions. That self-test exclusively builds and evaluates assertions in one single, fixed invocation shape:
    a plain PSCustomObject fixture, evaluated by a helper function it defines locally (Test-AllAssertions), all
    within the one scope that dot-sourced PackageMetadataAssertions.ps1. verify-package.ps1's real Step 3 uses a
    genuinely different metadata SHAPE (a live System.Xml.XmlElement produced by parsing a real .nuspec, not a
    PSCustomObject) but the *same* single flat invocation scope -- so neither script ever exercised the specific
    combination that can trigger this class of bug: a `.GetNewClosure()`'d scriptblock (used for the three
    per-TFM assertions, so each can capture its own loop-scoped TFM name) calling a sibling function
    (Get-NuspecDependencyGroupsByTfm) that was never captured as data, only resolved by ambient command-name
    lookup at the moment the closure is invoked. That lookup depends on the closure's own isolated dynamic
    module/session-state chaining back to wherever this file happened to be dot-sourced from -- an
    implementation detail, not a documented guarantee -- and is not exercised at all by a self-test that always
    invokes the produced assertions from the exact same scope shape used to build them.

    This test closes that gap two ways:

      1. Uses a REAL nuspec: packs the actual MongoDB.AgentFramework.csproj (Release, deterministic/CI mode,
         identical to verify-package.ps1's own Invoke-Pack) and extracts+parses the real .nuspec exactly the way
         verify-package.ps1's Step 3 does (`[xml]$nuspec = $nuspecText; $metadata = $nuspec.package.metadata`),
         so `$Metadata` is a genuine System.Xml.XmlElement, never a synthetic PSCustomObject.

      2. Invokes the resulting assertion scriptblocks from THREE deliberately different scope shapes -- flat
         top-level (matching verify-package.ps1's own Step 3), one level of function nesting (matching
         verify-package.metadata.tests.ps1's Test-AllAssertions wrapper shape), and two levels of function
         nesting via a second, separately dot-sourced helper file (a shape neither existing script uses) -- and
         asserts every required named assertion, especially the three per-TFM dependency-group checks, actually
         executes and reports Passed=$true in ALL three shapes. If PackageMetadataAssertions.ps1 ever regresses
         to relying on ambient function-name resolution from inside a closure again, this is the test that would
         catch it even where the other two self-tests would not, because it is the only one that varies the
         invocation scope shape at all.

      3. Repeats the real-nuspec run with one dependency version deliberately corrupted directly in the raw
         nuspec XML text (a real npm-style version-range perturbation, not a fixture object mutation), proving
         the per-TFM "exactly the expected package ids and version ranges" assertion genuinely fails against
         real XML shape too, not only against the PSCustomObject fixtures the other self-test uses.

    Run directly: pwsh dotnet/scripts/verify-package.metadata-integration.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
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
# Pack the real project and extract the real .nuspec text, identically to verify-package.ps1's own Invoke-Pack /
# Step 3, into a dedicated output directory so this self-test never disturbs verify-package.ps1's own artifacts.
# ---------------------------------------------------------------------------------------------------------------
$DotnetRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$SrcProject = Join-Path $DotnetRoot "src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj"
$OutputDir = Join-Path $DotnetRoot "artifacts/packages-metadata-integration-test"

if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}

Write-Host "Packing $SrcProject ($Configuration) for the metadata integration self-test..." -ForegroundColor Cyan
& dotnet pack $SrcProject `
    --configuration $Configuration `
    -p:ContinuousIntegrationBuild=true `
    -p:CI=true `
    -p:PackageOutputPath=$OutputDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed with exit code $LASTEXITCODE"
}

$nupkgPath = (Get-ChildItem $OutputDir -Filter "*.nupkg" | Select-Object -First 1).FullName
if (-not $nupkgPath) {
    throw "No .nupkg produced in $OutputDir"
}

function Get-RealNuspecText([string]$NupkgPath) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($NupkgPath)
    try {
        $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -eq "MongoDB.AgentFramework.nuspec" }
        if (-not $nuspecEntry) {
            throw "No MongoDB.AgentFramework.nuspec entry found in $NupkgPath"
        }

        $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Close()
        }
    }
    finally {
        $zip.Dispose()
    }
}

$realNuspecText = Get-RealNuspecText $nupkgPath
Assert-True (-not [string]::IsNullOrWhiteSpace($realNuspecText)) "The real packed .nuspec was extracted and is non-empty"

[xml]$realNuspec = $realNuspecText
$realMetadata = $realNuspec.package.metadata
Assert-True ($realMetadata -is [System.Xml.XmlElement]) `
    "The real parsed nuspec metadata is a genuine System.Xml.XmlElement, not a synthetic PSCustomObject (`$realMetadata is $($realMetadata.GetType().FullName))"

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
    "dependency groups are exactly net8.0, net9.0, and net10.0 (no more, no less)"
    "net8.0 dependency group has exactly the expected package ids and version ranges"
    "net9.0 dependency group has exactly the expected package ids and version ranges"
    "net10.0 dependency group has exactly the expected package ids and version ranges"
    "no analyzer/source-link/build-only packages leak into the nuspec dependency list"
)

$perTfmDependencyAssertionNames = @(
    "net8.0 dependency group has exactly the expected package ids and version ranges"
    "net9.0 dependency group has exactly the expected package ids and version ranges"
    "net10.0 dependency group has exactly the expected package ids and version ranges"
)

# -----------------------------------------------------------------------------------------------------------
# Shape 1: flat top-level invocation -- identical scope shape to verify-package.ps1's own Step 3 (no wrapping
# function at all between the dot-sourced file and the assertion loop).
# -----------------------------------------------------------------------------------------------------------
Write-Host ""
Write-Host "-- Shape 1: flat top-level invocation (matches verify-package.ps1 Step 3) --" -ForegroundColor Cyan
$flatAssertions = Get-NuspecMetadataAssertions -Metadata $realMetadata
$flatResults = [ordered]@{}
foreach ($name in $flatAssertions.Keys) {
    $flatResults[$name] = Test-NuspecAssertion -Description $name -Body $flatAssertions[$name]
}

Assert-True ((($flatResults.Keys | Sort-Object) -join '|') -eq (($expectedAssertionNames | Sort-Object) -join '|')) `
    "Shape 1: Get-NuspecMetadataAssertions returns exactly the expected set of named assertions against the real nuspec"
foreach ($name in $perTfmDependencyAssertionNames) {
    Assert-True $flatResults[$name].Passed "Shape 1: '$name' executes and PASSES against the real nuspec (message: '$($flatResults[$name].Message)')"
}
$flatAllPass = -not ($flatResults.Values | Where-Object { -not $_.Passed })
Assert-True $flatAllPass "Shape 1: every required assertion PASSES against the real, valid, freshly-packed nuspec"
if (-not $flatAllPass) {
    foreach ($failed in ($flatResults.Values | Where-Object { -not $_.Passed })) {
        Write-Host "         unexpectedly failed: $($failed.Description) -- $($failed.Message)" -ForegroundColor Yellow
    }
}

# -----------------------------------------------------------------------------------------------------------
# Shape 2: one level of function nesting -- matches verify-package.metadata.tests.ps1's own Test-AllAssertions
# wrapper shape, but against the real XmlElement metadata instead of a PSCustomObject fixture.
# -----------------------------------------------------------------------------------------------------------
Write-Host ""
Write-Host "-- Shape 2: one level of function nesting (matches the existing self-test's wrapper shape) --" -ForegroundColor Cyan
function Test-AllAssertionsOneLevelDeep([System.Xml.XmlElement]$Metadata) {
    $assertions = Get-NuspecMetadataAssertions -Metadata $Metadata
    $results = [ordered]@{}
    foreach ($name in $assertions.Keys) {
        $results[$name] = Test-NuspecAssertion -Description $name -Body $assertions[$name]
    }

    return $results
}

$oneLevelResults = Test-AllAssertionsOneLevelDeep $realMetadata
foreach ($name in $perTfmDependencyAssertionNames) {
    Assert-True $oneLevelResults[$name].Passed "Shape 2: '$name' executes and PASSES one function level deep (message: '$($oneLevelResults[$name].Message)')"
}
$oneLevelAllPass = -not ($oneLevelResults.Values | Where-Object { -not $_.Passed })
Assert-True $oneLevelAllPass "Shape 2: every required assertion PASSES one function level deep against the real nuspec"

# -----------------------------------------------------------------------------------------------------------
# Shape 3: two levels of function nesting, via a SEPARATE dot-sourced helper file -- a shape neither
# verify-package.ps1 nor verify-package.metadata.tests.ps1 exercises, deliberately as different as possible
# from both of the shapes above while still calling the exact same production functions.
# -----------------------------------------------------------------------------------------------------------
Write-Host ""
Write-Host "-- Shape 3: two levels of function nesting via a second dot-sourced helper file --" -ForegroundColor Cyan
$deepHelperPath = Join-Path $DotnetRoot "artifacts/packages-metadata-integration-test/_deep-invocation-helper.ps1"
New-Item -ItemType Directory -Path (Split-Path $deepHelperPath) -Force | Out-Null
@'
function Invoke-AssertionsOuter([System.Xml.XmlElement]$Metadata) {
    function Invoke-AssertionsInner([System.Xml.XmlElement]$InnerMetadata) {
        $assertions = Get-NuspecMetadataAssertions -Metadata $InnerMetadata
        $results = [ordered]@{}
        foreach ($name in $assertions.Keys) {
            $results[$name] = Test-NuspecAssertion -Description $name -Body $assertions[$name]
        }

        return $results
    }

    return Invoke-AssertionsInner $Metadata
}
'@ | Set-Content -Path $deepHelperPath -Encoding utf8

. $deepHelperPath
$deepResults = Invoke-AssertionsOuter $realMetadata
foreach ($name in $perTfmDependencyAssertionNames) {
    Assert-True $deepResults[$name].Passed "Shape 3: '$name' executes and PASSES two function levels deep via a second dot-sourced file (message: '$($deepResults[$name].Message)')"
}
$deepAllPass = -not ($deepResults.Values | Where-Object { -not $_.Passed })
Assert-True $deepAllPass "Shape 3: every required assertion PASSES two function levels deep against the real nuspec"

# -----------------------------------------------------------------------------------------------------------
# Real-XML mutation: corrupt one dependency's version range directly in the raw nuspec XML text (not a fixture
# object), reparse, and confirm exactly the corresponding per-TFM assertion fails.
# -----------------------------------------------------------------------------------------------------------
Write-Host ""
Write-Host "-- Real-XML mutation: corrupt net9.0's MongoDB.Driver version range in the raw nuspec text --" -ForegroundColor Cyan
if ($realNuspecText -notmatch '(?s)(<group targetFramework="net9\.0">.*?<dependency id="MongoDB\.Driver" version=")([^"]+)(".*?</group>)') {
    throw "Could not locate the net9.0 group's MongoDB.Driver dependency in the real packed nuspec text -- has the nuspec shape changed?"
}

$mutatedNuspecText = [regex]::Replace(
    $realNuspecText,
    '(?s)(<group targetFramework="net9\.0">.*?<dependency id="MongoDB\.Driver" version=")([^"]+)(".*?</group>)',
    '${1}[9.9.9, 10.0.0)${3}'
)
Assert-True ($mutatedNuspecText -ne $realNuspecText) "The raw nuspec XML text mutation actually changed the text"

[xml]$mutatedNuspec = $mutatedNuspecText
$mutatedMetadata = $mutatedNuspec.package.metadata
$mutatedAssertions = Get-NuspecMetadataAssertions -Metadata $mutatedMetadata
$mutatedResults = [ordered]@{}
foreach ($name in $mutatedAssertions.Keys) {
    $mutatedResults[$name] = Test-NuspecAssertion -Description $name -Body $mutatedAssertions[$name]
}

$mutatedFailingNames = @($mutatedResults.Keys | Where-Object { -not $mutatedResults[$_].Passed }) | Sort-Object
Assert-True (($mutatedFailingNames -join '|') -eq "net9.0 dependency group has exactly the expected package ids and version ranges") `
    "Real-XML mutation: exactly and only the net9.0 per-TFM dependency assertion fails (got: $($mutatedFailingNames -join '; '))"

Remove-Item $OutputDir -Recurse -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------------------------------------------
Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) metadata integration self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All package-metadata integration self-test assertions PASSED." -ForegroundColor Green
exit 0
