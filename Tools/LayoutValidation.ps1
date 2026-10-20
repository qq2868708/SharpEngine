[CmdletBinding()]
param(
    [string]$EngineRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$EngineRoot)
{
    $EngineRoot = Split-Path $PSScriptRoot -Parent
}
$EngineRoot = [System.IO.Path]::GetFullPath($EngineRoot)
$Checker = Join-Path $EngineRoot 'Tools/SharpEngineLayout.ps1'
$FixtureRoot = Join-Path $EngineRoot ('Saved/Validation/Layout/' + [Guid]::NewGuid().ToString('N'))
$Assertions = 0

function New-Directory([string]$Path)
{
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function New-EngineFixture([string]$Name)
{
    $Root = Join-Path $FixtureRoot $Name
    foreach ($RelativePath in @('engine.json', 'Build/SharpEngine.Build.props', 'Config/DefaultEditorLayout.json'))
    {
        $Destination = Join-Path $Root $RelativePath
        New-Directory (Split-Path $Destination -Parent)
        Copy-Item -LiteralPath (Join-Path $EngineRoot $RelativePath) -Destination $Destination
    }
    return $Root
}

function Test-Layout([string]$Root, [string]$Scope, [bool]$ExpectedSuccess,
    [string]$ExpectedDiagnostic = '', [string]$Target = 'Editor', [bool]$IsProject = $false)
{
    $Arguments = @{
        Mode = 'Check'
        Target = $Target
    }
    if ($IsProject)
    {
        $Arguments.ProjectFile = $Root
    }
    else
    {
        $Arguments.EngineRoot = $Root
    }
    if ($Scope)
    {
        $Arguments.Scope = $Scope
    }

    $Succeeded = $true
    $Diagnostic = ''
    try
    {
        $Output = & $Checker @Arguments
    }
    catch
    {
        $Succeeded = $false
        $Diagnostic = $_.Exception.Message
    }
    if ($Succeeded -ne $ExpectedSuccess -or
        ($ExpectedDiagnostic -and !$Diagnostic.Contains($ExpectedDiagnostic)))
    {
        throw "Layout regression: root=$Root scope=$Scope target=$Target success=$Succeeded diagnostic=$Diagnostic"
    }
    $script:Assertions++
}

function Test-RejectedBuildOutput([string[]]$Properties,
    [string]$ExpectedDiagnostic, [string[]]$ForbiddenPaths)
{
    foreach ($ForbiddenPath in $ForbiddenPaths)
    {
        if (Test-Path -LiteralPath $ForbiddenPath)
        {
            throw "Rejected build fixture already exists: $ForbiddenPath"
        }
    }

    $RuntimeProject = Join-Path $EngineRoot `
        'Source/SharpEngine.Runtime/SharpEngine.Runtime.csproj'
    $Arguments = [System.Collections.Generic.List[string]]::new()
    $Arguments.AddRange([string[]]@('build', $RuntimeProject, '--no-restore',
        '--nologo'))
    foreach ($Property in $Properties)
    {
        $Arguments.Add("-p:$Property")
    }

    $Output = (& dotnet $Arguments 2>&1 | Out-String)
    $ExitCode = $LASTEXITCODE
    if ($ExitCode -eq 0 -or !$Output.Contains($ExpectedDiagnostic))
    {
        throw "Expected rejected build diagnostic '$ExpectedDiagnostic': $Output"
    }

    foreach ($ForbiddenPath in $ForbiddenPaths)
    {
        if (Test-Path -LiteralPath $ForbiddenPath)
        {
            throw "Rejected build generated an illegal output path: $ForbiddenPath"
        }
    }
    $script:Assertions++
}

function Test-RejectedShaderOutput([string]$OutputDirectory)
{
    if (Test-Path -LiteralPath $OutputDirectory)
    {
        throw "Rejected shader fixture already exists: $OutputDirectory"
    }
    $Builder = Join-Path $EngineRoot 'Tools/BuildRenderShaders.ps1'
    $Diagnostic = ''
    try
    {
        $Diagnostic = (& $Builder -EngineRoot $EngineRoot `
            -OutputDirectory $OutputDirectory 2>&1 | Out-String)
    }
    catch
    {
        $Diagnostic += $_.Exception.Message
    }
    if (!$Diagnostic.Contains('SEOUTPUT014'))
    {
        throw "Expected rejected shader output diagnostic SEOUTPUT014: $Diagnostic"
    }
    if (Test-Path -LiteralPath $OutputDirectory)
    {
        throw "Rejected shader build generated an illegal output path: $OutputDirectory"
    }
    $script:Assertions++
}

$Valid = New-EngineFixture 'Valid'
Test-Layout $Valid '' $true
Test-Layout $Valid 'Target' $true
foreach ($DirectoryName in @('Binaries', 'Intermediate'))
{
    foreach ($TargetName in @('Editor', 'Game', 'Server', 'Tools'))
    {
        New-Directory (Join-Path $Valid "$DirectoryName/Win64/Debug/$TargetName")
    }
}
Test-Layout $Valid '' $true
Test-Layout $Valid 'Target' $true
Test-Layout $Valid 'Target' $true -Target Game
Test-Layout $Valid 'Target' $true -Target Server
New-Directory (Join-Path $Valid 'Intermediate/RenderCache')
Test-Layout $Valid '' $true

