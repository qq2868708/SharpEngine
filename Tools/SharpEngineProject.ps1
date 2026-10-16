[CmdletBinding()]
param(
    [ValidateSet('New', 'Resolve', 'Doctor')]
    [string]$Command = 'New',
    [string]$Name,
    [string]$Directory = (Get-Location).Path,
    [string]$EngineRoot,
    [string]$ProjectFile,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

function Write-Utf8NoBom([string]$Path, [string]$Contents) {
    $parent = Split-Path -Parent $Path
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Contents, $utf8)
}

function Get-FullPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path)
    )
}

function Get-RelativePath([string]$FromDirectory, [string]$ToPath) {
    $from = [System.IO.Path]::GetFullPath($FromDirectory).TrimEnd('\') + '\'
    # Both inputs are directories. Uri treats a path without a trailing
    # separator as a file, which adds one extra parent segment when the target
    # is the distribution root (for example ..\..\..\..\SharpEngine).
    $to = [System.IO.Path]::GetFullPath($ToPath).TrimEnd('\') + '\'
    $fromUri = New-Object System.Uri($from)
    $toUri = New-Object System.Uri($to)
    return $fromUri.MakeRelativeUri($toUri).ToString().Replace('/', '\')
}

function Convert-ToJsonFile([object]$Value, [string]$Path) {
    Write-Utf8NoBom $Path (($Value | ConvertTo-Json -Depth 8) + [Environment]::NewLine)
}

function Resolve-ProjectManifest([string]$Path) {
    $full = Get-FullPath $Path
    if (!(Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Project manifest not found: $full"
    }

    $manifest = Get-Content -Raw -LiteralPath $full | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1) {
        throw "Unsupported project manifest schema: $($manifest.schemaVersion)"
    }
    if (!$manifest.engine -or !$manifest.engine.path) {
        throw "Project manifest has no engine.path: $full"
    }

    $manifestDirectory = Split-Path -Parent $full
    $environmentName = [string]$manifest.engine.pathEnvironment
    $environmentPath = if ($environmentName) { [Environment]::GetEnvironmentVariable($environmentName) } else { $null }
    if ($environmentPath) {
        $engineFull = Get-FullPath $environmentPath
        $source = "environment:$environmentName"
    } else {
        $manifestEnginePath = [string]$manifest.engine.path
        if ([System.IO.Path]::IsPathRooted($manifestEnginePath)) {
            $engineFull = Get-FullPath $manifestEnginePath
        } else {
            $engineFull = Get-FullPath (Join-Path $manifestDirectory $manifestEnginePath)
        }
        $source = 'manifest-relative-path'
    }

    $engineManifestPath = Join-Path $engineFull ([string]$manifest.engine.manifest)
    if (!(Test-Path -LiteralPath $engineManifestPath -PathType Leaf)) {
        throw "Engine manifest not found: $engineManifestPath"
    }
    $engineManifest = Get-Content -Raw -LiteralPath $engineManifestPath | ConvertFrom-Json
    if ($engineManifest.engineId -ne [string]$manifest.engine.id) {
        throw "Engine id mismatch. Project requests '$($manifest.engine.id)', resolved '$($engineManifest.engineId)'."
    }

    [pscustomobject]@{
        ProjectManifest = $full
        ProjectRoot = Split-Path -Parent $manifestDirectory
        EngineRoot = $engineFull
        EngineManifest = $engineManifestPath
        EngineId = [string]$engineManifest.engineId
        EngineVersion = [string]$engineManifest.version
        ResolutionSource = $source
    }
}

function Invoke-NewProject {
    if (!$Name) { throw 'New requires -Name, for example -Name MyGame' }
    if ($Name -notmatch '^[A-Za-z][A-Za-z0-9_.-]*$') {
        throw "Invalid project name '$Name'. Use letters, digits, '.', '_' or '-'."
    }

    $parent = Get-FullPath $Directory
    $projectRoot = Join-Path $parent $Name
    if (Test-Path -LiteralPath $projectRoot) {
        throw "Project directory already exists: $projectRoot"
    }

    $toolRoot = Split-Path -Parent $PSScriptRoot
    $templateRoot = Join-Path $toolRoot 'Templates\BasicProject'
    if (!(Test-Path -LiteralPath $templateRoot -PathType Container) -or
        !(Test-Path -LiteralPath (Join-Path $templateRoot 'Project\project.json.template') -PathType Leaf)) {
        throw "BasicProject template not found at $templateRoot"
    }

    $engineFull = if ($EngineRoot) { Get-FullPath $EngineRoot } else { $toolRoot }
    $engineManifestPath = Join-Path $engineFull 'Engine\engine.json'
    if (!(Test-Path -LiteralPath $engineManifestPath -PathType Leaf)) {
        throw "Engine distribution manifest not found at $engineManifestPath"
    }
    $engineManifest = Get-Content -Raw -LiteralPath $engineManifestPath | ConvertFrom-Json
    $engineRelative = Get-RelativePath (Join-Path $projectRoot 'Project') $engineFull
    # JSON needs escaped backslashes; MSBuild receives the normal Windows path.
    $engineRelativeMsBuild = $engineRelative
    $engineRootExpression = if ([System.IO.Path]::IsPathRooted($engineRelative)) {
        $engineRelative
    } else {
        '$(MSBuildThisFileDirectory)' + $engineRelativeMsBuild
    }
    $projectId = $Name.ToLowerInvariant() -replace '[^a-z0-9]', ''
    if (!$projectId) { $projectId = 'game' }
    $namespace = ($Name -replace '[^A-Za-z0-9_]', '_')
    if ($namespace -match '^[0-9]') { $namespace = "Game_$namespace" }

    $files = @(
        'Project\project.json.template',
        'Project\SharpEngine.props.template',
        'Managed\BasicProject.csproj.template',
        'Managed\Game.cs',
        'Managed\Program.cs.template',
        'Content\UI\Main.ui.xml',
        'Content\Assets\assets.json',
        'Content\Worlds\Main.world.xml',
        'README.md'
    )
    foreach ($relative in $files) {
        $source = Join-Path $templateRoot $relative
        $targetRelative = $relative -replace '\.template$', ''
        if ($targetRelative -eq 'Managed\BasicProject.csproj') {
            $targetRelative = "Managed\$projectId.csproj"
        }
        $target = Join-Path $projectRoot $targetRelative
        $text = Get-Content -Raw -LiteralPath $source
        $text = $text.Replace('__PROJECT_ID__', $projectId)
        $text = $text.Replace('__PROJECT_NAMESPACE__', $namespace)
        $text = $text.Replace('__DISPLAY_NAME__', $Name)
        $text = $text.Replace('__ENGINE_PATH__', $engineRelative.Replace('\', '\\'))
        $text = $text.Replace('__ENGINE_PATH_MSBUILD__', $engineRelativeMsBuild)
        $text = $text.Replace('__ENGINE_ROOT_EXPRESSION__', $engineRootExpression)
        Write-Utf8NoBom $target $text
    }

    $result = [pscustomobject]@{
        ProjectRoot = $projectRoot
        ProjectManifest = Join-Path $projectRoot 'Project\project.json'
        EngineRoot = $engineFull
        EngineVersion = [string]$engineManifest.version
        EnginePathInManifest = $engineRelative
        CopiedEngineFiles = 0
    }
    if ($Json) { $result | ConvertTo-Json -Depth 5 } else {
        Write-Output "Created SharpEngine project: $projectRoot"
        Write-Output "Engine reference: $engineRelative (or `$env:SHARPENGINE_ROOT)"
        Write-Output "Next: dotnet build `"$projectRoot\Managed\$projectId.csproj`""
    }
}

function Invoke-Resolve([string]$Path) {
    $resolved = Resolve-ProjectManifest $Path
    if ($Json) { $resolved | ConvertTo-Json -Depth 5 } else {
        Write-Output "Project: $($resolved.ProjectRoot)"
        Write-Output "Engine:  $($resolved.EngineRoot)"
        Write-Output "Version: $($resolved.EngineVersion)"
        Write-Output "Source:  $($resolved.ResolutionSource)"
    }
}

function Invoke-Doctor([string]$Path) {
    $resolved = Resolve-ProjectManifest $Path
    $required = @('Engine', 'Managed', 'Content', 'Tools')
    $missing = @($required | Where-Object { !(Test-Path -LiteralPath (Join-Path $resolved.EngineRoot $_) -PathType Container) })
    if ($missing.Count -gt 0) { throw "Engine distribution is missing directories: $($missing -join ', ')" }
    if ($Json) {
        [pscustomobject]@{ Healthy = $true; EngineRoot = $resolved.EngineRoot; EngineVersion = $resolved.EngineVersion } | ConvertTo-Json
    } else {
        Write-Output "Healthy: $($resolved.EngineId) $($resolved.EngineVersion)"
        Write-Output "Engine root: $($resolved.EngineRoot)"
    }
}

switch ($Command) {
    'New' { Invoke-NewProject }
    'Resolve' {
        if (!$ProjectFile) { $ProjectFile = Join-Path (Get-Location).Path 'Project\project.json' }
        Invoke-Resolve $ProjectFile
    }
    'Doctor' {
        if (!$ProjectFile) { $ProjectFile = Join-Path (Get-Location).Path 'Project\project.json' }
        Invoke-Doctor $ProjectFile
    }
}
