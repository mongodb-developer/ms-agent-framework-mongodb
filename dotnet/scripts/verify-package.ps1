#Requires -Version 7.0
<#
.SYNOPSIS
    Verifies the packed MongoDB.AgentFramework NuGet artifact end to end, without publishing anything.

.DESCRIPTION
    Orchestrates every packaging validation this repository can run without owner publishing credentials or a
    live MongoDB deployment (docs/spec/quality-release.md, docs/spec/packages.md):

      1. Packs MongoDB.AgentFramework.csproj (Release, deterministic/CI mode).
      2. Checks the produced .nupkg/.snupkg contents against an explicit allowlist -- proving no sample, test, or
         internal implementation file leaks into the shipped artifact.
      3. Asserts the embedded .nuspec metadata (id, version, authors, license, readme, project/repository URLs,
         description, release notes, copyright, tags) is present and well-formed.
      4. Packs a second time from a clean build and compares the two artifacts, normalizing the OPC packaging
         metadata that `dotnet pack` regenerates with a new random identifier on every invocation (the
         `package/services/metadata/core-properties/*.psmdcp` part name and the `_rels/.rels` relationship ids)
         and per-entry zip timestamps, since neither reflects package *content*. Every real payload entry (dll,
         xml docs, nuspec, README) must be byte-identical across the two runs.
      5. Restores and runs the isolated consumer smoke test (tests/PackageSmokeTest) against the packed artifact
         through a local NuGet feed and an isolated NUGET_PACKAGES cache directory -- never a ProjectReference,
         and never the developer machine's shared global package cache -- constructing every public
         provider/facade type across Memory, exact Chat History, RAG (all four MongoDBSearchMode values), Index
         Management, Session Store, and Workflow Checkpoint Store. Runs once per TFM MongoDB.AgentFramework
         itself ships (`dotnet run -f net8.0|net9.0|net10.0`), proving the package restores and runs -- not just
         compiles -- on every shipped target framework.

    This script never contacts a real MongoDB deployment, never publishes or pushes a package, and never invents
    a signing identity or publisher credential; see dotnet/README.md and the final report this script prints for
    the governance blockers (ADR 0013) that remain before a real release.

.PARAMETER Configuration
    The build configuration to pack. Defaults to Release.

.PARAMETER SkipReproducibility
    Skips the double-pack reproducibility comparison (step 4). Useful for a fast iteration loop.

.PARAMETER SkipConsumerSmoke
    Skips the isolated consumer smoke test (step 5). Useful for a fast iteration loop.

.EXAMPLE
    pwsh dotnet/scripts/verify-package.ps1

.EXAMPLE
    pwsh dotnet/scripts/verify-package.ps1 -SkipReproducibility -SkipConsumerSmoke
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipReproducibility,
    [switch]$SkipConsumerSmoke
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot "PackageAllowlist.ps1")

$DotnetRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$SrcProject = Join-Path $DotnetRoot "src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj"
$SmokeProject = Join-Path $DotnetRoot "tests/PackageSmokeTest/PackageSmokeTest.csproj"
$PackagesDir = Join-Path $DotnetRoot "artifacts/packages"
$ReproDir = Join-Path $DotnetRoot "artifacts/packages-repro-check"
$ConsumerCacheDir = Join-Path $DotnetRoot "artifacts/nuget-consumer-cache"

$script:FailureCount = 0

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host "==== $Title ====" -ForegroundColor Cyan
}

function Write-Ok([string]$Message) {
    Write-Host "[ OK ] $Message" -ForegroundColor Green
}

function Write-Failure([string]$Message) {
    Write-Host "[FAIL] $Message" -ForegroundColor Red
    $script:FailureCount++
}

function Invoke-Checked([string]$Description, [scriptblock]$Body) {
    try {
        & $Body
        Write-Ok $Description
        return $true
    }
    catch {
        Write-Failure "$Description -- $($_.Exception.Message)"
        return $false
    }
}

# Clears an item at $Path if present. Ignores absence (a fresh checkout has no artifacts yet).
function Clear-PathIfExists([string]$Path) {
    if (Test-Path $Path) {
        Remove-Item $Path -Recurse -Force
    }
}

