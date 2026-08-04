<#
.SYNOPSIS
    Shared, dependency-free nuspec-metadata assertion logic for MongoDB.AgentFramework's packed .nupkg.

.DESCRIPTION
    Factored out of verify-package.ps1 so each named metadata assertion (id/version/authors/license/...) is a
    pure, testable PASS/FAIL evaluation, not "no exception was thrown". verify-package.ps1's previous
    `Invoke-Checked` helper invoked the assertion scriptblock and discarded its return value entirely:

        function Invoke-Checked([string]$Description, [scriptblock]$Body) {
            try {
                & $Body                  # <-- return value silently discarded
                Write-Ok $Description    # <-- always printed unless $Body THREW
                return $true
            }
            catch { ... }
        }

    So an assertion scriptblock such as `{ $metadata.id -eq "MongoDB.AgentFramework" }` returning the boolean
    $false (id genuinely wrong) still printed "[ OK ]" and never incremented the failure count -- only a thrown
    exception (e.g. a missing XML node causing a null-reference-shaped error) was ever caught. Test-NuspecAssertion
    below is the single source of truth for "did this named check actually pass": it requires the scriptblock to
    return a strict [bool], treats an explicit $false the same as a thrown exception (both are FAIL), and treats
    any non-boolean return (including $null or a "truthy" string/object) as a FAIL too, so a badly-written
    assertion can never accidentally pass through implicit truthiness coercion. This is exercised identically by
    verify-package.ps1's real run and by verify-package.metadata.tests.ps1's self-test, which deliberately mutates
    one required nuspec field at a time to a wrong/missing value and asserts the corresponding named assertion
    (and only that one) is reported as FAILED.
#>

<#
.SYNOPSIS
    Invokes a single named boolean assertion scriptblock and returns a structured, testable PASS/FAIL result.

.DESCRIPTION
    The scriptblock MUST return an actual boolean ($true/$false):
      - A scriptblock that throws is reported as Passed=$false with the exception message.
      - A scriptblock that returns the boolean $false is reported as Passed=$false -- this is the case the
        previous Invoke-Checked implementation silently missed entirely.
      - A scriptblock that returns anything other than a strict [bool] (including $null, an empty or non-empty
        string, or any other object) is ALSO reported as Passed=$false; there is intentionally no implicit
        truthiness coercion, since that would reintroduce a variant of the same "ignored output passes" bug.
      - Only a scriptblock returning the boolean $true is reported as Passed=$true.

.OUTPUTS
    [pscustomobject] with: Description, Passed, Message (empty on success; the failure reason otherwise).
#>
function Test-NuspecAssertion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Body
    )

    try {
        $result = & $Body
    }
    catch {
        return [pscustomobject]@{
            Description = $Description
            Passed      = $false
            Message     = $_.Exception.Message
        }
    }

    if ($result -is [bool]) {
        if ($result) {
            return [pscustomobject]@{ Description = $Description; Passed = $true; Message = "" }
        }

        return [pscustomobject]@{ Description = $Description; Passed = $false; Message = "assertion returned `$false" }
    }

    $observedType = if ($null -eq $result) { '<null>' } else { $result.GetType().FullName }
    return [pscustomobject]@{
        Description = $Description
        Passed      = $false
        Message     = "assertion scriptblock must return a boolean; got '$result' ($observedType)"
    }
}

<#
.SYNOPSIS
    Builds the exact, named ordered set of nuspec metadata assertions this package's packed .nuspec must satisfy.

.DESCRIPTION
    Takes a metadata object exposing the same shape as a parsed nuspec's <metadata> XML element (id, version,
    authors, license.type/'#text', licenseUrl, readme, projectUrl, description, releaseNotes, copyright, tags,
    repository.url/commit, dependencies.group) and returns an ordered dictionary of Description -> [scriptblock].
    Both verify-package.ps1's real run (against the actual parsed nuspec) and verify-package.metadata.tests.ps1's
    self-test (against plain PSCustomObject fixtures, so a "wrong value" scenario can be constructed cheaply
    without parsing real XML) build this exact same set, so there is only one place the required-assertion list
    is defined.
