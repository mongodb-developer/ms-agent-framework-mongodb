<#
.SYNOPSIS
    Shared, dependency-free release-tag/package-version matching logic for MongoDB.AgentFramework's .nupkg.

.DESCRIPTION
    docs/spec/quality-release.md's tag-triggered release path (`dotnet-v<version>`) must never let a mismatched
    tag/version pair reach upload or attestation: a maintainer accidentally pushing `dotnet-v1.2.3` against a
    .csproj whose <Version> is actually `1.2.4` (or vice versa) would attest/publish provenance for an artifact
    that does not correspond to the ref that triggered the release. Get-NupkgVersion parses the exact <version>
    NuGet itself embedded in the packed .nuspec (not the tracked .csproj source, which could theoretically drift
    from what was actually packed), and Test-ReleaseTagMatchesVersion is the pure, testable comparison used by
    both the real `dotnet-sbom-provenance.yml` workflow (see verify-release-tag.ps1) and
    verify-release-tag.tests.ps1's self-test (exact match, mismatch, pre-release version, and missing-prefix
    fixtures).
#>

<#
.SYNOPSIS
    Reads the <version> element from a packed .nupkg's embedded .nuspec.

.PARAMETER NupkgPath
    Path to a .nupkg file.

.OUTPUTS
    [string] the exact version text NuGet embedded in the .nuspec (e.g. "0.1.0-preview.1").
#>
function Get-NupkgVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$NupkgPath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($NupkgPath)
    try {
        $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -like "*.nuspec" } | Select-Object -First 1
        if (-not $nuspecEntry) {
            throw "No .nuspec entry found in '$NupkgPath'."
        }

        $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
        try {
            $nuspecText = $reader.ReadToEnd()
        }
        finally {
            $reader.Close()
        }
    }
    finally {
        $zip.Dispose()
    }

    [xml]$nuspec = $nuspecText
    $version = $nuspec.package.metadata.version
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "'$NupkgPath''s .nuspec has no <version> value."
    }

    return $version
}

<#
.SYNOPSIS
    Compares a package version against a git ref name for this repository's `dotnet-v<version>` tag convention.

.PARAMETER Version
    The exact package version (e.g. from Get-NupkgVersion), such as "0.1.0-preview.1" or "1.2.3".

.PARAMETER RefName
    The ref name to check against (GitHub Actions' `github.ref_name`: the tag name for a tag push, or the
    branch name for a branch push/workflow_dispatch -- never the full `refs/...` ref).

.OUTPUTS
    [pscustomobject] with: Version, RefName, ExpectedTag ("dotnet-v" + Version), Matches ($RefName -ceq
    ExpectedTag, case-sensitive since git tags are case-sensitive).
#>
function Test-ReleaseTagMatchesVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][AllowEmptyString()][string]$RefName
    )

    $expectedTag = "dotnet-v$Version"
    return [pscustomobject]@{
        Version     = $Version
        RefName     = $RefName
        ExpectedTag = $expectedTag
        Matches     = ($RefName -ceq $expectedTag)
    }
}
