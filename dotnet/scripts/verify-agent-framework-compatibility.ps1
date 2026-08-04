#Requires -Version 7.0
<#
.SYNOPSIS
    Verifies MongoDB.AgentFramework restores, builds, and tests cleanly against the exact minimum and newest
    verified Microsoft Agent Framework versions, without ever editing the tracked package-reference range.

.DESCRIPTION
    docs/spec/compatibility-migration.md requires an explicit compatibility matrix for the Microsoft Agent
    Framework range this package depends on: 1.13.0 (the reflection-verified minimum) through 1.16.0 (the
    reflection-verified newest, per docs/development/persistence/dotnet-contract-research.md and
    dotnet-checkpoint-contract-research.md). MongoDB.AgentFramework.csproj and
    MongoDB.AgentFramework.Tests.csproj both reference Microsoft.Agents.AI.Abstractions/Workflows through a
    single `$(AgentFrameworkVersion)` MSBuild property that defaults to the tracked `[1.13.0,1.17.0)` range when
    not overridden -- so this script (and the `dotnet-agent-framework-compat` CI job) exercise both matrix
    bounds purely via `-p:AgentFrameworkVersion=<exact version>` on `dotnet restore`/`build`/`test`, never by
    editing any tracked .csproj.

    For each matrix version this script:
      1. Cleans bin/obj for src/MongoDB.AgentFramework and tests/MongoDB.AgentFramework.Tests (a stale
         project.assets.json from a different pinned version would otherwise mask a real restore failure).
      2. Restores + builds (Release) with `-p:AgentFrameworkVersion=<version>`.
      3. Runs the unit test suite (Release, matching dotnet-quality.yml's `dotnet test` invocation) with the
         same override, so behavior is proven, not just compilation. Integration tests requiring real MongoDB
         credentials skip themselves cleanly (MONGODB_URI/MONGODB_DATABASE unset) exactly as they do in every
         other CI run; this script does not filter them out.
      4. Confirms Microsoft.Agents.AI.Abstractions and Microsoft.Agents.AI.Workflows both resolved to the exact
         requested version (not merely "some version satisfying the range"), and prints the resolved
         Microsoft.Extensions.* transitive versions so a reviewer can see they stayed within this package's own
         declared range at both matrix bounds (this package's own PackageReference ranges are what constrain
         them; this script does not independently re-derive compatibility, it verifies the declared range holds).

    Never edits a tracked file, never touches artifacts/packages, and restores the default (un-pinned) build
    state afterwards by cleaning bin/obj again -- a plain `dotnet build` after running this script keeps
    resolving the tracked `[1.13.0,1.17.0)` range exactly as before.

.PARAMETER Versions
    The exact Agent Framework versions to verify. Defaults to the documented matrix bounds: 1.13.0 and 1.16.0.

.EXAMPLE
    pwsh dotnet/scripts/verify-agent-framework-compatibility.ps1
#>
[CmdletBinding()]
param(
    [string[]]$Versions = @('1.13.0', '1.16.0')
)

$ErrorActionPreference = "Stop"

$DotnetRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$SrcProject = Join-Path $DotnetRoot "src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj"
$TestProject = Join-Path $DotnetRoot "tests/MongoDB.AgentFramework.Tests/MongoDB.AgentFramework.Tests.csproj"

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

function Clear-PathIfExists([string]$Path) {
    if (Test-Path $Path) {
        Remove-Item $Path -Recurse -Force
    }
}

function Clear-ProjectOutputs() {
    Clear-PathIfExists (Join-Path $DotnetRoot "src/MongoDB.AgentFramework/bin")
    Clear-PathIfExists (Join-Path $DotnetRoot "src/MongoDB.AgentFramework/obj")
    Clear-PathIfExists (Join-Path $DotnetRoot "tests/MongoDB.AgentFramework.Tests/bin")
    Clear-PathIfExists (Join-Path $DotnetRoot "tests/MongoDB.AgentFramework.Tests/obj")
}

function Get-ResolvedPackageVersion([string]$AssetsPath, [string]$PackageId) {
    $assets = Get-Content $AssetsPath -Raw | ConvertFrom-Json
    $libraryKey = $assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like "$PackageId/*" } | Select-Object -First 1
    if (-not $libraryKey) {
        return $null
    }

    return ($libraryKey -split '/', 2)[1]
}

foreach ($version in $Versions) {
    Write-Section "Agent Framework compatibility matrix: $version"
    Clear-ProjectOutputs

    & dotnet build $SrcProject --configuration Release "-p:AgentFrameworkVersion=$version"
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "$version -- src build failed with exit code $LASTEXITCODE"
        continue
    }
    Write-Ok "$version -- src/MongoDB.AgentFramework built"

    $assetsPath = Join-Path $DotnetRoot "src/MongoDB.AgentFramework/obj/project.assets.json"
    $resolvedAbstractions = Get-ResolvedPackageVersion -AssetsPath $assetsPath -PackageId "Microsoft.Agents.AI.Abstractions"
    $resolvedWorkflows = Get-ResolvedPackageVersion -AssetsPath $assetsPath -PackageId "Microsoft.Agents.AI.Workflows"
    $resolvedLoggingAbstractions = Get-ResolvedPackageVersion -AssetsPath $assetsPath -PackageId "Microsoft.Extensions.Logging.Abstractions"

    if ($resolvedAbstractions -eq $version) {
        Write-Ok "$version -- Microsoft.Agents.AI.Abstractions resolved to the exact requested version"
    }
    else {
        Write-Failure "$version -- Microsoft.Agents.AI.Abstractions resolved to '$resolvedAbstractions', expected exactly '$version'"
    }

    if ($resolvedWorkflows -eq $version) {
        Write-Ok "$version -- Microsoft.Agents.AI.Workflows resolved to the exact requested version"
    }
    else {
        Write-Failure "$version -- Microsoft.Agents.AI.Workflows resolved to '$resolvedWorkflows', expected exactly '$version'"
    }

    Write-Host "$version -- transitive Microsoft.Extensions.Logging.Abstractions resolved to $resolvedLoggingAbstractions (declared range [10.0.9,11.0.0))"

    & dotnet test $TestProject --configuration Release "-p:AgentFrameworkVersion=$version" --no-restore --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "$version -- unit tests failed with exit code $LASTEXITCODE"
        continue
    }
    Write-Ok "$version -- MongoDB.AgentFramework.Tests unit suite passed"
}

# Restore the default, un-pinned build state so a subsequent plain `dotnet build`/`dotnet pack` resolves the
# tracked range again, not whichever matrix version ran last.
Clear-ProjectOutputs

Write-Section "Summary"
if ($script:FailureCount -gt 0) {
    Write-Host "$($script:FailureCount) check(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "Agent Framework compatibility matrix PASSED for: $($Versions -join ', ')" -ForegroundColor Green
exit 0
