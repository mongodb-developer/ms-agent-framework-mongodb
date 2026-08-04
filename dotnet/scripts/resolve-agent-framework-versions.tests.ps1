#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AgentFrameworkCompatibility.ps1')
$script:Failures = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "[ OK ] $Message" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Message" -ForegroundColor Red; $script:Failures++ }
}

$versions = @{
    'Microsoft.Agents.AI.Abstractions' = @('1.0.0', '1.1.0', '1.2.0-preview.1', '2.0.0')
    'Microsoft.Agents.AI.Workflows' = @('1.0.0', '1.1.0', '1.2.0-preview.1', '1.9.0')
}
$stable = Select-AgentFrameworkVersions -PackageVersions $versions -Mode StablePair
Assert-True (($stable.Versions -join ',') -eq '1.0.0,1.1.0') 'StablePair selects previous and latest common stable versions'

$dispatch = Select-AgentFrameworkVersions -PackageVersions $versions -Mode AllDispatch -ExactVersion '1.0.0'
Assert-True (($dispatch.Versions -join ',') -eq '1.1.0,1.2.0-preview.1,1.0.0') 'Dispatch selects latest stable, preview, and optional exact without substitution'

$withoutPreview = @{
    'Microsoft.Agents.AI.Abstractions' = @('1.0.0', '1.1.0')
    'Microsoft.Agents.AI.Workflows' = @('1.0.0', '1.1.0')
}
$noPreview = Select-AgentFrameworkVersions -PackageVersions $withoutPreview -Mode StableAndPreview
Assert-True (-not $noPreview.PreviewAvailable) 'Missing preview is explicitly reported unavailable'
Assert-True (($noPreview.Versions -join ',') -eq '1.1.0') 'Missing preview never substitutes another stable version'

$threw = $false
try { Select-AgentFrameworkVersions -PackageVersions $versions -Mode Exact -ExactVersion '9.9.9' | Out-Null }
catch { $threw = $true }
Assert-True $threw 'Unlisted or non-common exact version fails closed'

if ($script:Failures -gt 0) { exit 1 }
Write-Host 'All Agent Framework resolver self-tests PASSED.' -ForegroundColor Green
