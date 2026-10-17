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
$selectorProject = Join-Path $engineRoot 'Source\SharpEngine.ProjectSelector\SharpEngine.ProjectSelector.csproj'
if (!(Test-Path -LiteralPath $selectorProject -PathType Leaf)) {
    throw "SharpEngine Project Selector was not found: $selectorProject"
}

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

& dotnet run --project $selectorProject -- $selectorArguments
if ($LASTEXITCODE -ne 0) {
    throw "SharpEngine project-file generation failed with exit code $LASTEXITCODE"
}