#>

<#
.SYNOPSIS
    The exact direct package id -> normalized NuGet version-range map every dependency group in the packed
    nuspec MUST expose, identically for net8.0/net9.0/net10.0 (see MongoDB.AgentFramework.csproj's single
    <ItemGroup> of PackageReference entries -- there is only one dependency set, applied to all three TFMs).
    Kept as a single source of truth so a real csproj change and this assertion's expectation cannot silently
    drift apart without a corresponding test/code review.
#>
$script:ExpectedNuspecDependenciesByPackageId = [ordered]@{
    "Microsoft.Agents.AI.Abstractions"           = "[1.13.0,1.17.0)"
    "Microsoft.Agents.AI.Workflows"               = "[1.13.0,1.17.0)"
    "Microsoft.Extensions.AI.Abstractions"        = "[10.7.0,11.0.0)"
    "Microsoft.Extensions.Logging.Abstractions"   = "[10.0.9,11.0.0)"
    "MongoDB.Driver"                              = "[3.10.0,4.0.0)"
}

# Build-only/analyzer PackageReferences (Microsoft.CodeAnalysis.PublicApiAnalyzers, Microsoft.SourceLink.GitHub)
# carry PrivateAssets="all" in the csproj specifically so they do NOT flow into the published nuspec's
# <dependencies> as a runtime requirement for consumers. This is the negative-space check for that intent.
$script:ExcludedNuspecDependencyPackageIds = @(
    "Microsoft.CodeAnalysis.PublicApiAnalyzers"
    "Microsoft.SourceLink.GitHub"
)

$script:RequiredNuspecDependencyTfms = @("net8.0", "net9.0", "net10.0")

# TFM "normalization" here means tolerating incidental case/whitespace differences in the <group
# targetFramework="..."> attribute without treating them as a real mismatch; NuGet itself always emits the exact
# short-folder-name form (e.g. "net8.0"), so this is a defensive minimum, not a full moniker parser.
function Get-NormalizedTfm {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Tfm)
    return $Tfm.Trim().ToLowerInvariant()
}

# NuGet's pack step renders nuspec version ranges as "[1.2.0, 3.0.0)" (space after the comma); csproj Version
# attributes are written without that space. Comparing on whitespace-stripped text keeps the assertion robust to
# that purely cosmetic difference while still requiring an exact bound-for-bound match otherwise.
function Get-NormalizedVersionRange {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Range)
    return ($Range -replace '\s', '')
}

<#
.SYNOPSIS
    Reads a nuspec <metadata>'s <dependencies> element into an ordered TFM -> (ordered package id -> normalized
    version range) map, tolerating the classic PowerShell XML "single child collapses out of array form" quirk
    (a nuspec with exactly one <group> or exactly one <dependency> would otherwise not enumerate as a collection).
#>
function Get-NuspecDependencyGroupsByTfm {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Metadata
    )

    $groupsByTfm = [ordered]@{}
    foreach ($group in @($Metadata.dependencies.group)) {
        if ($null -eq $group) { continue }

        $tfm = Get-NormalizedTfm $group.targetFramework
        $dependenciesById = [ordered]@{}
        foreach ($dependency in @($group.dependency)) {
            if ($null -eq $dependency) { continue }
            $dependenciesById[[string]$dependency.id] = Get-NormalizedVersionRange ([string]$dependency.version)
        }

        $groupsByTfm[$tfm] = $dependenciesById
    }

    return $groupsByTfm
}

<#
.SYNOPSIS
    Strict-boolean exact-set comparison between an actual (package id -> normalized range) map and the expected
    one: every expected id must be present with the exact expected range, no extra ids may appear, and no
    id may be missing.
