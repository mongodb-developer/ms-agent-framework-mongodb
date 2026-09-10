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

.DESCRIPTION
    Rejects, by throwing, either of two duplicate shapes rather than silently overwriting the earlier entry in
    the dictionary being built:
      - Two (or more) <group targetFramework="..."> elements normalizing to the same TFM (e.g. two "net8.0"
        groups, or "net8.0" and " NET8.0 "). Assigning `$groupsByTfm[$tfm] = ...` a second time would otherwise
        silently discard the first group's dependency set with no error and no assertion ever seeing it existed.
      - Two (or more) <dependency id="..."> elements within the SAME group sharing an id (e.g. "MongoDB.Driver"
        declared twice, possibly with conflicting version ranges). Assigning `$dependenciesById[$id] = ...` a
        second time would otherwise silently discard the first range the same way.
    Both shapes indicate a genuinely malformed/ambiguous nuspec (or a broken msbuild/pack configuration that
    emitted it) that must fail loudly here, BEFORE any assertion performs a dictionary lookup against
    potentially-incomplete data -- a silently-dropped duplicate could otherwise mask a real dependency drift (the
    surviving entry might not be the one a reviewer expects) while every assertion still appears to pass.
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
        if ($groupsByTfm.Contains($tfm)) {
            throw "Nuspec <dependencies> declares more than one <group targetFramework> normalizing to '$tfm' -- duplicate dependency groups for the same target framework are rejected instead of silently overwriting the earlier group."
        }

        $dependenciesById = [ordered]@{}
        foreach ($dependency in @($group.dependency)) {
            if ($null -eq $dependency) { continue }
            $id = [string]$dependency.id
            if ($dependenciesById.Contains($id)) {
                throw "Nuspec dependency group '$tfm' declares package id '$id' more than once -- duplicate dependency ids within one group are rejected instead of silently overwriting the earlier range."
            }

            $dependenciesById[$id] = Get-NormalizedVersionRange ([string]$dependency.version)
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

    # Closure-safe function capture. GetNewClosure() gives the resulting scriptblock its own isolated dynamic
    # module/session state; ordinary lexical *variables* referenced in the body are snapshotted into that new
    # state by GetNewClosure() itself, but a *function call* made from inside a closured body (e.g.
    # `Get-NuspecDependencyGroupsByTfm $Metadata`) is instead resolved by ordinary PowerShell command-name lookup
    # at the moment the closure is later invoked -- and that lookup depends on the closure's own session state
    # chaining back to whatever scope originally dot-sourced this file. That chain is an implementation detail,
    # not a documented guarantee, and does not reliably hold across every invocation context a scriptblock value
    # can end up being called from (e.g. a Pester It/Describe block, a background job, a runspace, or simply a
    # different PowerShell host/version than was used during development) -- in which case the call fails with
    # "the term '...' is not recognized" precisely because the closure's isolated scope never sees the sibling
    # function at all, even though the exact same code works when tested in the same scope it was authored in.
    #
    # The fix: capture each helper function's definition as a scriptblock *value* (via the `function:` drive)
    # here, in Get-NuspecMetadataAssertions's own scope -- where both helpers are unconditionally visible because
    # this file dot-sources them together -- and let GetNewClosure() snapshot that captured value into the
    # closure exactly like any other captured variable. Every assertion below then invokes the helper via `& `
    # against the captured value, never via bare command-name resolution, so behavior no longer depends on which
    # scope the resulting scriptblock is eventually invoked from.
    $capturedGetGroupsByTfm = ${function:Get-NuspecDependencyGroupsByTfm}
    $capturedMatchesExactly = ${function:Test-NuspecDependencyMapMatchesExactly}

    # Same `$script:`-qualified-variable gotcha documented below on the per-TFM loop: any scriptblock that gets
    # `.GetNewClosure()`'d must reference a plain local copy of a `$script:` value, never the `$script:` variable
    # itself, because the closure's own isolated session state has no such script-scoped variable and silently
    # evaluates the reference to $null instead of failing loudly.
    $capturedRequiredTfms = $script:RequiredNuspecDependencyTfms
    $capturedExcludedDependencyIds = $script:ExcludedNuspecDependencyPackageIds

    # Every assertion scriptblock below is `.GetNewClosure()`'d, including the simple ones that reference only
    # $Metadata and no helper function. This is deliberate and not merely defensive-in-depth: a PLAIN (non-closed)
    # PowerShell scriptblock is dynamically scoped when later invoked via `& $Body` -- it resolves a free variable
    # like $Metadata by walking the ACTUAL CALL STACK at the moment of invocation, not by binding to wherever the
    # scriptblock was lexically written. In other words, `{ $Metadata.id -eq "..." }` only ever produces the
    # right answer if, at the exact point something eventually calls `& $Body`, there HAPPENS to be an in-scope
    # variable literally named $Metadata (PowerShell variable names are case-insensitive, so verify-package.ps1's
    # own `$metadata = $nuspec.package.metadata` at its top level is what has always made this "work" there, and
    # verify-package.metadata.tests.ps1's Test-AllAssertions helper takes a parameter literally named $Metadata
    # for the same reason) -- a total accident of naming, not real closure semantics. Call these assertions from
    # ANY other scope shape that does not happen to have a like-named variable in its call chain (proven by
    # verify-package.metadata-integration.tests.ps1's "Shape 1" case, which names its variable $realMetadata) and
    # every one of them silently evaluates against $null and returns the WRONG boolean $false -- with no error,
    # not even the "term not recognized" symptom the closure-only bug produces, making it strictly more dangerous.
    # GetNewClosure() eliminates this: it snapshots $Metadata's CURRENT value into the closure's own isolated
    # state, so evaluation no longer depends on what variable names happen to exist in whatever scope later calls
    # `& $Body`.
    $assertions = [ordered]@{
        "id equals MongoDB.AgentFramework"             = { $Metadata.id -eq "MongoDB.AgentFramework" }.GetNewClosure()
        "version is set"                                = { -not [string]::IsNullOrWhiteSpace($Metadata.version) }.GetNewClosure()
        "authors is set"                                = { -not [string]::IsNullOrWhiteSpace($Metadata.authors) }.GetNewClosure()
        "license expression is MIT"                     = { $Metadata.license.type -eq "expression" -and $Metadata.license.'#text' -eq "MIT" }.GetNewClosure()
        "licenseUrl is set (legacy consumer fallback)"  = { -not [string]::IsNullOrWhiteSpace($Metadata.licenseUrl) }.GetNewClosure()
        "readme is set"                                 = { -not [string]::IsNullOrWhiteSpace($Metadata.readme) }.GetNewClosure()
        "projectUrl is set"                             = { -not [string]::IsNullOrWhiteSpace($Metadata.projectUrl) }.GetNewClosure()
        "description is set"                            = { -not [string]::IsNullOrWhiteSpace($Metadata.description) }.GetNewClosure()
        "releaseNotes is set"                           = { -not [string]::IsNullOrWhiteSpace($Metadata.releaseNotes) }.GetNewClosure()
        "copyright is set"                              = { -not [string]::IsNullOrWhiteSpace($Metadata.copyright) }.GetNewClosure()
        "tags is set"                                   = { -not [string]::IsNullOrWhiteSpace($Metadata.tags) }.GetNewClosure()
        "repository url is embedded (SourceLink)"       = { -not [string]::IsNullOrWhiteSpace($Metadata.repository.url) }.GetNewClosure()
        "repository commit is embedded (SourceLink)"    = { -not [string]::IsNullOrWhiteSpace($Metadata.repository.commit) }.GetNewClosure()
        "dependency groups are exactly net8.0, net9.0, and net10.0 (no more, no less)" = {
            $actualTfms = @((& $capturedGetGroupsByTfm $Metadata).Keys) | Sort-Object
            $expectedTfms = @($capturedRequiredTfms) | Sort-Object
            $null -eq (Compare-Object -ReferenceObject $expectedTfms -DifferenceObject $actualTfms)
        }.GetNewClosure()
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
            $groupsByTfm = & $capturedGetGroupsByTfm $Metadata
            if (-not $groupsByTfm.Contains($capturedTfm)) {
                return $false
            }

            & $capturedMatchesExactly -Actual $groupsByTfm[$capturedTfm] -Expected $capturedExpectedDependencies
        }.GetNewClosure()
    }

    $assertions["no analyzer/source-link/build-only packages leak into the nuspec dependency list"] = {
        $groupsByTfm = & $capturedGetGroupsByTfm $Metadata
        $allDependencyIds = @($groupsByTfm.Values | ForEach-Object { @($_.Keys) })
        $leaked = @($allDependencyIds | Where-Object { $capturedExcludedDependencyIds -contains $_ })
        $leaked.Count -eq 0
    }.GetNewClosure()

    return $assertions
}
