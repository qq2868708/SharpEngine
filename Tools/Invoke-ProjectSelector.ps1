[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EngineRoot,
    [ValidateSet('Debug', 'Development', 'Shipping')][string]$Configuration = 'Debug',
    [ValidateSet('Win64')][string]$Platform = 'Win64',
    [Parameter(Mandatory = $true)][string[]]$SelectorArguments
)

$ErrorActionPreference = 'Stop'
$engineFull = [System.IO.Path]::GetFullPath($EngineRoot)
$manifest = Get-Content -Raw -LiteralPath (Join-Path $engineFull 'engine.json') | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2)
{
    throw 'Engine manifest schemaVersion must be 2.'
}
$configurations = @($Configuration, 'Development', 'Debug', 'Shipping') | Select-Object -Unique
$selector = $null
foreach ($candidate in $configurations)
{
    $directory = ([string]$manifest.paths.binaries).Replace('{platform}', $Platform).
        Replace('{configuration}', $candidate).Replace('{target}', 'Tools')
    $executable = Join-Path (Join-Path $engineFull $directory) 'SharpEngine.ProjectSelector.exe'
    if (Test-Path -LiteralPath $executable -PathType Leaf)
    {
        $selector = $executable
        break
    }
}
if ($selector)
{
    & $selector @SelectorArguments
}
else
{
    $source = Join-Path $engineFull 'Source\SharpEngine.ProjectSelector\SharpEngine.ProjectSelector.csproj'
    if (!(Test-Path -LiteralPath $source -PathType Leaf))
    {
        throw "The engine distribution is missing the Project Selector executable: $engineFull"
    }
    & dotnet run --project $source -c $Configuration -- @SelectorArguments
}
if ($LASTEXITCODE -ne 0)
{
    throw "SharpEngine Project Selector failed with exit code $LASTEXITCODE"
}
