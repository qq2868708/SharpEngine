[CmdletBinding()]
param(
    [string]$EngineRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$ManualRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ManualCondition
{
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition)
    {
        throw "Engine manual validation failed: $Message"
    }
}

$ResolvedEngineRoot = [System.IO.Path]::GetFullPath($EngineRoot)
if ([string]::IsNullOrWhiteSpace($ManualRoot))
{
    $ManualRoot = Join-Path $ResolvedEngineRoot 'Documentation/EngineManual'
}
$ResolvedManualRoot = [System.IO.Path]::GetFullPath($ManualRoot)
$ManifestPath = Join-Path $ResolvedManualRoot 'manual.json'
$RootIndexPath = Join-Path $ResolvedManualRoot 'index.html'
$EngineManifestPath = Join-Path $ResolvedEngineRoot 'engine.json'

Assert-ManualCondition (Test-Path -LiteralPath $ManifestPath -PathType Leaf) "Missing $ManifestPath"
Assert-ManualCondition (Test-Path -LiteralPath $RootIndexPath -PathType Leaf) "Missing $RootIndexPath"
Assert-ManualCondition (Test-Path -LiteralPath $EngineManifestPath -PathType Leaf) "Missing $EngineManifestPath"

$Manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
$EngineManifest = Get-Content -Raw -LiteralPath $EngineManifestPath | ConvertFrom-Json
Assert-ManualCondition ($Manifest.schemaVersion -eq 1) 'manual.json schemaVersion must be 1.'
Assert-ManualCondition (-not [string]::IsNullOrWhiteSpace([string]$Manifest.currentVersion)) 'manual.json currentVersion is required.'
Assert-ManualCondition ($null -ne $Manifest.versions) 'manual.json versions is required.'

$CurrentVersions = @($Manifest.versions | Where-Object { $_.version -eq $Manifest.currentVersion })
Assert-ManualCondition ($CurrentVersions.Count -eq 1) "Current version '$($Manifest.currentVersion)' must have exactly one manifest entry."
$CurrentVersion = $CurrentVersions[0]
Assert-ManualCondition ([string]$EngineManifest.version -eq [string]$Manifest.currentVersion) 'Manual version must match engine.json version.'
Assert-ManualCondition ([string]$CurrentVersion.engineVersion -eq [string]$EngineManifest.version) 'Current manual entry engineVersion must match engine.json version.'

$VersionRoot = Join-Path $ResolvedManualRoot (Join-Path 'versions' ([string]$Manifest.currentVersion))
foreach ($RequiredFile in @('index.html', 'styles.css', 'user/index.html', 'api/index.html'))
{
    Assert-ManualCondition (Test-Path -LiteralPath (Join-Path $VersionRoot $RequiredFile) -PathType Leaf) "Missing current-version file '$RequiredFile'."
}

foreach ($ManifestPathValue in @($CurrentVersion.entryPoint, $CurrentVersion.userManual, $CurrentVersion.apiIndex))
{
    Assert-ManualCondition (-not [string]::IsNullOrWhiteSpace([string]$ManifestPathValue)) 'Current manual entry contains an empty document path.'
    $ManifestDocumentPath = Join-Path $ResolvedManualRoot ([string]$ManifestPathValue)
    Assert-ManualCondition (Test-Path -LiteralPath $ManifestDocumentPath -PathType Leaf) "Manifest document path '$ManifestPathValue' does not exist."
}

$PageChecks = @(
    @{ Path = Join-Path $VersionRoot 'user/index.html'; Heading = '用户手册' },
    @{ Path = Join-Path $VersionRoot 'api/index.html'; Heading = 'API 索引' }
)
foreach ($PageCheck in $PageChecks)
{
    $PageText = Get-Content -Raw -LiteralPath $PageCheck.Path
    Assert-ManualCondition ($PageText -match [regex]::Escape($PageCheck.Heading)) "Missing heading '$($PageCheck.Heading)' in '$($PageCheck.Path)'."
    Assert-ManualCondition ($PageText -match '<h2>本版更新</h2>' -and $PageText -match 'id="updates"') "Missing 本版更新 section in '$($PageCheck.Path)'."
}

$CheckedLinks = 0
$HtmlFiles = Get-ChildItem -LiteralPath $ResolvedManualRoot -Recurse -Filter '*.html' -File
foreach ($HtmlFile in $HtmlFiles)
{
    $HtmlText = Get-Content -Raw -LiteralPath $HtmlFile.FullName
    $Matches = [regex]::Matches($HtmlText, '(?i)href\s*=\s*["'']([^"'']+)["'']')
    foreach ($Match in $Matches)
    {
        $Href = $Match.Groups[1].Value
        if ($Href -match '^(?i)(#|https?:|mailto:|javascript:)')
        {
            continue
        }
        $RelativePath = $Href.Split('#', 2)[0]
        if ($RelativePath.Length -eq 0)
        {
            continue
        }
        $TargetPath = [System.IO.Path]::GetFullPath((Join-Path $HtmlFile.DirectoryName $RelativePath))
        Assert-ManualCondition (Test-Path -LiteralPath $TargetPath -PathType Leaf) "Broken local link '$Href' in '$($HtmlFile.FullName)'."
        $CheckedLinks++
    }
}

Write-Output "PASS: engine manual $($Manifest.currentVersion); htmlFiles=$($HtmlFiles.Count); localLinks=$CheckedLinks"
