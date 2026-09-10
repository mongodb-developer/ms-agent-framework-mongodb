#Requires -Version 7.0
<#
.SYNOPSIS
    Self-test for PackageReproducibility.ps1's Test-PackageReproducibility -- proves the double-pack
    reproducibility comparison normalizes ONLY the generated OPC identifiers (psmdcp GUID part name, _rels
    relationship ids) and still detects a genuine content difference inside `_rels/.rels` or `*.psmdcp`, instead
    of excluding those two entries from comparison entirely.

.DESCRIPTION
    A previous version of this comparison excluded `_rels/.rels` and every `*.psmdcp` entry from the byte-level
    comparison completely, matching only the normalized entry NAME set -- so a real content difference inside
    either file (not just the expected generated GUID/relationship-id noise) would never have been caught, even
    though the script's own documentation and console output claimed a "normalized comparison". This self-test
    exercises, using synthetic in-memory entry maps (no real `dotnet pack` required):

      1. Two packs identical in every real payload entry, differing ONLY in the psmdcp GUID part name and the
         _rels/.rels relationship ids/target referencing it (the expected, legitimate NuGet.Client OPC
         packaging non-determinism) -> Passed = $true.
      2. A genuine, non-GUID content difference inside a `*.psmdcp` entry (e.g. a different `<dc:creator>`) ->
         Passed = $false, and the mismatch is attributed to the psmdcp entry specifically.
      3. A genuine, non-relationship-id content difference inside `_rels/.rels` (e.g. a relationship's `Target`
         pointing at a different, wrong file) -> Passed = $false, attributed to `_rels/.rels`.
      4. A genuine difference in a real payload entry (the .dll bytes) -> Passed = $false (regression check:
         normalization must never mask an actual payload difference).
      5. A missing/extra real entry (entry-set mismatch) -> Passed = $false with EntrySetMismatch = $true.
      6. Reintroducing the ORIGINAL bug shape (excluding `_rels/.rels`/`*.psmdcp` from the byte comparison
         entirely, matching only entry names) into a copy of the comparison would make cases 2 and 3 above
         silently PASS -- this file's cases 2/3 are therefore a meaningful regression guard, not a tautology.

    Run directly: pwsh dotnet/scripts/verify-package.reproducibility.tests.ps1
    Exit code 0 = every assertion behaved as expected; exit code 1 = at least one assertion did not.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PackageReproducibility.ps1")

$script:AssertionFailures = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) {
        Write-Host "[ OK ] $Message" -ForegroundColor Green
    }
    else {
        Write-Host "[FAIL] $Message" -ForegroundColor Red
        $script:AssertionFailures++
    }
}

function Get-Utf8Bytes([string]$Text) {
    return [System.Text.Encoding]::UTF8.GetBytes($Text)
}

