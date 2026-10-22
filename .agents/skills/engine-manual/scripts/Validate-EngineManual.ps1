[CmdletBinding()]
param(
    [string]$ManualRoot = (Join-Path (Get-Location) 'Documentation/EngineManual')
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$ResolvedManualRoot = [System.IO.Path]::GetFullPath($ManualRoot)
$EngineRoot = Split-Path (Split-Path $ResolvedManualRoot -Parent) -Parent
& (Join-Path $RepositoryRoot 'scripts/Validate-EngineManual.ps1') -EngineRoot $EngineRoot -ManualRoot $ResolvedManualRoot
