#Requires -Version 7.0
<#
.SYNOPSIS
    Verifies all credential-free .NET tests, package build, and local consumer against exact Agent Framework versions.
.DESCRIPTION
    Every requested version receives exactly one report row, even when restore, build, dependency validation,
    tests, package creation, or consumer smoke fails. TRX, JSON, and Markdown evidence is retained under
    artifacts/agent-framework-compat-results. The tracked [1.13.0,1.17.0) range is never edited or widened.
#>
[CmdletBinding()]
param(
    [string[]]$Versions = @('1.13.0', '1.16.0'),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TrxResults.ps1')

$dotnetRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$srcProject = Join-Path $dotnetRoot 'src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj'
$smokeProject = Join-Path $dotnetRoot 'tests/PackageSmokeTest/PackageSmokeTest.csproj'
$resultsDir = Join-Path $dotnetRoot 'artifacts/agent-framework-compat-results'
$packagesDir = Join-Path $dotnetRoot 'artifacts/packages'
$testProjects = @(
    [pscustomobject]@{ Name = 'MongoDB.AgentFramework.Tests'; Path = Join-Path $dotnetRoot 'tests/MongoDB.AgentFramework.Tests/MongoDB.AgentFramework.Tests.csproj' },
    [pscustomobject]@{ Name = 'IngestionSamples.Tests'; Path = Join-Path $dotnetRoot 'tests/IngestionSamples.Tests/IngestionSamples.Tests.csproj' }
)

function Clear-PathIfExists([string]$Path) {
    if (Test-Path $Path) { Remove-Item $Path -Recurse -Force }
}

function Clear-ProjectOutputs {
    foreach ($relative in @(
        'src/MongoDB.AgentFramework/bin', 'src/MongoDB.AgentFramework/obj',
        'samples/IngestionSamples/bin', 'samples/IngestionSamples/obj',
        'tests/MongoDB.AgentFramework.Tests/bin', 'tests/MongoDB.AgentFramework.Tests/obj',
        'tests/IngestionSamples.Tests/bin', 'tests/IngestionSamples.Tests/obj',
        'tests/PackageSmokeTest/bin', 'tests/PackageSmokeTest/obj'
    )) {
        Clear-PathIfExists (Join-Path $dotnetRoot $relative)
    }
}

function Get-ResolvedPackageVersion([string]$AssetsPath, [string]$PackageId) {
    $assets = Get-Content $AssetsPath -Raw | ConvertFrom-Json
    $key = $assets.libraries.PSObject.Properties.Name |
        Where-Object { $_ -like "$PackageId/*" } | Select-Object -First 1
    if (-not $key) { return $null }
    return ($key -split '/', 2)[1]
}

function Write-Reports([System.Collections.Generic.List[object]]$Rows) {
    @($Rows) | ConvertTo-Json -Depth 6 -AsArray | Set-Content (Join-Path $resultsDir 'compatibility-report.json')
    $markdown = $Rows | ForEach-Object {
        "| $($_.version) | $($_.result) | $($_.stage) | $($_.executed) | $($_.consumerSmoke) | $($_.message -replace '\|','\\|') |"
    }
    @"
# MongoDB.AgentFramework compatibility report

The package range remains `[1.13.0,1.17.0)`. These rows are drift/test evidence only and do not widen it.

| Exact version | Result | Completed stage | Tests executed | Consumer smoke | Detail |
| --- | --- | --- | ---: | --- | --- |
$($markdown -join [Environment]::NewLine)
"@ | Set-Content (Join-Path $resultsDir 'compatibility-report.md')
}

Clear-PathIfExists $resultsDir
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
$rows = [System.Collections.Generic.List[object]]::new()
$failureCount = 0

foreach ($version in $Versions) {
    Write-Host "`n==== Agent Framework compatibility: $version ====" -ForegroundColor Cyan
    $row = [ordered]@{
        version = $version; result = 'failed'; stage = 'not-started'; executed = 0
        consumerSmoke = $false; message = 'Validation did not complete.'
    }
    $versionFailed = $false
    try {
        Clear-ProjectOutputs

        $row.stage = 'restore'
        foreach ($project in $testProjects) {
            & dotnet restore $project.Path "-p:AgentFrameworkVersion=$version"
            if ($LASTEXITCODE -ne 0) { throw "Restore failed for $($project.Name) (exit $LASTEXITCODE)." }
        }

        $row.stage = 'build'
        & dotnet build $srcProject --configuration $Configuration "-p:AgentFrameworkVersion=$version" --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Package source build failed (exit $LASTEXITCODE)." }
        foreach ($project in $testProjects) {
            & dotnet build $project.Path --configuration $Configuration "-p:AgentFrameworkVersion=$version" --no-restore
            if ($LASTEXITCODE -ne 0) { throw "Build failed for $($project.Name) (exit $LASTEXITCODE)." }
        }

        $row.stage = 'dependency-validation'
        $assetsPath = Join-Path $dotnetRoot 'src/MongoDB.AgentFramework/obj/project.assets.json'
        foreach ($packageId in @('Microsoft.Agents.AI.Abstractions', 'Microsoft.Agents.AI.Workflows')) {
            $actual = Get-ResolvedPackageVersion -AssetsPath $assetsPath -PackageId $packageId
            if ($actual -cne $version) {
                throw "$packageId resolved to '$actual', expected exactly '$version'."
            }
        }

        foreach ($project in $testProjects) {
            $row.stage = "tests:$($project.Name)"
            $projectResults = Join-Path $resultsDir "$version/$($project.Name)"
            New-Item -ItemType Directory -Path $projectResults -Force | Out-Null
            $trxName = "$($project.Name)-$version.trx"
            $trxPath = Join-Path $projectResults $trxName
            & dotnet test $project.Path --configuration $Configuration "-p:AgentFrameworkVersion=$version" `
                --no-build --no-restore --logger "trx;LogFileName=$trxName" --results-directory $projectResults
            $testExit = $LASTEXITCODE
            $executed = Get-TrxExecutedCount -TrxPath $trxPath
            if ($null -eq $executed) { throw "$($project.Name) produced no readable TRX evidence." }
            $row.executed += $executed
            if ($testExit -ne 0) { throw "$($project.Name) tests failed (exit $testExit, $executed executed)." }
            if ($executed -le 0) { throw "$($project.Name) TRX reports zero executed tests." }
        }
        $row.stage = 'tests'

        $row.stage = 'package'
        Clear-PathIfExists $packagesDir
        & dotnet pack $srcProject --configuration $Configuration "-p:AgentFrameworkVersion=$version" `
            -p:ContinuousIntegrationBuild=true -p:CI=true -p:PackageOutputPath=$packagesDir --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Package build failed (exit $LASTEXITCODE)." }

        $row.stage = 'consumer-smoke'
        $previousNugetPackages = $env:NUGET_PACKAGES
        $consumerCache = Join-Path $dotnetRoot "artifacts/agent-framework-compat-consumer-cache/$version"
        Clear-PathIfExists $consumerCache
        $env:NUGET_PACKAGES = $consumerCache
        try {
            & dotnet restore $smokeProject --force --no-cache "-p:AgentFrameworkVersion=$version"
            if ($LASTEXITCODE -ne 0) { throw "Package consumer restore failed (exit $LASTEXITCODE)." }
            & dotnet run --project $smokeProject --configuration $Configuration --framework net8.0 `
                "-p:AgentFrameworkVersion=$version" --no-restore
            if ($LASTEXITCODE -ne 0) { throw "Package consumer smoke failed (exit $LASTEXITCODE)." }
        }
        finally {
            $env:NUGET_PACKAGES = $previousNugetPackages
        }
        $row.consumerSmoke = $true
        $row.stage = 'complete'
        $row.result = 'passed'
        $row.message = 'All credential-free tests, package build, and local-feed consumer smoke passed.'
    }
    catch {
        $versionFailed = $true
        $failureCount++
        $row.result = 'failed'
        $row.message = $_.Exception.Message
        Write-Host "[FAIL] $version -- $($row.message)" -ForegroundColor Red
    }
    finally {
        if ($versionFailed) { $row.result = 'failed' }
        $rows.Add([pscustomobject]$row)
        Write-Reports -Rows $rows
        Clear-ProjectOutputs
    }
}

Write-Reports -Rows $rows
if ($rows.Count -ne $Versions.Count) {
    throw "Internal report error: expected $($Versions.Count) rows, wrote $($rows.Count)."
}
if ($failureCount -gt 0) {
    Write-Host "$failureCount compatibility version(s) FAILED; reports retained in $resultsDir." -ForegroundColor Red
    exit 1
}
Write-Host "Agent Framework compatibility PASSED for: $($Versions -join ', ')" -ForegroundColor Green
