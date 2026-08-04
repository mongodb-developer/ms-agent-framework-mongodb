<#
.SYNOPSIS
    Shared, dependency-free package-content allowlist logic for MongoDB.AgentFramework's .nupkg/.snupkg.

.DESCRIPTION
    Factored out of verify-package.ps1 so the exact-match, multiplicity-aware comparison logic is a pure
    function with no ZIP/file-system dependency, and can therefore be exercised directly by
    verify-package.allowlist.tests.ps1 (a self-test using deliberately missing/extra/duplicated fixture entries)
    without needing a real packed artifact.

    The package-content allowlist requirement is an EXACT expected entry set with EXACT multiplicity, not merely
    "every actual entry matches some allowed pattern": the previous regex-only implementation would never notice
    a required entry being silently absent (e.g., a missing TFM's .dll, or a missing README.md) because it only
    ever flagged entries that failed to match an allowed pattern -- it never checked the other direction. A
    silently missing lib/netX.0 assembly, or a duplicated entry inflating the package, would both have passed
    the old check. Test-PackageContentAllowlist below fails on all three failure shapes: a required entry that
    is completely absent, an entry present that is not in the expected set at all, and an entry present with the
    wrong count (including one that IS expected but appears more than once).
#>

# The OPC core-properties part filename is a fresh random 32-hex-digit GUID on every `dotnet pack` invocation
# (NuGet.Client's own OPC-packaging behavior; see verify-package.ps1's reproducibility step for the empirical
# proof that this is independent of build determinism). Exactly one such part is expected in both packages, so
# it is normalized to a fixed placeholder token before the exact-match comparison below, which otherwise would
# spuriously fail every single run on that GUID alone. Any filename under core-properties/ that does NOT match
# the real 32-hex-digit GUID shape is intentionally left un-normalized, so a malformed or unexpected
# core-properties filename still shows up as its own missing/unexpected entry rather than being silently waved
# through.
function Get-NormalizedPackageEntryName {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name
    )

    return $Name -replace '(^package/services/metadata/core-properties/)[0-9a-fA-F]{32}(\.psmdcp$)', '$1{guid}$2'
}

# The exact, normalized set of entries MongoDB.AgentFramework.<version>.nupkg must contain -- no more, no fewer,
# each exactly once. README/nuspec/[Content_Types]/rels/psmdcp are the OPC + NuGet metadata wrapper every nupkg
# has; the six lib/ entries are the three shipped TFMs' assembly plus its generated XML doc comments.
$script:NupkgExpectedEntries = @(
    '_rels/.rels'
    '[Content_Types].xml'
    'package/services/metadata/core-properties/{guid}.psmdcp'
    'MongoDB.AgentFramework.nuspec'
    'README.md'
    'lib/net8.0/MongoDB.AgentFramework.dll'
    'lib/net8.0/MongoDB.AgentFramework.xml'
    'lib/net9.0/MongoDB.AgentFramework.dll'
    'lib/net9.0/MongoDB.AgentFramework.xml'
    'lib/net10.0/MongoDB.AgentFramework.dll'
    'lib/net10.0/MongoDB.AgentFramework.xml'
)

# The exact, normalized set of entries MongoDB.AgentFramework.<version>.snupkg must contain: the same OPC/NuGet
# metadata wrapper as the nupkg, plus exactly the three shipped TFMs' portable PDBs -- deliberately no dll/xml/
# README, since a symbol package must never duplicate the runtime payload.
$script:SnupkgExpectedEntries = @(
    '_rels/.rels'
    '[Content_Types].xml'
    'package/services/metadata/core-properties/{guid}.psmdcp'
    'MongoDB.AgentFramework.nuspec'
    'lib/net8.0/MongoDB.AgentFramework.pdb'
    'lib/net9.0/MongoDB.AgentFramework.pdb'
    'lib/net10.0/MongoDB.AgentFramework.pdb'
)

<#
.SYNOPSIS
    Compares a package's actual (normalized) entry list against an exact expected entry set, including
    multiplicity, and returns a structured, testable result -- no console output, no file/zip I/O.

.OUTPUTS
    [pscustomobject] with: Label, Passed, Missing (expected but absent), Unexpected (present but not expected,
    formatted with the observed count), MultiplicityMismatch (expected AND present, but the wrong number of
    times), ActualCount, ExpectedCount.
#>
function Test-PackageContentAllowlist {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ActualEntries,
        [Parameter(Mandatory)][string[]]$ExpectedEntries,
        [Parameter(Mandatory)][string]$Label
    )

    $normalizedActual = @($ActualEntries | ForEach-Object { Get-NormalizedPackageEntryName $_ })

    $actualCounts = @{}
    foreach ($entry in $normalizedActual) {
        $actualCounts[$entry] = 1 + [int]($actualCounts[$entry] ?? 0)
    }

    $expectedCounts = @{}
    foreach ($entry in $ExpectedEntries) {
        $expectedCounts[$entry] = 1 + [int]($expectedCounts[$entry] ?? 0)
    }

    $allKeys = @($actualCounts.Keys) + @($expectedCounts.Keys) | Select-Object -Unique

    $missing = [System.Collections.Generic.List[string]]::new()
    $unexpected = [System.Collections.Generic.List[string]]::new()
    $multiplicityMismatch = [System.Collections.Generic.List[string]]::new()

    foreach ($key in $allKeys) {
        $actualCount = [int]($actualCounts[$key] ?? 0)
        $expectedCount = [int]($expectedCounts[$key] ?? 0)

        if ($actualCount -eq 0 -and $expectedCount -gt 0) {
            $missing.Add($key)
        }
        elseif ($actualCount -gt 0 -and $expectedCount -eq 0) {
            $unexpected.Add("$key (found x$actualCount)")
        }
        elseif ($actualCount -ne $expectedCount) {
            $multiplicityMismatch.Add("$key (expected x$expectedCount, found x$actualCount)")
        }
    }

    return [pscustomobject]@{
        Label                 = $Label
        Passed                = ($missing.Count -eq 0 -and $unexpected.Count -eq 0 -and $multiplicityMismatch.Count -eq 0)
        Missing               = $missing.ToArray()
        Unexpected            = $unexpected.ToArray()
        MultiplicityMismatch  = $multiplicityMismatch.ToArray()
        ActualCount           = $normalizedActual.Count
        ExpectedCount         = $ExpectedEntries.Count
    }
}
