[CmdletBinding()]
param(
    [ValidateSet('Build','Generate','GenerateProjectFiles','Clean')][string]$Command = 'Build',
    [string]$EngineRoot = (Split-Path $PSScriptRoot -Parent),
    [ValidateSet('Editor','Game','Server')][string]$Target = 'Editor',
    [ValidateSet('Debug','Development','Shipping')][string]$Configuration = 'Debug',
    [ValidateSet('Win64')][string]$Platform = 'Win64',
    [ValidateSet('OpenGL','Both')][string]$RenderBackends = 'Both'
)
$ErrorActionPreference = 'Stop'
if ($RenderBackends -eq 'OpenGL')
{
    $RenderBackends = 'OpenGL'
}
else
{
    $RenderBackends = 'Both'
}
if ([string]::IsNullOrWhiteSpace($env:VULKAN_SDK))
{
    $Sdk = [Environment]::GetEnvironmentVariable('VULKAN_SDK', 'User')
    if ([string]::IsNullOrWhiteSpace($Sdk))
    {
        $Sdk = [Environment]::GetEnvironmentVariable('VULKAN_SDK', 'Machine')
    }
    if (![string]::IsNullOrWhiteSpace($Sdk))
    {
        $env:VULKAN_SDK = $Sdk
    }
}
if ($Command -eq 'Clean') {
    & (Join-Path $EngineRoot 'Tools/SharpEngineLayout.ps1') -EngineRoot $EngineRoot -Mode Clean -Target $Target -Configuration $Configuration -Platform $Platform
    return
}
$verb = if ($Command -in @('Generate', 'GenerateProjectFiles')) {
    'GenerateProjectFiles'
} else { 'build-engine' }
& dotnet run --project (Join-Path $EngineRoot 'Source/SharpEngine.ProjectSelector/SharpEngine.ProjectSelector.csproj') -- $verb --engine-root $EngineRoot --target $Target --configuration $Configuration --platform $Platform --render-backends $RenderBackends --no-ui
if ($LASTEXITCODE) { throw "Engine $Command failed: $LASTEXITCODE" }
