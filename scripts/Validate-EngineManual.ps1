[CmdletBinding()]
param(
    [string]$EngineRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
$EngineRoot = [System.IO.Path]::GetFullPath($EngineRoot)
$EngineManifestPath = Join-Path $EngineRoot 'engine.json'
$ManualManifestPath = Join-Path $EngineRoot 'Documentation/EngineManual/manual.json'
$EngineManifest = Get-Content -Raw -LiteralPath $EngineManifestPath | ConvertFrom-Json
$ManualManifest = Get-Content -Raw -LiteralPath $ManualManifestPath | ConvertFrom-Json

if ($ManualManifest.currentVersion -ne $EngineManifest.version) {
    throw "Engine manual currentVersion '$($ManualManifest.currentVersion)' does not match engine version '$($EngineManifest.version)'."
}

$VersionRoot = Join-Path $EngineRoot "Documentation/EngineManual/versions/$($ManualManifest.currentVersion)"
$EntryPoints = @(
    (Join-Path $VersionRoot 'user/index.html'),
    (Join-Path $VersionRoot 'api/index.html')
)
$CheckedLinks = 0
foreach ($EntryPoint in $EntryPoints) {
    if (!(Test-Path -LiteralPath $EntryPoint -PathType Leaf)) {
        throw "Engine manual entry point is missing: $EntryPoint"
    }

    $Html = Get-Content -Raw -LiteralPath $EntryPoint
    if ($Html -notmatch '<h2>本版更新</h2>' -or $Html -notmatch 'id="updates"') {
        throw "Engine manual entry point is missing its 本版更新 section: $EntryPoint"
    }

    foreach ($Match in [regex]::Matches($Html, 'href="([^"]+)"')) {
        $Reference = $Match.Groups[1].Value
        if ($Reference.StartsWith('#') -or
            $Reference.StartsWith('http://') -or
            $Reference.StartsWith('https://') -or
            $Reference.StartsWith('mailto:')) {
            continue
        }

        $RelativePath = $Reference.Split('#', 2)[0]
        if ($RelativePath.Length -eq 0) {
            continue
        }

        $Target = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $EntryPoint -Parent) $RelativePath))
        if (!(Test-Path -LiteralPath $Target)) {
            throw "Broken local manual link '$Reference' in '$EntryPoint'."
        }
        $CheckedLinks++
    }
}

Write-Output "PASS: engine manual $($ManualManifest.currentVersion); entryPoints=$($EntryPoints.Count); localLinks=$CheckedLinks"