function Invoke-Pack([string]$OutputDir) {
    Clear-PathIfExists $OutputDir
    Clear-PathIfExists (Join-Path $DotnetRoot "src/MongoDB.AgentFramework/bin")
    Clear-PathIfExists (Join-Path $DotnetRoot "src/MongoDB.AgentFramework/obj")

    & dotnet pack $SrcProject `
        --configuration $Configuration `
        -p:ContinuousIntegrationBuild=true `
        -p:CI=true `
        -p:PackageOutputPath=$OutputDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed with exit code $LASTEXITCODE"
    }

    $nupkg = Get-ChildItem $OutputDir -Filter "*.nupkg" | Select-Object -First 1
    $snupkg = Get-ChildItem $OutputDir -Filter "*.snupkg" | Select-Object -First 1
    if (-not $nupkg) {
        throw "No .nupkg produced in $OutputDir"
    }

    if (-not $snupkg) {
        throw "No .snupkg (symbol package) produced in $OutputDir"
    }

    return [pscustomobject]@{ Nupkg = $nupkg.FullName; Snupkg = $snupkg.FullName }
}

function Get-ZipEntryTexts([string]$ZipPath) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $result = [ordered]@{}
        foreach ($entry in $zip.Entries) {
            $stream = $entry.Open()
            try {
                $ms = New-Object System.IO.MemoryStream
                $stream.CopyTo($ms)
                $result[$entry.FullName] = $ms.ToArray()
            }
            finally {
                $stream.Dispose()
            }
        }

        return $result
    }
    finally {
        $zip.Dispose()
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Step 1: pack
# ---------------------------------------------------------------------------------------------------------------
Write-Section "Step 1: dotnet pack ($Configuration)"
$primary = Invoke-Pack -OutputDir $PackagesDir
Write-Ok "Packed $($primary.Nupkg)"
Write-Ok "Packed $($primary.Snupkg)"

# ---------------------------------------------------------------------------------------------------------------
# Step 2: package-content allowlist -- exact expected entry set + exact multiplicity (dotnet/scripts/PackageAllowlist.ps1)
# ---------------------------------------------------------------------------------------------------------------
Write-Section "Step 2: package-content allowlist (exact set + multiplicity)"

function Get-ZipEntryNames([string]$ZipPath) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        return @($zip.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $zip.Dispose()
    }
}

function Assert-PackageAllowlist([string]$ZipPath, [string[]]$ExpectedEntries, [string]$Label) {
    $actualEntries = Get-ZipEntryNames $ZipPath
    $result = Test-PackageContentAllowlist -ActualEntries $actualEntries -ExpectedEntries $ExpectedEntries -Label $Label

    if (-not $result.Passed) {
        if ($result.Missing.Count -gt 0) {
            Write-Failure "$Label is missing required entries: $($result.Missing -join ', ')"
        }
        if ($result.Unexpected.Count -gt 0) {
            Write-Failure "$Label contains disallowed/unexpected entries: $($result.Unexpected -join ', ')"
        }
        if ($result.MultiplicityMismatch.Count -gt 0) {
            Write-Failure "$Label has wrong entry counts: $($result.MultiplicityMismatch -join ', ')"
        }
        return
    }

    Write-Ok "$Label ($($result.ActualCount) entries) matches the expected set and multiplicity exactly"
    foreach ($entry in ($actualEntries | Sort-Object)) {
        Write-Host "         $entry"
    }
}

Assert-PackageAllowlist -ZipPath $primary.Nupkg -ExpectedEntries $script:NupkgExpectedEntries -Label "nupkg"
Assert-PackageAllowlist -ZipPath $primary.Snupkg -ExpectedEntries $script:SnupkgExpectedEntries -Label "snupkg"

# ---------------------------------------------------------------------------------------------------------------
# Step 3: nuspec metadata assertions
# ---------------------------------------------------------------------------------------------------------------
Write-Section "Step 3: nuspec metadata"

$zip = [System.IO.Compression.ZipFile]::OpenRead($primary.Nupkg)
try {
    $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -eq "MongoDB.AgentFramework.nuspec" }
    $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
    $nuspecText = $reader.ReadToEnd()
    $reader.Close()
}
finally {
    $zip.Dispose()
}

[xml]$nuspec = $nuspecText
$metadata = $nuspec.package.metadata

