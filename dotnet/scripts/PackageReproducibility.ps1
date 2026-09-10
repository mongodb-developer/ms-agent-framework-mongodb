<#
.SYNOPSIS
    Shared, dependency-free double-pack reproducibility comparison for MongoDB.AgentFramework's packed .nupkg/.snupkg.

.DESCRIPTION
    Factored out of verify-package.ps1's Step 4 so the comparison logic is a pure, testable function, not inline
    script code that can only ever be exercised against two real `dotnet pack` outputs.

    `dotnet pack` regenerates two OPC (Open Packaging Conventions) wrapper artifacts with a fresh random
    identifier on every invocation, neither of which reflects package *content*:
      - the core-properties part's file name, `package/services/metadata/core-properties/<32-hex-guid>.psmdcp`
      - `_rels/.rels`'s `<Relationship Id="R...">` attribute values (and its `Target` reference to the psmdcp
        part's GUID-bearing file name above)

    A previous version of this comparison excluded `_rels/.rels` and every `*.psmdcp` entry from the byte-level
    comparison ENTIRELY (matching only the normalized entry *name* set), even though this script's own docs and
    output claimed a "normalized comparison". That means a real, meaningful difference inside either file's
    *content* -- e.g. a different `<dc:creator>`, a different embedded package id/version in the psmdcp's
    `<dc:identifier>`/`<version>`, or `_rels/.rels` pointing the nuspec/core-properties relationship at a
    different target path than the actual packed entries -- would never be detected, because those two entries'
    content was never compared at all, only silently treated as "presence-only, always trusted".

    This module instead normalizes ONLY the specific generated-identifier substrings (the psmdcp GUID part name,
    wherever it appears, and _rels/.rels's `Id="R..."` relationship id attributes) and then compares the
    resulting normalized bytes for EVERY entry, including `_rels/.rels` and `*.psmdcp`, byte-for-byte. See
    verify-package.reproducibility.tests.ps1 for fixtures proving: (a) two packs differing only by the expected
    generated identifiers still compare equal after normalization, and (b) a genuine content difference inside
    `_rels/.rels` or a `*.psmdcp` entry -- one that is NOT just the generated identifier -- still fails the
    comparison, exactly like a real payload (dll/xml/nuspec/README) difference would.
#>

<#
.SYNOPSIS
    Normalizes a zip entry NAME by replacing the packed `*.psmdcp` part's random 32-hex-digit GUID with a fixed
    placeholder, so the two packs' entry-name sets compare equal despite that GUID differing between them.
#>
function Get-NormalizedOpcEntryName {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Name
    )

    return $Name -replace '(core-properties/)[0-9a-f]{32}(\.psmdcp)', '$1{guid}$2'
}

<#
.SYNOPSIS
    Normalizes a zip entry's CONTENT bytes, replacing only the generated-identifier substrings that are expected
    to legitimately differ between two `dotnet pack` invocations of otherwise-identical source.

.DESCRIPTION
    Only `_rels/.rels` and `*.psmdcp` entries are text-normalized at all -- every other entry (dll/xml/nuspec/
    README) is returned completely unchanged, so a real content difference anywhere else is never masked by this
    function. Within those two entries, exactly two substring shapes are replaced with a fixed placeholder:
      - the psmdcp part's 32-hex-digit GUID file name (wherever it appears -- both in the psmdcp's own path and
        in `_rels/.rels`'s `Target="...psmdcp"` attribute referencing it)
      - `_rels/.rels`'s `Id="R<hex>"` relationship id attribute values (NuGet.Client assigns these fresh random
        ids on every pack; they are OPC package-internal plumbing, not package content)
    Everything else in both files' content -- including `<dc:creator>`, `<dc:identifier>`, `<version>`,
    `<keywords>`, `<lastModifiedBy>`, and any relationship `Target`/`Type` value other than the psmdcp GUID
    itself -- is left untouched and therefore still participates in the byte-for-byte comparison.
#>
function Get-NormalizedOpcEntryBytes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()][byte[]]$Bytes
    )

    if ($Name -ne "_rels/.rels" -and $Name -notmatch '\.psmdcp$') {
        return $Bytes
    }

    $text = [System.Text.Encoding]::UTF8.GetString($Bytes)
    $normalizedText = $text `
        -replace '(core-properties/)[0-9a-f]{32}(\.psmdcp)', '$1{guid}$2' `
        -replace 'Id="R[0-9A-Fa-f]+"', 'Id="{relid}"'

    return [System.Text.Encoding]::UTF8.GetBytes($normalizedText)
}

<#
.SYNOPSIS
    Pure comparison of two packs' zip entry maps (entry name -> raw content bytes), proving reproducibility.

.PARAMETER EntriesA
    Ordered dictionary of entry name -> byte[] content for the first pack (see verify-package.ps1's
    Get-ZipEntryTexts).

.PARAMETER EntriesB
    Same shape, for the second pack.

.OUTPUTS
    [pscustomobject] with:
      - Passed: [bool] overall result.
      - EntrySetMismatch: [bool] true if the normalized entry-name sets themselves differ (a real entry is
        missing/extra, not just a generated-identifier difference).
      - NamesA / NamesB: the sorted, normalized entry-name lists actually compared (diagnostic only).
      - ContentMismatches: [string[]] normalized entry names whose NORMALIZED content differs between the two
        packs -- populated only when EntrySetMismatch is $false. Any entry appearing here failed the comparison
        even after normalization, meaning the difference is real (not merely a generated OPC identifier).
#>
function Test-PackageReproducibility {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$EntriesA,
        [Parameter(Mandatory)]$EntriesB
    )

    $namesA = @($EntriesA.Keys | ForEach-Object { Get-NormalizedOpcEntryName $_ }) | Sort-Object
    $namesB = @($EntriesB.Keys | ForEach-Object { Get-NormalizedOpcEntryName $_ }) | Sort-Object

    if (($namesA -join "|") -ne ($namesB -join "|")) {
        return [pscustomobject]@{
            Passed            = $false
            EntrySetMismatch  = $true
            NamesA            = @($namesA)
            NamesB            = @($namesB)
            ContentMismatches = @()
        }
    }

    # Lookup from normalized name -> the ORIGINAL (GUID-bearing) key in B, since B's raw key for the psmdcp entry
    # (and any entry whose name embeds it) will not literally equal A's raw key even though they represent "the
    # same" entry after normalization.
    $bKeyByNormalizedName = @{}
    foreach ($key in $EntriesB.Keys) {
        $bKeyByNormalizedName[(Get-NormalizedOpcEntryName $key)] = $key
    }

    $mismatches = @()
    foreach ($keyA in $EntriesA.Keys) {
        $normalizedName = Get-NormalizedOpcEntryName $keyA
        $keyB = $bKeyByNormalizedName[$normalizedName]

        $normalizedBytesA = Get-NormalizedOpcEntryBytes -Name $keyA -Bytes ([byte[]]$EntriesA[$keyA])
        $normalizedBytesB = Get-NormalizedOpcEntryBytes -Name $keyB -Bytes ([byte[]]$EntriesB[$keyB])

        if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$normalizedBytesA, [byte[]]$normalizedBytesB)) {
            $mismatches += $normalizedName
        }
    }

    return [pscustomobject]@{
        Passed            = ($mismatches.Count -eq 0)
        EntrySetMismatch  = $false
        NamesA            = @($namesA)
        NamesB            = @($namesB)
        ContentMismatches = $mismatches
    }
}
