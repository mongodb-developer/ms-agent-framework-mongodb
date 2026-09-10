#Requires -Version 7.0
<#
.SYNOPSIS
    Runs every credential-free test project with a unique TRX and asserts each executed at least one test.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$dotnetRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$testProjects = @(
    [pscustomobject]@{ Name = 'MongoDB.AgentFramework.Tests'; Path = 'tests/MongoDB.AgentFramework.Tests/MongoDB.AgentFramework.Tests.csproj' },
    [pscustomobject]@{ Name = 'IngestionSamples.Tests'; Path = 'tests/IngestionSamples.Tests/IngestionSamples.Tests.csproj' }
)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null

foreach ($project in $testProjects) {
    $trxName = "$($project.Name).trx"
    $args = @('test', (Join-Path $dotnetRoot $project.Path), '--configuration', $Configuration,
        '--logger', "trx;LogFileName=$trxName", '--results-directory', $ResultsDirectory)
    if ($NoBuild) { $args += '--no-build' }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "$($project.Name) tests failed (exit $LASTEXITCODE)." }
    & (Join-Path $PSScriptRoot 'assert-trx-executed.ps1') `
        -TrxPath (Join-Path $ResultsDirectory $trxName) -Label $project.Name
    if ($LASTEXITCODE -ne 0) { throw "$($project.Name) TRX assertion failed." }
}