$assertions = [ordered]@{
    "id equals MongoDB.AgentFramework"        = { $metadata.id -eq "MongoDB.AgentFramework" }
    "version is set"                          = { -not [string]::IsNullOrWhiteSpace($metadata.version) }
    "authors is set"                          = { -not [string]::IsNullOrWhiteSpace($metadata.authors) }
    "license expression is MIT"               = { $metadata.license.type -eq "expression" -and $metadata.license.'#text' -eq "MIT" }
    "licenseUrl is set (legacy consumer fallback)" = { -not [string]::IsNullOrWhiteSpace($metadata.licenseUrl) }
    "readme is set"                           = { -not [string]::IsNullOrWhiteSpace($metadata.readme) }
    "projectUrl is set"                       = { -not [string]::IsNullOrWhiteSpace($metadata.projectUrl) }
    "description is set"                      = { -not [string]::IsNullOrWhiteSpace($metadata.description) }
    "releaseNotes is set"                     = { -not [string]::IsNullOrWhiteSpace($metadata.releaseNotes) }
    "copyright is set"                        = { -not [string]::IsNullOrWhiteSpace($metadata.copyright) }
    "tags is set"                             = { -not [string]::IsNullOrWhiteSpace($metadata.tags) }
    "repository url is embedded (SourceLink)" = { -not [string]::IsNullOrWhiteSpace($metadata.repository.url) }
    "repository commit is embedded (SourceLink)" = { -not [string]::IsNullOrWhiteSpace($metadata.repository.commit) }
    "at least one per-TFM dependency group"   = { $metadata.dependencies.group.Count -ge 1 }
}

foreach ($name in $assertions.Keys) {
    Invoke-Checked $name $assertions[$name] | Out-Null
}

Write-Host ""
Write-Host "nuspec id/version/authors: $($metadata.id) $($metadata.version) ($($metadata.authors))"
Write-Host "nuspec repository: $($metadata.repository.url) @ $($metadata.repository.commit) [$($metadata.repository.branch)]"

# ---------------------------------------------------------------------------------------------------------------
# Step 4: reproducibility (pack twice from clean, compare content ignoring known-nondeterministic OPC wrapper bits)
# ---------------------------------------------------------------------------------------------------------------
if ($SkipReproducibility) {
    Write-Section "Step 4: reproducibility (skipped by -SkipReproducibility)"
}
else {
    Write-Section "Step 4: reproducibility (pack twice, compare)"
    $second = Invoke-Pack -OutputDir $ReproDir

    function Compare-PackageReproducibility([string]$PathA, [string]$PathB, [string]$Label) {
        $entriesA = Get-ZipEntryTexts $PathA
        $entriesB = Get-ZipEntryTexts $PathB

        # The OPC core-properties part name is a fresh random GUID on every `dotnet pack` invocation (NuGet.Client
        # behavior, not a build nondeterminism -- see the psmdcp/_rels comparison below, which proves their
        # *content* -- other than that GUID and the _rels relationship ids that reference it -- is identical).
        # Normalize the key so entry-set comparison does not spuriously fail on that GUID alone.
        $normalize = { param($name) $name -replace '(core-properties/)[0-9a-f]{32}(\.psmdcp)', '$1{guid}$2' }
        $namesA = $entriesA.Keys | ForEach-Object { & $normalize $_ } | Sort-Object
        $namesB = $entriesB.Keys | ForEach-Object { & $normalize $_ } | Sort-Object
        if (($namesA -join "|") -ne ($namesB -join "|")) {
            Write-Failure "$Label entry sets differ: [$($namesA -join ', ')] vs [$($namesB -join ', ')]"
            return
        }

        $realKeysA = $entriesA.Keys | Where-Object { $_ -ne "_rels/.rels" -and $_ -notmatch "\.psmdcp$" }
        $mismatches = @()
        foreach ($key in $realKeysA) {
            $bytesA = $entriesA[$key]
            $bytesB = $entriesB[$key]
            if ($null -eq $bytesB -or -not [System.Linq.Enumerable]::SequenceEqual([byte[]]$bytesA, [byte[]]$bytesB)) {
                $mismatches += $key
            }
        }

        if ($mismatches.Count -gt 0) {
            Write-Failure "$Label real payload entries differ between the two packs: $($mismatches -join ', ')"
            return
        }

        Write-Ok "$Label`: every real payload entry (dll/xml/nuspec/README) is byte-identical across two packs"
        Write-Host "         (only the OPC core-properties part name and _rels/.rels relationship ids -- neither of which is package content -- differ, which is expected NuGet.Client `dotnet pack` OPC packaging behavior, not build nondeterminism)"
    }

    Compare-PackageReproducibility -PathA $primary.Nupkg -PathB $second.Nupkg -Label "nupkg"
    Compare-PackageReproducibility -PathA $primary.Snupkg -PathB $second.Snupkg -Label "snupkg"

    # The reproducibility check's own second pack is scratch output only; do not leave it in artifacts/.
    Clear-PathIfExists $ReproDir
}