#>
function Test-NuspecDependencyMapMatchesExactly {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Actual,
        [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Expected
    )

    $actualIds = @($Actual.Keys) | Sort-Object
    $expectedIds = @($Expected.Keys) | Sort-Object
    if ($null -ne (Compare-Object -ReferenceObject $expectedIds -DifferenceObject $actualIds)) {
        return $false
    }

    foreach ($id in $expectedIds) {
        if ($Actual[$id] -ne $Expected[$id]) {
            return $false
        }
    }

    return $true
}

function Get-NuspecMetadataAssertions {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Metadata
    )

    $assertions = [ordered]@{
        "id equals MongoDB.AgentFramework"             = { $Metadata.id -eq "MongoDB.AgentFramework" }
        "version is set"                                = { -not [string]::IsNullOrWhiteSpace($Metadata.version) }
        "authors is set"                                = { -not [string]::IsNullOrWhiteSpace($Metadata.authors) }
        "license expression is MIT"                     = { $Metadata.license.type -eq "expression" -and $Metadata.license.'#text' -eq "MIT" }
        "licenseUrl is set (legacy consumer fallback)"  = { -not [string]::IsNullOrWhiteSpace($Metadata.licenseUrl) }
        "readme is set"                                 = { -not [string]::IsNullOrWhiteSpace($Metadata.readme) }
        "projectUrl is set"                             = { -not [string]::IsNullOrWhiteSpace($Metadata.projectUrl) }
        "description is set"                            = { -not [string]::IsNullOrWhiteSpace($Metadata.description) }
        "releaseNotes is set"                           = { -not [string]::IsNullOrWhiteSpace($Metadata.releaseNotes) }
        "copyright is set"                              = { -not [string]::IsNullOrWhiteSpace($Metadata.copyright) }
        "tags is set"                                   = { -not [string]::IsNullOrWhiteSpace($Metadata.tags) }
        "repository url is embedded (SourceLink)"       = { -not [string]::IsNullOrWhiteSpace($Metadata.repository.url) }
        "repository commit is embedded (SourceLink)"    = { -not [string]::IsNullOrWhiteSpace($Metadata.repository.commit) }
        "dependency groups are exactly net8.0, net9.0, and net10.0 (no more, no less)" = {
            $actualTfms = @((Get-NuspecDependencyGroupsByTfm $Metadata).Keys) | Sort-Object
            $expectedTfms = @($script:RequiredNuspecDependencyTfms) | Sort-Object
            $null -eq (Compare-Object -ReferenceObject $expectedTfms -DifferenceObject $actualTfms)
        }
    }

    foreach ($tfm in $script:RequiredNuspecDependencyTfms) {
        # Closure-safe: capture the loop variable (and the expected-dependency map) into new, unscoped locals
        # before the scriptblock is created. GetNewClosure() snapshots ordinary lexical variables it finds
        # referenced in the scriptblock body, but it does NOT resolve explicit `$script:`-qualified variables --
        # those still look up the *closure's own* isolated session state after GetNewClosure(), where no such
        # script-scoped variable exists, and silently evaluate to $null. Assigning the script-scoped value to a
        # plain local here (and referencing only that local inside the scriptblock) sidesteps that entirely.
        $capturedTfm = $tfm
        $capturedExpectedDependencies = $script:ExpectedNuspecDependenciesByPackageId
        $assertions["$capturedTfm dependency group has exactly the expected package ids and version ranges"] = {
            $groupsByTfm = Get-NuspecDependencyGroupsByTfm $Metadata
            if (-not $groupsByTfm.Contains($capturedTfm)) {
                return $false
            }

            Test-NuspecDependencyMapMatchesExactly -Actual $groupsByTfm[$capturedTfm] -Expected $capturedExpectedDependencies
        }.GetNewClosure()
    }

    $assertions["no analyzer/source-link/build-only packages leak into the nuspec dependency list"] = {
        $groupsByTfm = Get-NuspecDependencyGroupsByTfm $Metadata
        $allDependencyIds = @($groupsByTfm.Values | ForEach-Object { @($_.Keys) })
        $leaked = @($allDependencyIds | Where-Object { $script:ExcludedNuspecDependencyPackageIds -contains $_ })
        $leaked.Count -eq 0
    }

    return $assertions
}
