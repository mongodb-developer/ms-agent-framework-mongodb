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
function Get-NuspecMetadataAssertions {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Metadata
    )

    return [ordered]@{
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
        "at least one per-TFM dependency group"         = { $Metadata.dependencies.group.Count -ge 1 }
    }
}
