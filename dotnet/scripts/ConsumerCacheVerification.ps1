<#
.SYNOPSIS
    Shared, dependency-free logic proving PackageSmokeTest's isolated restore actually resolved
    MongoDB.AgentFramework from the just-packed local artifact -- not a same-named/same-version package that
    happened to be found on nuget.org, and not a stale copy left over from a previous run's package cache.

.DESCRIPTION
    tests/PackageSmokeTest/nuget.config restricts MongoDB.AgentFramework to the local packed feed via
    packageSourceMapping (every other package is restricted to nuget.org), and verify-package.ps1 restores into
    a freshly-cleared, isolated NUGET_PACKAGES directory so no pre-existing global cache entry can mask a broken
    source restriction. Neither of those is directly falsifiable from the restore's exit code alone: a
    misconfigured packageSourceMapping that let MongoDB.AgentFramework fall through to nuget.org would still
    "succeed" if nuget.org ever published a package under this id (or, during local development, if a stale
    cache entry happened to satisfy the version constraint). The one thing that cannot be spoofed by a wrong
    source is the exact byte content: NuGet records the SHA512 (base64) of the exact .nupkg it restored a
    package from directly in the consuming project's restored project.assets.json. Comparing that recorded
    hash against a fresh hash of the actual locally-packed .nupkg on disk is a content-addressed proof that the
    restored library came from that specific file, not merely a package with a matching id/version string.
#>

<#
.SYNOPSIS
    Computes the base64-encoded SHA512 hash of a file, in the same format NuGet embeds in project.assets.json's
    per-library "sha512" field (and in a package folder's "<id>.<version>.nupkg.sha512" file).
#>
function Get-Sha512Base64 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath
    )

    if (-not (Test-Path $FilePath)) {
        throw "Cannot compute a hash: '$FilePath' does not exist."
    }

    $sha512 = [System.Security.Cryptography.SHA512]::Create()
    try {
        $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $FilePath).ProviderPath)
        $hashBytes = $sha512.ComputeHash($bytes)
        return [Convert]::ToBase64String($hashBytes)
    }
    finally {
        $sha512.Dispose()
    }
}

<#
.SYNOPSIS
    Reads a single package's restored library entry (type/sha512/path) out of a parsed project.assets.json.

.OUTPUTS
    [pscustomobject] with Type/Sha512/Path, or $null if the "<id>/<version>" key is not present in "libraries".
#>
function Get-ProjectAssetsLibraryEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$ProjectAssets,
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$PackageVersion
    )

    $key = "$PackageId/$PackageVersion"
    $libraries = $ProjectAssets.libraries
    if ($null -eq $libraries) {
        return $null
    }

    $entry = $libraries.PSObject.Properties | Where-Object { $_.Name -eq $key } | Select-Object -First 1
    if ($null -eq $entry) {
        return $null
    }

    return [pscustomobject]@{
        Type   = $entry.Value.type
        Sha512 = $entry.Value.sha512
        Path   = $entry.Value.path
    }
}

<#
.SYNOPSIS
    Strict-boolean proof that a restored project.assets.json's MongoDB.AgentFramework library entry is a
    "package"-type dependency whose recorded content hash matches the exact locally-packed .nupkg on disk.

.OUTPUTS
    [bool]
#>
function Test-ConsumerCacheResolvedPackedPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$ProjectAssets,
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$PackageVersion,
        [Parameter(Mandatory)][string]$NupkgPath
    )

    $library = Get-ProjectAssetsLibraryEntry -ProjectAssets $ProjectAssets -PackageId $PackageId -PackageVersion $PackageVersion
    if ($null -eq $library) {
        return $false
    }

    if ($library.Type -ne "package") {
        return $false
    }

    if ([string]::IsNullOrWhiteSpace($library.Sha512)) {
        return $false
    }

    $expectedHash = Get-Sha512Base64 -FilePath $NupkgPath
    return ($library.Sha512 -ceq $expectedHash)
}
