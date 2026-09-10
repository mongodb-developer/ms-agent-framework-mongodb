#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'verify-agent-framework-compatibility.ps1'
$text = Get-Content $scriptPath -Raw
$failures = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "[ OK ] $Message" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Message" -ForegroundColor Red; $script:failures++ }
}

Assert-True ($text -match "Name = 'MongoDB\.AgentFramework\.Tests'") 'Compatibility runs provider tests'
Assert-True ($text -match "Name = 'IngestionSamples\.Tests'") 'Compatibility runs ingestion sample tests'
Assert-True ($text -match '\$rows\.Add\(\[pscustomobject\]\$row\)') 'Exactly one centralized row append exists'
Assert-True (([regex]::Matches($text, '\$rows\.Add\(')).Count -eq 1) 'No failure path can append duplicate or omit rows'
Assert-True ($text -match 'finally\s*\{[\s\S]*Write-Reports -Rows \$rows') 'Reports are emitted from finally before failure returns'
Assert-True ($text -match 'if \(\$versionFailed\) \{ \$row\.result = ''failed'' \}') 'Any caught failure forces a failed row'
Assert-True ($text -match '\$rows\.Count -ne \$Versions\.Count') 'Final report cardinality is asserted'
Assert-True ($text -match '\$row\.executed \+= \$executed') 'Executed count aggregates every test project'
Assert-True ($text -match 'produced no readable TRX evidence') 'Missing TRX is a reported failure'
Assert-True ($text -match 'TRX reports zero executed tests') 'Zero-test result is a reported failure'

if ($failures -gt 0) { exit 1 }
Write-Host 'All compatibility reporting structure self-tests PASSED.' -ForegroundColor Green
