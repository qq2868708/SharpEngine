[CmdletBinding()]
param(
    [string]$EngineRoot,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = Split-Path $PSScriptRoot -Parent
}
$Sdk = $env:VULKAN_SDK
if ([string]::IsNullOrWhiteSpace($Sdk)) {
    throw 'VULKAN_SDK is required to compile RenderCore shaders'
}
$Glslc = Join-Path $Sdk 'Bin/glslc.exe'
$Validator = Join-Path $Sdk 'Bin/spirv-val.exe'
$GlslValidator = Join-Path $Sdk 'Bin/glslangValidator.exe'
if (!(Test-Path $Glslc) -or !(Test-Path $Validator) -or
    !(Test-Path $GlslValidator)) {
    throw "Vulkan shader tools were not found under $Sdk"
}
$SourceDirectory = Join-Path $EngineRoot 'Source/Engine/RenderCore/Shaders'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$Entries = @(
    @{ Name='SceneStaticVert'; File='SceneStatic.vert.glsl'; Stage='vert' },
    @{ Name='SceneSkinnedVert'; File='SceneSkinned.vert.glsl'; Stage='vert' },
    @{ Name='SceneFrag'; File='Scene.frag.glsl'; Stage='frag' }
)
$OpenGlEntries = @(
    @{ Name='OpenGLSceneStaticVert'; File='OpenGL/SceneStatic.vert.glsl'; Stage='vert' },
    @{ Name='OpenGLSceneSkinnedVert'; File='OpenGL/SceneSkinned.vert.glsl'; Stage='vert' },
    @{ Name='OpenGLSceneFrag'; File='OpenGL/Scene.frag.glsl'; Stage='frag' }
)
$Header = Join-Path $OutputDirectory 'SceneShaders.generated.h'
$Builder = [System.Text.StringBuilder]::new()
[void]$Builder.AppendLine('#pragma once')
[void]$Builder.AppendLine('#include <cstdint>')
foreach ($Entry in $Entries) {
    $Source = Join-Path $SourceDirectory $Entry.File
    $Spirv = Join-Path $OutputDirectory ($Entry.Name + '.spv')
    & $Glslc "-fshader-stage=$($Entry.Stage)" $Source -o $Spirv
    if ($LASTEXITCODE) { throw "glslc failed for $Source" }
    & $Validator $Spirv
    if ($LASTEXITCODE) { throw "spirv-val failed for $Spirv" }
    $Bytes = [System.IO.File]::ReadAllBytes($Spirv)
    if (($Bytes.Length % 4) -ne 0) { throw "Invalid SPIR-V byte count: $Spirv" }
    [void]$Builder.AppendLine("inline constexpr uint32_t $($Entry.Name)[] = {")
    for ($Index = 0; $Index -lt $Bytes.Length; $Index += 4) {
        $Word = [BitConverter]::ToUInt32($Bytes, $Index)
        [void]$Builder.Append(('0x{0:X8}u,' -f $Word))
        if ((($Index / 4) % 8) -eq 7) { [void]$Builder.AppendLine() }
    }
    [void]$Builder.AppendLine('};')
}
foreach ($Entry in $OpenGlEntries) {
    $Source = Join-Path $SourceDirectory $Entry.File
    & $GlslValidator '-S' $Entry.Stage $Source
    if ($LASTEXITCODE) { throw "OpenGL shader validation failed for $Source" }
    $SourceText = [System.IO.File]::ReadAllText($Source)
    [void]$Builder.AppendLine(('inline constexpr const char {0}[] = R"SHARPGLSL(' -f $Entry.Name))
    [void]$Builder.Append($SourceText)
    if (!$SourceText.EndsWith("`n")) { [void]$Builder.AppendLine() }
    [void]$Builder.AppendLine(')SHARPGLSL";')
}
$Temporary = $Header + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
[System.IO.File]::WriteAllText($Temporary, $Builder.ToString(),
    [System.Text.UTF8Encoding]::new($false))
Move-Item -Force -LiteralPath $Temporary -Destination $Header
