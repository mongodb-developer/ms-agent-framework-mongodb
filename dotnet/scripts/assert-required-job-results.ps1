#Requires -Version 7.0
<#
.SYNOPSIS
    Fails a stable aggregate status unless every required GitHub Actions dependency succeeded.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DotNetQualityResult,
    [Parameter(Mandatory)][string]$CompatibilityResult
)

$required = [ordered]@{
    'dotnet-quality' = $DotNetQualityResult
    'dotnet-agent-framework-compat' = $CompatibilityResult
}
$nonSuccess = @($required.GetEnumerator() | Where-Object { $_.Value -cne 'success' })
foreach ($dependency in $required.GetEnumerator()) {
    Write-Host "$($dependency.Key): $($dependency.Value)"
}
if ($nonSuccess.Count -gt 0) {
    $details = $nonSuccess | ForEach-Object { "$($_.Key)=$($_.Value)" }
    throw "Required readiness dependencies did not all succeed: $($details -join ', ')."
}
