<#
.SYNOPSIS
    Pure release-readiness decisions shared by build readiness, release coordination, and self-tests.
#>
. (Join-Path $PSScriptRoot 'AgentFrameworkCompatibility.ps1')
. (Join-Path $PSScriptRoot 'ReleaseVersionTag.ps1')

function Get-CanonicalNuGetVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Version)

    Import-NuGetVersioning
    $parsed = [NuGet.Versioning.NuGetVersion]::Parse($Version)
    $canonical = $parsed.ToNormalizedString()
    if ($canonical -cne $Version) {
        throw "Version '$Version' is not canonical NuGet SemVer; canonical form is '$canonical'."
    }
    return $canonical
}

function Get-CanonicalDotNetReleaseTag {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Version)

    $canonical = Get-CanonicalNuGetVersion $Version
    $tag = "dotnet-v$canonical"
    if (-not (Test-ValidReleaseTagGrammar $tag)) {
        throw "Canonical NuGet version '$canonical' cannot form an approved dotnet-v release tag."
    }
    return $tag
}

function Get-ReleaseTagDisposition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ExpectedSha,
        [AllowEmptyString()][string]$ExistingTagSha
    )

    if ([string]::IsNullOrWhiteSpace($ExistingTagSha)) {
        return 'create'
    }
    if ($ExistingTagSha -ceq $ExpectedSha) {
        return 'already-exact'
    }
    return 'conflict'
}
