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
      1. Cleans bin/obj for src/MongoDB.AgentFramework and tests/MongoDB.AgentFramework.Tests, and any leftover
         TRX results from a previous run of this script (a stale project.assets.json or TRX file from a
         different pinned version would otherwise mask a real restore/test failure or a false "passed" reading).
      2. Restores tests/MongoDB.AgentFramework.Tests.csproj with `-p:AgentFrameworkVersion=<version>` -- NuGet's
         restore graph pulls in its `<ProjectReference>` to src/MongoDB.AgentFramework.csproj automatically, so
         this single restore covers the exact pinned version for both the package under test ("relevant package
         build") and its test suite; neither project is ever restored/built independently of the other, so a
         restore failure in either is always caught.
      3. Builds (Release) both the src project directly (the exact thing that gets packed) and the test project,
         each with the same `-p:AgentFrameworkVersion=<version>` override and `--no-restore` (the restore already
         happened in step 2, so a silently-stale/incomplete restore can never be masked by an implicit re-restore
         here).
      4. Confirms Microsoft.Agents.AI.Abstractions and Microsoft.Agents.AI.Workflows both resolved to the exact
         requested version (not merely "some version satisfying the range"), and prints the resolved
         Microsoft.Extensions.* transitive versions so a reviewer can see they stayed within this package's own
         declared range at both matrix bounds (this package's own PackageReference ranges are what constrain
         them; this script does not independently re-derive compatibility, it verifies the declared range holds).
      5. Runs the unit test suite with `--no-build --no-restore` (the build already happened in step 3, so this
         step can only execute already-built tests, never silently no-op through an implicit rebuild) and a TRX
         logger, then parses the produced TRX file's `<ResultSummary><Counters>` element and asserts its
         `executed` attribute is strictly greater than zero. This is deliberate: `dotnet test --no-restore`
         against an unrestored/stale test project can exit 0 with zero tests actually executed (MSBuild silently
         no-ops the VSTest target when it cannot evaluate the test-SDK's `IsTestProject` property from a missing
         restore), which a console-output/exit-code check alone would never catch. Integration tests requiring
         real MongoDB credentials still skip themselves cleanly (MONGODB_URI/MONGODB_DATABASE unset) exactly as
         they do in every other CI run; this script does not filter them out, and skipped tests are excluded from
         the asserted `executed` count (they count toward `total`, not `executed`).

    Never edits a tracked file, never touches artifacts/packages, and restores the default (un-pinned) build
    state afterwards by cleaning bin/obj again -- a plain `dotnet build` after running this script keeps
    resolving the tracked `[1.13.0,1.17.0)` range exactly as before.

.PARAMETER Versions
    The exact Agent Framework versions to verify. Defaults to the documented matrix bounds: 1.13.0 and 1.16.0.

.PARAMETER Configuration
    The build configuration to restore/build/test. Defaults to Release, matching every other verification script
    in this directory (verify-package.ps1) and dotnet-quality.yml's `dotnet test`/`dotnet build` invocations.

.EXAMPLE
    pwsh dotnet/scripts/verify-agent-framework-compatibility.ps1

.EXAMPLE
    pwsh dotnet/scripts/verify-agent-framework-compatibility.ps1 -Configuration Release -Versions "1.13.0"
#>
[CmdletBinding()]
param(
    [string[]]$Versions = @('1.13.0', '1.16.0'),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "TrxResults.ps1")

$DotnetRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$SrcProject = Join-Path $DotnetRoot "src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj"
$TestProject = Join-Path $DotnetRoot "tests/MongoDB.AgentFramework.Tests/MongoDB.AgentFramework.Tests.csproj"
$ResultsDir = Join-Path $DotnetRoot "artifacts/agent-framework-compat-results"

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
    Clear-PathIfExists $ResultsDir
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

    # A single restore against the test project covers both projects: NuGet's restore graph follows the test
    # project's <ProjectReference> to src/MongoDB.AgentFramework.csproj automatically, so both the package under
    # test ("relevant package build") and its test suite are restored to the exact same pinned version in one
    # step -- never independently, so a restore failure in either can never be silently skipped by the other.
    & dotnet restore $TestProject "-p:AgentFrameworkVersion=$version"
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "$version -- restore failed with exit code $LASTEXITCODE"
        continue
    }
    Write-Ok "$version -- restored (src/MongoDB.AgentFramework + MongoDB.AgentFramework.Tests)"

    & dotnet build $SrcProject --configuration $Configuration "-p:AgentFrameworkVersion=$version" --no-restore
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

    # Build the test project explicitly (with --no-restore, since step 1 already restored it) so the following
    # `dotnet test --no-build` can never silently no-op against a stale/absent build output.
    & dotnet build $TestProject --configuration $Configuration "-p:AgentFrameworkVersion=$version" --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "$version -- MongoDB.AgentFramework.Tests build failed with exit code $LASTEXITCODE"
        continue
    }
    Write-Ok "$version -- MongoDB.AgentFramework.Tests built"

    New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null
    $trxFileName = "agent-framework-compat-$version.trx"
    $trxPath = Join-Path $ResultsDir $trxFileName

    & dotnet test $TestProject --configuration $Configuration "-p:AgentFrameworkVersion=$version" --no-build --no-restore `
        --logger "trx;LogFileName=$trxFileName" --results-directory $ResultsDir `
        --logger "console;verbosity=normal"
    $testExitCode = $LASTEXITCODE

    $executedCount = Get-TrxExecutedCount -TrxPath $trxPath
    if ($null -eq $executedCount) {
        Write-Failure "$version -- no TRX result file found at $trxPath; cannot confirm any test actually executed"
        continue
    }

    if ($testExitCode -ne 0) {
        Write-Failure "$version -- unit tests failed with exit code $testExitCode ($executedCount executed per TRX)"
        continue
    }

    # This is the assertion that actually catches the bug a console/exit-code check alone would miss: `dotnet
    # test --no-restore` against a stale/unrestored project can exit 0 having executed zero tests (MSBuild
    # silently no-ops the VSTest target when it can't evaluate IsTestProject without a completed restore). A
    # nonzero *executed* count from the TRX -- not the process exit code, not console text -- is the only thing
    # that proves tests actually ran.
    if ($executedCount -le 0) {
        Write-Failure "$version -- TRX reports zero executed tests ($trxPath); a passing exit code alone does not prove any test ran"
        continue
    }

    Write-Ok "$version -- MongoDB.AgentFramework.Tests unit suite passed ($executedCount tests executed per TRX)"
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
