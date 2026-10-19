param(
    [string]$ManualRoot = (Join-Path (Get-Location) 'Documentation/EngineManual')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ManualCondition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "Engine manual validation failed: $Message"
    }
}

$ResolvedManualRoot = [System.IO.Path]::GetFullPath($ManualRoot)
$ManifestPath = Join-Path $ResolvedManualRoot 'manual.json'
$RootIndexPath = Join-Path $ResolvedManualRoot 'index.html'
$RepositoryRoot = Split-Path (Split-Path $ResolvedManualRoot -Parent) -Parent
$EngineManifestPath = Join-Path $RepositoryRoot 'engine.json'

Assert-ManualCondition (Test-Path -LiteralPath $ManifestPath -PathType Leaf) "Missing $ManifestPath"
Assert-ManualCondition (Test-Path -LiteralPath $RootIndexPath -PathType Leaf) "Missing $RootIndexPath"
Assert-ManualCondition (Test-Path -LiteralPath $EngineManifestPath -PathType Leaf) "Missing $EngineManifestPath"

$Manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
$EngineManifest = Get-Content -Raw -LiteralPath $EngineManifestPath | ConvertFrom-Json
Assert-ManualCondition ($Manifest.schemaVersion -eq 1) 'manual.json schemaVersion must be 1.'
Assert-ManualCondition (-not [string]::IsNullOrWhiteSpace([string]$Manifest.currentVersion)) 'manual.json currentVersion is required.'
Assert-ManualCondition ($null -ne $Manifest.versions) 'manual.json versions is required.'

$CurrentVersion = @($Manifest.versions | Where-Object { $_.version -eq $Manifest.currentVersion })
Assert-ManualCondition ($CurrentVersion.Count -eq 1) "Current version '$($Manifest.currentVersion)' must have exactly one manifest entry."
Assert-ManualCondition ([string]$EngineManifest.version -eq [string]$Manifest.currentVersion) "Manual currentVersion '$($Manifest.currentVersion)' must match engine.json version '$($EngineManifest.version)'."
Assert-ManualCondition ([string]$CurrentVersion[0].engineVersion -eq [string]$EngineManifest.version) 'Current manual entry engineVersion must match engine.json version.'

$VersionRoot = Join-Path $ResolvedManualRoot (Join-Path 'versions' ([string]$Manifest.currentVersion))
$RequiredFiles = @(
    'index.html',
    'styles.css',
    'user/index.html',
    'api/index.html'
)

foreach ($RequiredFile in $RequiredFiles) {
    Assert-ManualCondition (Test-Path -LiteralPath (Join-Path $VersionRoot $RequiredFile) -PathType Leaf) "Missing current-version file '$RequiredFile'."
}

foreach ($ManifestPathValue in @($CurrentVersion[0].entryPoint, $CurrentVersion[0].userManual, $CurrentVersion[0].apiIndex)) {
    Assert-ManualCondition (-not [string]::IsNullOrWhiteSpace([string]$ManifestPathValue)) 'Current manual entry contains an empty document path.'
    $ManifestDocumentPath = Join-Path $ResolvedManualRoot ([string]$ManifestPathValue -replace '/', '\')
    Assert-ManualCondition (Test-Path -LiteralPath $ManifestDocumentPath -PathType Leaf) "Manifest document path '$ManifestPathValue' does not exist."
}

$PageChecks = @(
    @{ Path = Join-Path $VersionRoot 'user/index.html'; Heading = '用户手册' },
    @{ Path = Join-Path $VersionRoot 'api/index.html'; Heading = 'API 索引' }
)

foreach ($PageCheck in $PageChecks) {
    $PageText = Get-Content -Raw -LiteralPath $PageCheck.Path
    Assert-ManualCondition ($PageText -match [regex]::Escape($PageCheck.Heading)) "Missing heading '$($PageCheck.Heading)' in '$($PageCheck.Path)'."
    Assert-ManualCondition ($PageText -match '本版更新') "Missing '本版更新' section in '$($PageCheck.Path)'."
}

$HtmlFiles = Get-ChildItem -LiteralPath $ResolvedManualRoot -Recurse -Filter '*.html' -File
foreach ($HtmlFile in $HtmlFiles) {
    $HtmlText = Get-Content -Raw -LiteralPath $HtmlFile.FullName
    $Matches = [regex]::Matches($HtmlText, '(?i)href\s*=\s*["'']([^"''#]+)["'']')
    foreach ($Match in $Matches) {
        $Href = $Match.Groups[1].Value
        if ($Href -match '^(?i)(https?:|mailto:|javascript:)') {
            continue
        }

        $TargetPath = Join-Path $HtmlFile.DirectoryName $Href
        Assert-ManualCondition (Test-Path -LiteralPath $TargetPath -PathType Leaf) "Broken local link '$Href' in '$($HtmlFile.FullName)'."
    }
}

Write-Output "Engine manual validation passed: version $($Manifest.currentVersion)"
