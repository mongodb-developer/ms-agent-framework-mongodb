#Requires -Version 7.0
<#
.SYNOPSIS
    Validates .NET manifest/package/tag readiness without creating a tag or publishing.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Remote = 'origin'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseReadiness.ps1')

$dotnetRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$projectPath = Join-Path $dotnetRoot 'src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj'
$reportDir = Join-Path $dotnetRoot 'artifacts/build-release-readiness'
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null

$result = [ordered]@{
    status = 'failed'
    version = $null
    expectedTag = $null
    canonical = $false
    packageValidated = $false
    remoteTagConflict = $null
    tagged = $false
    published = $false
    error = $null
}
function Write-ReadinessReport {
    $result | ConvertTo-Json | Set-Content (Join-Path $reportDir 'build-release-readiness.json')
    @"
# .NET build release readiness

- Status: $($result.status)
- Manifest version: $($result.version)
- Expected tag: $($result.expectedTag)
- Canonical NuGet SemVer: $($result.canonical)
- Package metadata/content and tag agreement: $($result.packageValidated)
- Conflicting remote tag: $($result.remoteTagConflict)
- Tagging/publication: not attempted; build branches have no release authority
- Error: $($result.error)
"@ | Set-Content (Join-Path $reportDir 'build-release-readiness.md')
}

try {
    [xml]$project = Get-Content $projectPath -Raw
    $version = Get-CanonicalNuGetVersion ([string]$project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText)
    $expectedTag = Get-CanonicalDotNetReleaseTag $version
    $result.version = $version
    $result.expectedTag = $expectedTag
    $result.canonical = $true

    & (Join-Path $PSScriptRoot 'verify-package.ps1') -Configuration $Configuration `
        -SkipReproducibility -SkipConsumerSmoke
    if ($LASTEXITCODE -ne 0) { throw 'Package metadata/content validation failed.' }

    $nupkgs = @(Get-ChildItem (Join-Path $dotnetRoot 'artifacts/packages') -Filter '*.nupkg' |
        Where-Object { $_.Name -notlike '*.snupkg' })
    if ($nupkgs.Count -ne 1) { throw "Expected exactly one nupkg; found $($nupkgs.Count)." }
    & (Join-Path $PSScriptRoot 'verify-release-tag.ps1') `
        -NupkgPath $nupkgs[0].FullName -RefName $expectedTag -EnforceMatch
    if ($LASTEXITCODE -ne 0) { throw "Packed package does not agree with expected tag '$expectedTag'." }
    $result.packageValidated = $true

    $remoteResult = @(& git -C $dotnetRoot ls-remote --tags --refs $Remote "refs/tags/$expectedTag")
    if ($LASTEXITCODE -ne 0) { throw "Unable to query '$Remote' for release tag conflicts." }
    $result.remoteTagConflict = $remoteResult.Count -gt 0
    if ($result.remoteTagConflict) {
        throw "Remote tag '$expectedTag' already exists; increment the manifest version before build-branch promotion."
    }
    $result.status = 'passed'
    Write-ReadinessReport
}
catch {
    $result.error = $_.Exception.Message
    Write-ReadinessReport
    throw
}
