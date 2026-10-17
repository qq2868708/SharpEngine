[CmdletBinding()]
param(
    [ValidateSet('Build','Generate','Clean')][string]$Command = 'Build',
    [string]$EngineRoot = (Split-Path $PSScriptRoot -Parent),
    [ValidateSet('Editor','Game')][string]$Target = 'Editor',
    [ValidateSet('Debug','Development','Shipping')][string]$Configuration = 'Debug',
    [ValidateSet('Win64')][string]$Platform = 'Win64'
)
$ErrorActionPreference = 'Stop'
if ($Command -eq 'Clean') {
    & (Join-Path $EngineRoot 'Tools/SharpEngineLayout.ps1') -EngineRoot $EngineRoot -Mode Clean -Target $Target -Configuration $Configuration -Platform $Platform
    return
}
$verb = if ($Command -eq 'Generate') { 'generate-engine' } else { 'build-engine' }
& dotnet run --project (Join-Path $EngineRoot 'Source/SharpEngine.ProjectSelector/SharpEngine.ProjectSelector.csproj') -- $verb --engine-root $EngineRoot --target $Target --configuration $Configuration --platform $Platform --no-ui
if ($LASTEXITCODE) { throw "Engine $Command failed: $LASTEXITCODE" }
