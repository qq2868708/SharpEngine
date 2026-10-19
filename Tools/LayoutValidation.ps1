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

$Valid = New-EngineFixture 'Valid'
Test-Layout $Valid '' $true
Test-Layout $Valid 'Target' $true
foreach ($DirectoryName in @('Binaries', 'Intermediate'))
{
    foreach ($TargetName in @('Editor', 'Game', 'Tools'))
    {
        New-Directory (Join-Path $Valid "$DirectoryName/Win64/Debug/$TargetName")
    }
}
Test-Layout $Valid '' $true
Test-Layout $Valid 'Target' $true
Test-Layout $Valid 'Target' $true -Target Game

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

Write-Output "PASS: $Assertions layout assertions"
Write-Output "Fixtures: $FixtureRoot"
