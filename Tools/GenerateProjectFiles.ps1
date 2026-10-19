[CmdletBinding()]
param(
    [string]$ProjectFile,
    [string]$EngineRoot,
    [ValidateSet('Debug', 'Development', 'Shipping')][string]$Configuration = 'Debug',
    [ValidateSet('Win64')][string]$Platform = 'Win64'
)

$ErrorActionPreference = 'Stop'

function Get-FullPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
}

$defaultEngineRoot = Split-Path $PSScriptRoot -Parent
$engineRoot = if ($EngineRoot) { Get-FullPath $EngineRoot } else { Get-FullPath $defaultEngineRoot }

$selectorArguments = @(
    'GenerateProjectFiles',
    '--configuration', $Configuration,
    '--platform', $Platform,
    '--no-ui'
)
if ($ProjectFile) {
    $selectorArguments += @('--project', (Get-FullPath $ProjectFile))
    if ($EngineRoot) { $selectorArguments += @('--engine-root', $engineRoot) }
} else {
    $selectorArguments += @('--engine-root', $engineRoot)
}

& (Join-Path $PSScriptRoot 'Invoke-ProjectSelector.ps1') -EngineRoot $engineRoot `
    -Configuration $Configuration -Platform $Platform -SelectorArguments $selectorArguments