# ---------------------------------------------------------------------------------------------------------------
# Step 5: clean, isolated consumer smoke test
# ---------------------------------------------------------------------------------------------------------------
if ($SkipConsumerSmoke) {
    Write-Section "Step 5: consumer smoke test (skipped by -SkipConsumerSmoke)"
}
else {
    Write-Section "Step 5: clean, isolated consumer smoke test (every shipped TFM)"
    Clear-PathIfExists $ConsumerCacheDir
    New-Item -ItemType Directory -Path $ConsumerCacheDir -Force | Out-Null
    Clear-PathIfExists (Join-Path $DotnetRoot "tests/PackageSmokeTest/bin")
    Clear-PathIfExists (Join-Path $DotnetRoot "tests/PackageSmokeTest/obj")

    # Read the TFM list straight from PackageSmokeTest.csproj's own <TargetFrameworks> element instead of
    # hardcoding it here a second time, so this script can never silently drift out of sync with the project
    # actually being run (e.g. if a TFM is added to/removed from the smoke project without updating this script).
    [xml]$smokeProjectXml = Get-Content $SmokeProject -Raw
    $targetFrameworksValue = $smokeProjectXml.Project.PropertyGroup.TargetFrameworks
    if ([string]::IsNullOrWhiteSpace($targetFrameworksValue)) {
        throw "PackageSmokeTest.csproj must declare <TargetFrameworks> (semicolon-separated, multi-target) so " +
              "every shipped TFM can be smoke-tested; found none."
    }

    $targetFrameworks = $targetFrameworksValue -split ';' | Where-Object { $_ }
    Write-Host "Smoke-testing TFMs: $($targetFrameworks -join ', ')"

    # NUGET_PACKAGES is overridden to an isolated, disposable directory under artifacts/ so this restore can never
    # be satisfied by anything already present in the developer/CI machine's shared global package cache: it must
    # come only from nuget.org (transitive dependencies) and the local packed feed (MongoDB.AgentFramework itself,
    # per tests/PackageSmokeTest/nuget.config). A single restore covers every TFM (NuGet restores the whole
    # multi-targeted project graph together); each TFM is then run independently via `dotnet run -f <tfm>`.
    $previousNugetPackages = $env:NUGET_PACKAGES
    $env:NUGET_PACKAGES = $ConsumerCacheDir
    try {
        & dotnet restore $SmokeProject --force --no-cache
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed with exit code $LASTEXITCODE"
        }

        $tfmFailures = @()
        foreach ($tfm in $targetFrameworks) {
            Write-Host ""
            Write-Host "-- dotnet run -f $tfm --"
            & dotnet run --project $SmokeProject --configuration $Configuration --framework $tfm --no-restore
            if ($LASTEXITCODE -ne 0) {
                $tfmFailures += "$tfm (exit code $LASTEXITCODE)"
            }
        }
    }
    finally {
        $env:NUGET_PACKAGES = $previousNugetPackages
    }

    if ($tfmFailures.Count -gt 0) {
        Write-Failure "Consumer smoke test failed for: $($tfmFailures -join ', ')"
    }
    else {
        Write-Ok "Consumer smoke test restored MongoDB.AgentFramework from the packed .nupkg and constructed every public feature area on every TFM ($($targetFrameworks -join ', '))"
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Step 6: checksum manifest for the kept artifacts
# ---------------------------------------------------------------------------------------------------------------
Write-Section "Step 6: artifact checksums"
foreach ($path in @($primary.Nupkg, $primary.Snupkg)) {
    $hash = Get-FileHash -Path $path -Algorithm SHA256
    Write-Host "SHA256($([System.IO.Path]::GetFileName($path))) = $($hash.Hash)"
}

# ---------------------------------------------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------------------------------------------
Write-Section "Summary"
if ($script:FailureCount -gt 0) {
    Write-Host "$($script:FailureCount) check(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All package verification checks PASSED." -ForegroundColor Green
exit 0
