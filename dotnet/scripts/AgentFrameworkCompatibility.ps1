<#
.SYNOPSIS
    NuGet-aware version selection shared by Agent Framework compatibility automation and its self-tests.
#>

function Import-NuGetVersioning {
    if ("NuGet.Versioning.NuGetVersion" -as [type]) {
        return
    }

    $sdk = (& dotnet --list-sdks | Select-Object -Last 1)
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^(?<version>[^\s]+)\s+\[(?<path>.+)\]$') {
        throw "Unable to locate the .NET SDK's NuGet.Versioning assembly."
    }

    $assembly = Join-Path $Matches.path "$($Matches.version)/NuGet.Versioning.dll"
    if (-not (Test-Path $assembly)) {
        throw "NuGet.Versioning.dll was not found at '$assembly'."
    }
    Add-Type -Path $assembly
}

function Sort-NuGetVersions {
    param([Parameter(Mandatory)][string[]]$Versions)

    Import-NuGetVersioning
    $parsed = [NuGet.Versioning.NuGetVersion[]]@($Versions | ForEach-Object {
        [NuGet.Versioning.NuGetVersion]::Parse($_)
    })
    [Array]::Sort($parsed, [NuGet.Versioning.VersionComparer]::VersionRelease)
    return @($parsed | ForEach-Object { $_.ToNormalizedString() })
}

function Select-AgentFrameworkVersions {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$PackageVersions,
        [ValidateSet('StablePair', 'StableAndPreview', 'Exact', 'AllDispatch')]
        [string]$Mode,
        [string]$ExactVersion
    )

    $packageIds = @('Microsoft.Agents.AI.Abstractions', 'Microsoft.Agents.AI.Workflows')
    foreach ($id in $packageIds) {
        if (-not $PackageVersions.ContainsKey($id)) {
            throw "Version data for '$id' is missing."
        }
    }

    $common = @($PackageVersions[$packageIds[0]] | Where-Object {
        $PackageVersions[$packageIds[1]] -ccontains $_
    })
    if ($common.Count -eq 0) {
        throw "The two Agent Framework packages have no common listed versions."
    }

    $ordered = @(Sort-NuGetVersions -Versions $common)
    $stable = @($ordered | Where-Object {
        -not [NuGet.Versioning.NuGetVersion]::Parse($_).IsPrerelease
    })
    $preview = @($ordered | Where-Object {
        [NuGet.Versioning.NuGetVersion]::Parse($_).IsPrerelease
    })

    if ($stable.Count -eq 0) {
        throw "No common listed stable Agent Framework version exists."
    }

    $selected = [System.Collections.Generic.List[string]]::new()
    if ($Mode -in @('StablePair', 'StableAndPreview', 'AllDispatch')) {
        if ($Mode -eq 'StablePair' -and $stable.Count -lt 2) {
            throw "At least two common listed stable versions are required for the latest/previous gate."
        }
        if ($Mode -eq 'StablePair') {
            $selected.Add($stable[-2])
        }
        $selected.Add($stable[-1])
    }

    if ($Mode -in @('StableAndPreview', 'AllDispatch')) {
        if ($preview.Count -gt 0) {
            $selected.Add($preview[-1])
        }
    }

    if ($Mode -in @('Exact', 'AllDispatch') -and -not [string]::IsNullOrWhiteSpace($ExactVersion)) {
        $normalizedExact = [NuGet.Versioning.NuGetVersion]::Parse($ExactVersion).ToNormalizedString()
        if ($ordered -cnotcontains $normalizedExact) {
            throw "Exact version '$ExactVersion' is not a common listed version of both Agent Framework packages."
        }
        $selected.Add($normalizedExact)
    }
    elseif ($Mode -eq 'Exact') {
        throw "Exact mode requires -ExactVersion."
    }

    return [pscustomobject]@{
        Versions         = @($selected | Select-Object -Unique)
        LatestStable     = $stable[-1]
        PreviousStable   = if ($stable.Count -gt 1) { $stable[-2] } else { $null }
        LatestPreview    = if ($preview.Count -gt 0) { $preview[-1] } else { $null }
        PreviewAvailable = ($preview.Count -gt 0)
    }
}