$UnrelatedPaths = @(
    'Binaries/Win64/Debug/OtherValidation',
    'Intermediate/Win64/Debug/OtherValidation',
    'Binaries/Win64/Experimental/Editor',
    'Intermediate/FirstUseSmoke',
    'Build/Validation'
)
for ($Index = 0; $Index -lt $UnrelatedPaths.Count; $Index++)
{
    $Root = New-EngineFixture "Unrelated-$Index"
    $OutputDirectory = Join-Path $Root $UnrelatedPaths[$Index]
    New-Directory $OutputDirectory
    $Sentinel = Join-Path $OutputDirectory 'preserve.txt'
    Set-Content -LiteralPath $Sentinel -Value 'Preserve validation output'
    $Before = (Get-FileHash -LiteralPath $Sentinel).Hash
    Test-Layout $Root '' $false
    Test-Layout $Root 'Target' $true
    if ((Get-FileHash -LiteralPath $Sentinel).Hash -ne $Before)
    {
        throw "Layout check mutated unrelated output: $Sentinel"
    }
    $Assertions++
}

foreach ($DirectoryName in @('Binaries', 'Intermediate'))
{
    $Segments = @($DirectoryName, 'Win64', 'Debug', 'Editor')
    for ($Index = 0; $Index -lt $Segments.Count; $Index++)
    {
        $Root = New-EngineFixture "$DirectoryName-File-$Index"
        $FilePath = $Root
        foreach ($Segment in $Segments[0..$Index])
        {
            $FilePath = Join-Path $FilePath $Segment
        }
        New-Directory (Split-Path $FilePath -Parent)
        Set-Content -LiteralPath $FilePath -Value 'Not an output directory'
        Test-Layout $Root 'Target' $false 'Output path must be a directory'
    }
}

$InvalidContract = New-EngineFixture 'InvalidContract'
$ManifestPath = Join-Path $InvalidContract 'engine.json'
$Manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$Manifest.schemaVersion = 999
$Manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ManifestPath
Test-Layout $InvalidContract '' $false 'schemaVersion must be 2'
Test-Layout $InvalidContract 'Target' $false 'schemaVersion must be 2'

$ProjectRoot = Join-Path $FixtureRoot 'Project'
New-Directory (Join-Path $ProjectRoot 'Source/Runtime')
Set-Content -LiteralPath (Join-Path $ProjectRoot 'Source/Runtime/Runtime.csproj') -Value '<Project />'
$ProjectFile = Join-Path $ProjectRoot 'Fixture.sharpproject'
Set-Content -LiteralPath $ProjectFile -Value '{"schemaVersion":3,"engine":{"association":"fixture"},"modules":["Source/Runtime/Runtime.csproj"]}'
Test-Layout $ProjectFile '' $true -IsProject $true
New-Directory (Join-Path $ProjectRoot 'Intermediate/Win64/Debug/OtherValidation')
Test-Layout $ProjectFile '' $false 'Unknown Intermediate target entry' -IsProject $true
Test-Layout $ProjectFile 'Target' $true -IsProject $true
New-Directory (Join-Path $ProjectRoot 'Binaries/Win64/Debug')
Set-Content -LiteralPath (Join-Path $ProjectRoot 'Binaries/Win64/Debug/Game') -Value 'Not a directory'
Test-Layout $ProjectFile 'Target' $false 'Output path must be a directory' -Target Game -IsProject $true
Test-Layout $ProjectFile 'Target' $false 'Projects support only these Targets' -Target Tools -IsProject $true

$ForbiddenTarget = 'ForbiddenValidation'
Test-RejectedBuildOutput @("SharpEngineOutputTarget=$ForbiddenTarget") 'SEOUTPUT003' @(
    (Join-Path $EngineRoot "Binaries/Win64/Debug/$ForbiddenTarget"),
    (Join-Path $EngineRoot "Intermediate/Win64/Debug/$ForbiddenTarget"))

$RejectedRoot = Join-Path $FixtureRoot 'RejectedBuildOutput'
$RejectedBinaries = Join-Path $RejectedRoot 'Binaries/TempValidation'
$RejectedIntermediate = Join-Path $RejectedRoot 'Intermediate/TempValidation'
Test-RejectedBuildOutput @(
    "SharpEngineBinariesDir=$RejectedBinaries\",
    "SharpEngineIntermediateDir=$RejectedIntermediate\") 'SEOUTPUT005' @(
    $RejectedBinaries, $RejectedIntermediate)

$RejectedOutput = Join-Path $RejectedRoot 'DirectOutput'
Test-RejectedBuildOutput @("OutputPath=$RejectedOutput\") 'SEOUTPUT007' @(
    $RejectedOutput)

$RejectedBaseIntermediate = Join-Path $RejectedRoot 'DirectIntermediate'
Test-RejectedBuildOutput @(
    "BaseIntermediateOutputPath=$RejectedBaseIntermediate\") 'SEOUTPUT009' @(
    $RejectedBaseIntermediate)

Test-RejectedBuildOutput @('Configuration=Release') 'SEOUTPUT002' @(
    (Join-Path $EngineRoot 'Binaries/Win64/Release'),
    (Join-Path $EngineRoot 'Intermediate/Win64/Release'))

$RejectedShaderOutput = Join-Path $EngineRoot 'Intermediate/ShaderGuardValidation'
Test-RejectedShaderOutput $RejectedShaderOutput

Write-Output "PASS: $Assertions layout assertions"
Write-Output "Fixtures: $FixtureRoot"
