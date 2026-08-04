#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'HistoryQuickstart',
        'IncrementalIngestionQuickstart',
        'IndexManagementQuickstart',
        'MemoryQuickstart',
        'ParentDocumentRAGQuickstart',
        'RAGQuickstart',
        'SessionPersistenceQuickstart',
        'WorkflowCheckpointResumeQuickstart')]
    [string] $Sample,

    [string] $EnvironmentFile,

    [switch] $ValidateOnly
)

$ErrorActionPreference = 'Stop'
$sampleDirectory = Join-Path $PSScriptRoot $Sample
$projectPath = Join-Path $sampleDirectory "$Sample.csproj"
$environmentPath = if ($EnvironmentFile) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EnvironmentFile)
}
else {
    Join-Path $sampleDirectory '.env'
}

if (-not (Test-Path -LiteralPath $environmentPath -PathType Leaf)) {
    throw "Environment file '$environmentPath' was not found. Copy '$sampleDirectory\.env.example' to '$sampleDirectory\.env' and populate it."
}

$loadedNames = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)

foreach ($entry in Get-Content -LiteralPath $environmentPath) {
    $line = $entry.Trim()
    if ($line.Length -eq 0 -or $line.StartsWith('#', [System.StringComparison]::Ordinal)) {
        continue
    }

    if ($line -notmatch '^([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') {
        throw "Invalid entry in '$environmentPath': '$entry'. Expected NAME=VALUE."
    }

    $name = $Matches[1]
    $value = $Matches[2].Trim()
    if (-not $loadedNames.Add($name)) {
        throw "Duplicate environment variable '$name' in '$environmentPath'."
    }

    if ($value.Length -ge 2) {
        $first = $value[0]
        $last = $value[$value.Length - 1]
        if (($first -eq '"' -and $last -eq '"') -or ($first -eq "'" -and $last -eq "'")) {
            $value = $value.Substring(1, $value.Length - 2)
        }
    }

    if ($value.StartsWith('REPLACE_WITH_', [System.StringComparison]::Ordinal)) {
        throw "Populate '$name' in '$environmentPath' before running the sample."
    }

    if ($null -eq [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::Process)) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $value,
            [EnvironmentVariableTarget]::Process)
    }
}

if ($loadedNames.Count -eq 0) {
    throw "Environment file '$environmentPath' did not contain any variables."
}

if ($ValidateOnly) {
    Write-Output "Validated $environmentPath"
    Write-Output "Variables: $([string]::Join(', ', ($loadedNames | Sort-Object)))"
    exit 0
}

& dotnet run --project $projectPath
exit $LASTEXITCODE