# A minimal, but structurally realistic, fixture: a real payload dll/xml/nuspec/README entry plus a psmdcp part
# (under a caller-supplied GUID file name) and the _rels/.rels that references it, mirroring the exact real
# MongoDB.AgentFramework.nupkg shape captured during development (see PackageReproducibility.ps1's header).
function New-PackEntryMap {
    param(
        [string]$PsmdcpGuid,
        [string]$NuspecRelId = "R8A14303BD67C3B24",
        [string]$CorePropsRelId = "R9776BD5ED1C44E6B",
        [string]$Creator = "mongo",
        [string]$CorePropsTarget = $null,
        [string]$DllBytesText = "FAKE-DLL-BYTES-V1",
        [string]$NuspecText = "<package><metadata><id>MongoDB.AgentFramework</id></metadata></package>"
    )

    if (-not $CorePropsTarget) {
        $CorePropsTarget = "/package/services/metadata/core-properties/$PsmdcpGuid.psmdcp"
    }

    $psmdcpText = @"
<?xml version="1.0" encoding="utf-8"?>
<coreProperties xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns="http://schemas.openxmlformats.org/package/2006/metadata/core-properties">
  <dc:creator>$Creator</dc:creator>
  <dc:identifier>MongoDB.AgentFramework</dc:identifier>
  <version>0.1.0-preview.1</version>
</coreProperties>
"@

    $relsText = @"
<?xml version="1.0" encoding="utf-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Type="http://schemas.microsoft.com/packaging/2010/07/manifest" Target="/MongoDB.AgentFramework.nuspec" Id="$NuspecRelId" />
  <Relationship Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="$CorePropsTarget" Id="$CorePropsRelId" />
</Relationships>
"@

    return [ordered]@{
        "lib/net8.0/MongoDB.AgentFramework.dll"                                       = Get-Utf8Bytes $DllBytesText
        "MongoDB.AgentFramework.nuspec"                                               = Get-Utf8Bytes $NuspecText
        "README.md"                                                                   = Get-Utf8Bytes "# MongoDB.AgentFramework"
        "_rels/.rels"                                                                 = Get-Utf8Bytes $relsText
        "package/services/metadata/core-properties/$PsmdcpGuid.psmdcp"               = Get-Utf8Bytes $psmdcpText
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Case 1: only the expected, legitimate generated-identifier noise differs -- must PASS.
# ---------------------------------------------------------------------------------------------------------------
$entriesA = New-PackEntryMap -PsmdcpGuid "a743fecc81b543e498ca9ae6e7b2e374"
$entriesB = New-PackEntryMap -PsmdcpGuid "ffeeddccbbaa99887766554433221100" -NuspecRelId "R1122334455667788" -CorePropsRelId "R99887766554433AA"

$result1 = Test-PackageReproducibility -EntriesA $entriesA -EntriesB $entriesB
Assert-True $result1.Passed "Two packs differing ONLY in the psmdcp GUID part name and _rels relationship ids PASS (expected NuGet.Client OPC non-determinism)"
Assert-True (-not $result1.EntrySetMismatch) "Case 1 does not report an entry-set mismatch"
Assert-True ($result1.ContentMismatches.Count -eq 0) "Case 1 reports zero content mismatches"

# ---------------------------------------------------------------------------------------------------------------
# Case 2: a genuine, non-GUID content difference inside the psmdcp entry (a different <dc:creator>) -- this is
# the direct regression proof: the previous implementation excluded *.psmdcp from the byte comparison entirely
# and would have reported this pair as PASSING.
# ---------------------------------------------------------------------------------------------------------------
$entriesC = New-PackEntryMap -PsmdcpGuid "a743fecc81b543e498ca9ae6e7b2e374" -Creator "mongo"
$entriesD = New-PackEntryMap -PsmdcpGuid "ffeeddccbbaa99887766554433221100" -Creator "some-other-unexpected-creator"

$result2 = Test-PackageReproducibility -EntriesA $entriesC -EntriesB $entriesD
Assert-True (-not $result2.Passed) "A genuine <dc:creator> content difference inside the psmdcp entry (not just its GUID) FAILS the comparison"
Assert-True (-not $result2.EntrySetMismatch) "Case 2's failure is a content mismatch, not an entry-set mismatch"
Assert-True (($result2.ContentMismatches | Where-Object { $_ -match '\.psmdcp$' }).Count -eq 1) "Case 2's content mismatch is attributed to the psmdcp entry"

# ---------------------------------------------------------------------------------------------------------------
# Case 3: a genuine content difference inside _rels/.rels that is NOT a relationship id or the psmdcp GUID target
# -- a relationship's Target pointing at a completely different, wrong file. This is the direct regression proof
# for _rels/.rels: the previous implementation excluded it from the byte comparison entirely.
# ---------------------------------------------------------------------------------------------------------------
$entriesE = New-PackEntryMap -PsmdcpGuid "a743fecc81b543e498ca9ae6e7b2e374"
$entriesF = New-PackEntryMap -PsmdcpGuid "ffeeddccbbaa99887766554433221100"
# Corrupt _rels/.rels in $entriesF: point the manifest relationship at the wrong nuspec file name.
$entriesF["_rels/.rels"] = Get-Utf8Bytes (
    [System.Text.Encoding]::UTF8.GetString($entriesF["_rels/.rels"]) -replace 'Target="/MongoDB\.AgentFramework\.nuspec"', 'Target="/SomeOther.Package.nuspec"'
)

$result3 = Test-PackageReproducibility -EntriesA $entriesE -EntriesB $entriesF
Assert-True (-not $result3.Passed) "A genuine Target-path content difference inside _rels/.rels (not just a relationship id or the psmdcp GUID) FAILS the comparison"
Assert-True (($result3.ContentMismatches | Where-Object { $_ -eq "_rels/.rels" }).Count -eq 1) "Case 3's content mismatch is attributed to _rels/.rels"

# ---------------------------------------------------------------------------------------------------------------
# Case 4 (regression guard): a genuine real-payload difference (the actual .dll bytes) must still fail, exactly
# as before this change -- normalization must never spread beyond _rels/.rels and *.psmdcp.
# ---------------------------------------------------------------------------------------------------------------
$entriesG = New-PackEntryMap -PsmdcpGuid "a743fecc81b543e498ca9ae6e7b2e374" -DllBytesText "FAKE-DLL-BYTES-V1"
$entriesH = New-PackEntryMap -PsmdcpGuid "ffeeddccbbaa99887766554433221100" -DllBytesText "FAKE-DLL-BYTES-V2-DIFFERENT"

$result4 = Test-PackageReproducibility -EntriesA $entriesG -EntriesB $entriesH
Assert-True (-not $result4.Passed) "A genuine real-payload (.dll) content difference still FAILS the comparison"
Assert-True (($result4.ContentMismatches | Where-Object { $_ -match '\.dll$' }).Count -eq 1) "Case 4's content mismatch is attributed to the .dll entry"

# ---------------------------------------------------------------------------------------------------------------
# Case 5 (regression guard): a missing/extra real entry is an entry-set mismatch, not silently ignored.
# ---------------------------------------------------------------------------------------------------------------
$entriesI = New-PackEntryMap -PsmdcpGuid "a743fecc81b543e498ca9ae6e7b2e374"
$entriesJ = New-PackEntryMap -PsmdcpGuid "ffeeddccbbaa99887766554433221100"
$entriesJ.Remove("README.md")

$result5 = Test-PackageReproducibility -EntriesA $entriesI -EntriesB $entriesJ
Assert-True (-not $result5.Passed) "A missing real entry (README.md) FAILS the comparison"
Assert-True $result5.EntrySetMismatch "A missing real entry is reported as an EntrySetMismatch, not a content mismatch"

# ---------------------------------------------------------------------------------------------------------------
# Case 6: prove cases 2/3 are a meaningful regression guard by reproducing the ORIGINAL excluded-entirely bug
# shape locally (never touching the production module) and showing it WOULD have silently passed both.
# ---------------------------------------------------------------------------------------------------------------
function Test-PackageReproducibilityOriginalBugShape {
    param($EntriesA, $EntriesB)

    $normalize = { param($name) $name -replace '(core-properties/)[0-9a-f]{32}(\.psmdcp)', '$1{guid}$2' }
    $namesA = @($EntriesA.Keys | ForEach-Object { & $normalize $_ }) | Sort-Object
    $namesB = @($EntriesB.Keys | ForEach-Object { & $normalize $_ }) | Sort-Object
    if (($namesA -join "|") -ne ($namesB -join "|")) {
        return $false
    }

    # THE BUG: entries matching _rels/.rels or *.psmdcp are excluded from the byte comparison entirely.
    $realKeysA = $EntriesA.Keys | Where-Object { $_ -ne "_rels/.rels" -and $_ -notmatch '\.psmdcp$' }
    foreach ($key in $realKeysA) {
        if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$EntriesA[$key], [byte[]]$EntriesB[$key])) {
            return $false
        }
    }

    return $true
}

$originalBugCase2 = Test-PackageReproducibilityOriginalBugShape -EntriesA $entriesC -EntriesB $entriesD
$originalBugCase3 = Test-PackageReproducibilityOriginalBugShape -EntriesA $entriesE -EntriesB $entriesF
Assert-True $originalBugCase2 "Regression proof: the ORIGINAL exclude-entirely comparison shape WOULD have silently passed case 2's psmdcp content difference (confirms Test-PackageReproducibility's case 2 check is meaningful, not tautological)"
Assert-True $originalBugCase3 "Regression proof: the ORIGINAL exclude-entirely comparison shape WOULD have silently passed case 3's _rels/.rels content difference (confirms Test-PackageReproducibility's case 3 check is meaningful, not tautological)"

# ---------------------------------------------------------------------------------------------------------------
Write-Host ""
if ($script:AssertionFailures -gt 0) {
    Write-Host "$($script:AssertionFailures) self-test assertion(s) FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "All package-reproducibility self-test assertions PASSED." -ForegroundColor Green
exit 0
