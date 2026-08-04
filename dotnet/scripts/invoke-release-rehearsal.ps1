#Requires -Version 7.0
<#
.SYNOPSIS
    Runs the complete local, non-publishing MongoDB.AgentFramework release rehearsal.
.DESCRIPTION
    Restores, formats, builds, tests, dynamically checks current/previous Agent Framework compatibility, fully
    verifies package metadata/content/reproducibility and the local-feed consumer, then writes reports/checksums.
    This script never creates or pushes a tag and never invokes NuGet publication.
#>
[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$dotnetRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$solution = Join-Path $dotnetRoot 'MongoDB.AgentFramework.slnx'
$results = Join-Path $dotnetRoot 'artifacts/release-rehearsal'
if (Test-Path $results) { Remove-Item $results -Recurse -Force }
New-Item -ItemType Directory -Path $results -Force | Out-Null

function Invoke-Checked([scriptblock]$Command, [string]$Description) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
}

Push-Location $dotnetRoot
try {
    Invoke-Checked { dotnet restore $solution } 'Restore'
    Invoke-Checked { dotnet format $solution --verify-no-changes --verbosity minimal } 'Formatting validation'
    Invoke-Checked { dotnet build $solution --configuration $Configuration --no-restore } 'Build'
    Invoke-Checked {
        dotnet test $solution --configuration $Configuration --no-build `
            --logger 'trx;LogFileName=release-rehearsal.trx' --results-directory $results
    } 'Tests'

    & (Join-Path $PSScriptRoot 'assert-trx-executed.ps1') `
        -TrxPath (Join-Path $results 'release-rehearsal.trx') -Label 'release rehearsal'
    if ($LASTEXITCODE -ne 0) { throw 'TRX execution assertion failed.' }

    & (Join-Path $PSScriptRoot 'resolve-agent-framework-versions.ps1') -Mode StablePair
    if ($LASTEXITCODE -ne 0) { throw 'Agent Framework version resolution failed.' }
    $resolution = Get-Content (Join-Path $dotnetRoot 'artifacts/agent-framework-version-resolution/resolution.json') -Raw |
        ConvertFrom-Json
    $compatibilityVersions = @($resolution.versions)
    & (Join-Path $PSScriptRoot 'verify-agent-framework-compatibility.ps1') `
        -Configuration $Configuration -Versions $compatibilityVersions
    if ($LASTEXITCODE -ne 0) { throw 'Agent Framework compatibility validation failed.' }

    & (Join-Path $PSScriptRoot 'verify-package.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Package verification failed.' }

    Get-ChildItem (Join-Path $dotnetRoot 'artifacts/packages') -File |
        Where-Object { $_.Extension -in @('.nupkg', '.snupkg') } |
        ForEach-Object {
            $hash = Get-FileHash $_.FullName -Algorithm SHA256
            "$($hash.Hash)  $($_.Name)"
        } | Set-Content (Join-Path $results 'checksums.sha256.txt')

    @"
# Local .NET release rehearsal

- Result: passed
- Configuration: $Configuration
- Agent Framework exact versions: $($resolution.versions -join ', ')
- Package checks: metadata, content allowlist, reproducibility, and local-feed consumer smoke passed
- Publication: not attempted; this command has no tag, push, or NuGet publication operation
"@ | Set-Content (Join-Path $results 'release-rehearsal.md')
}
finally {
    Pop-Location
}

Write-Host "Release rehearsal PASSED. Reports: $results" -ForegroundColor Green
