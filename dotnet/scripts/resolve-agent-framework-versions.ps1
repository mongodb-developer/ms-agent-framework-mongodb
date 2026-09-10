#Requires -Version 7.0
<#
.SYNOPSIS
    Resolves common, listed Agent Framework versions from the official NuGet V3 registration API.
#>
[CmdletBinding()]
param(
    [ValidateSet('StablePair', 'StableAndPreview', 'Exact', 'AllDispatch')]
    [string]$Mode = 'StablePair',
    [string]$ExactVersion,
    [string]$SupportedVersionRange,
    [string]$ProjectPath = 'src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj',
    [string]$OutputDirectory = "artifacts/agent-framework-version-resolution",
    [string]$GitHubOutputPath = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AgentFrameworkCompatibility.ps1')

function Get-NuGetRegistrationBaseUrl {
    $service = Invoke-RestMethod -Uri 'https://api.nuget.org/v3/index.json'
    $resource = $service.resources | Where-Object {
        $_.'@type' -like 'RegistrationsBaseUrl/3.6.0*'
    } | Select-Object -First 1
    if (-not $resource) {
        throw 'The official NuGet V3 service index has no RegistrationsBaseUrl/3.6.0 resource.'
    }
    return $resource.'@id'.TrimEnd('/')
}

function Get-ListedPackageVersions {
    param([Parameter(Mandatory)][string]$RegistrationBaseUrl, [Parameter(Mandatory)][string]$PackageId)

    $index = Invoke-RestMethod -Uri "$RegistrationBaseUrl/$($PackageId.ToLowerInvariant())/index.json"
    $leaves = [System.Collections.Generic.List[object]]::new()
    foreach ($page in $index.items) {
        if ($page.items) {
            foreach ($leaf in $page.items) { $leaves.Add($leaf) }
        }
        else {
            $expandedPage = Invoke-RestMethod -Uri $page.'@id'
            foreach ($leaf in $expandedPage.items) { $leaves.Add($leaf) }
        }
    }

    return @($leaves | Where-Object {
        $null -eq $_.catalogEntry.listed -or $_.catalogEntry.listed -eq $true
    } | ForEach-Object {
        [NuGet.Versioning.NuGetVersion]::Parse($_.catalogEntry.version).ToNormalizedString()
    } | Select-Object -Unique)
}

function Get-ProjectAgentFrameworkVersionRange {
    param([Parameter(Mandatory)][string]$Path)

    [xml]$project = Get-Content $Path -Raw
    $range = @($project.Project.PropertyGroup.AgentFrameworkVersion | Where-Object {
        $null -ne $_ -and -not [string]::IsNullOrWhiteSpace($_.InnerText)
    } | ForEach-Object { $_.InnerText.Trim() }) | Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($range)) {
        throw "Project '$Path' does not declare a default <AgentFrameworkVersion> range."
    }

    return $range
}

Import-NuGetVersioning
$dotnetRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$effectiveSupportedVersionRange = $SupportedVersionRange
if ($Mode -eq 'StablePair' -and [string]::IsNullOrWhiteSpace($effectiveSupportedVersionRange)) {
    $effectiveSupportedVersionRange = Get-ProjectAgentFrameworkVersionRange -Path (Join-Path $dotnetRoot $ProjectPath)
}
$registrationBase = Get-NuGetRegistrationBaseUrl
$packageVersions = @{}
foreach ($packageId in @('Microsoft.Agents.AI.Abstractions', 'Microsoft.Agents.AI.Workflows')) {
    $packageVersions[$packageId] = @(Get-ListedPackageVersions -RegistrationBaseUrl $registrationBase -PackageId $packageId)
}

$selection = Select-AgentFrameworkVersions -PackageVersions $packageVersions -Mode $Mode -ExactVersion $ExactVersion `
    -SupportedVersionRange $effectiveSupportedVersionRange
$outputPath = Join-Path $dotnetRoot $OutputDirectory
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$report = [ordered]@{
    resolvedAtUtc     = [DateTimeOffset]::UtcNow.ToString('O')
    source            = 'https://api.nuget.org/v3/index.json'
    packages          = @('Microsoft.Agents.AI.Abstractions', 'Microsoft.Agents.AI.Workflows')
    mode              = $Mode
    supportedRange    = $effectiveSupportedVersionRange
    versions          = @($selection.Versions)
    lowerBoundStable  = $selection.LowerBoundStable
    latestStable      = $selection.LatestStable
    previousStable    = $selection.PreviousStable
    latestPreview     = $selection.LatestPreview
    previewAvailable  = $selection.PreviewAvailable
}
$supportedRangeText = if ([string]::IsNullOrWhiteSpace($effectiveSupportedVersionRange)) {
    'unbounded (all common listed versions)'
}
else {
    $effectiveSupportedVersionRange
}
if ($Mode -eq 'StablePair' -and -not [string]::IsNullOrWhiteSpace($effectiveSupportedVersionRange)) {
    $stableSelectionText = "Supported stable selection: floor `$($selection.LowerBoundStable)`, newest `$($selection.LatestStable)`."
}
else {
    $stableSelectionText = "Stable selection: latest `$($selection.LatestStable)`, previous `$($selection.PreviousStable)`."
}
$report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $outputPath 'resolution.json')

$previewText = if ($selection.PreviewAvailable) { $selection.LatestPreview } else { 'unavailable (no stable substitution)' }
@"
# Agent Framework version resolution

- Source: official NuGet V3 service and registration APIs
- Packages: `Microsoft.Agents.AI.Abstractions`, `Microsoft.Agents.AI.Workflows`
- Mode: `$Mode`
- Supported stable range: `$supportedRangeText`
- $stableSelectionText
- Latest preview: `$previewText`
- Selected: $($selection.Versions -join ', ')
"@ | Set-Content (Join-Path $outputPath 'resolution.md')

if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
    "matrix=$($selection.Versions | ConvertTo-Json -Compress -AsArray)" | Add-Content $GitHubOutputPath
    "preview-available=$($selection.PreviewAvailable.ToString().ToLowerInvariant())" | Add-Content $GitHubOutputPath
}

$report | ConvertTo-Json -Depth 5
