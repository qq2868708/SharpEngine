[CmdletBinding()]
param(
    [ValidateSet('New', 'Resolve', 'Doctor', 'Build')]
    [string]$Command = 'New',
    [string]$Name,
    [string]$Directory = (Get-Location).Path,
    [string]$EngineRoot,
    [string]$ProjectFile,
    [ValidateSet('Editor', 'Game')][string]$Target = 'Editor',
    [ValidateSet('Debug', 'Development', 'Shipping')][string]$Configuration = 'Debug',
    [ValidateSet('Win64')][string]$Platform = 'Win64',
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

function Write-Utf8NoBom([string]$Path, [string]$Contents) {
    $parent = Split-Path -Parent $Path
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Contents,
        [System.Text.UTF8Encoding]::new($false))
}

function Get-FullPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
}

function Get-RelativePath([string]$FromDirectory, [string]$ToPath) {
    $from = [System.IO.Path]::GetFullPath($FromDirectory).TrimEnd('\') + '\'
    $to = [System.IO.Path]::GetFullPath($ToPath).TrimEnd('\') + '\'
    return ([System.Uri]::new($from)).MakeRelativeUri([System.Uri]::new($to)).ToString().Replace('/', '\')
}

function Find-ProjectFile {
    if ($ProjectFile) { return Get-FullPath $ProjectFile }
    $matches = @(Get-ChildItem -LiteralPath (Get-Location).Path -Filter '*.sharpproject' -File)
    if ($matches.Count -ne 1) {
        throw 'Specify -ProjectFile, or run from a directory containing exactly one .sharpproject file.'
    }
    return $matches[0].FullName
}

function Resolve-ProjectManifest([string]$Path) {
    $full = Get-FullPath $Path
    if (!(Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Project manifest not found: $full"
    }
    $manifest = Get-Content -Raw -LiteralPath $full | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 2) {
        throw "Unsupported project manifest schema: $($manifest.schemaVersion)"
    }
    if (!$manifest.projectId -or !$manifest.engine.path -or !$manifest.modules) {
        throw "Project manifest is missing projectId, engine.path, or modules: $full"
    }
    $projectRoot = Split-Path -Parent $full
    $environmentName = [string]$manifest.engine.pathEnvironment
    $environmentPath = if ($environmentName) {
        [Environment]::GetEnvironmentVariable($environmentName)
    } else { $null }
    $engineFull = if ($environmentPath) {
        Get-FullPath $environmentPath
    } elseif ([System.IO.Path]::IsPathRooted([string]$manifest.engine.path)) {
        Get-FullPath ([string]$manifest.engine.path)
    } else {
        Get-FullPath (Join-Path $projectRoot ([string]$manifest.engine.path))
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
        ProjectRoot = $projectRoot
        ProjectId = [string]$manifest.projectId
        EngineRoot = $engineFull
        EngineManifest = $engineManifestPath
        EngineId = [string]$engineManifest.engineId
        EngineVersion = [string]$engineManifest.version
        Modules = @($manifest.modules | ForEach-Object {
            [System.IO.Path]::GetFullPath((Join-Path $projectRoot ([string]$_)))
        })
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
    $engineFull = if ($EngineRoot) { Get-FullPath $EngineRoot } else { $toolRoot }
    $engineManifestPath = Join-Path $engineFull 'Engine\engine.json'
    if (!(Test-Path -LiteralPath $engineManifestPath -PathType Leaf)) {
        throw "Engine distribution manifest not found: $engineManifestPath"
    }
    $engineManifest = Get-Content -Raw -LiteralPath $engineManifestPath | ConvertFrom-Json
    $engineRelative = Get-RelativePath $projectRoot $engineFull
    $projectId = $Name.ToLowerInvariant() -replace '[^a-z0-9]', ''
    if (!$projectId) { $projectId = 'game' }
    $namespace = $Name -replace '[^A-Za-z0-9_]', '_'
    if ($namespace -match '^[0-9]') { $namespace = "Game_$namespace" }

    $files = @(
        @{ Source = 'BasicProject.sharpproject.template'; Target = "$Name.sharpproject" },
        @{ Source = 'Directory.Build.props'; Target = 'Directory.Build.props' },
        @{ Source = 'Source\Runtime\Runtime.csproj.template'; Target = "Source\$namespace\$namespace.csproj" },
        @{ Source = 'Source\Runtime\RuntimeModule.cs'; Target = "Source\$namespace\${namespace}Module.cs" },
        @{ Source = 'Source\Editor\Editor.csproj.template'; Target = "Source\$namespace.Editor\$namespace.Editor.csproj" },
        @{ Source = 'Source\Editor\EditorModule.cs'; Target = "Source\$namespace.Editor\${namespace}EditorModule.cs" },
        @{ Source = 'Source\Editor\ProjectEditorExtension.cs'; Target = "Source\$namespace.Editor\ProjectEditorExtension.cs" },
        @{ Source = 'Content\UI\Main.ui.xml'; Target = 'Content\UI\Main.ui.xml' },
        @{ Source = 'Content\Assets\assets.json'; Target = 'Content\Assets\assets.json' },
        @{ Source = 'Content\Worlds\Main.world.xml'; Target = 'Content\Worlds\Main.world.xml' },
        @{ Source = 'README.md'; Target = 'README.md' }
    )
    foreach ($file in $files) {
        $source = Join-Path $templateRoot $file.Source
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "BasicProject template file not found: $source"
        }
        $text = Get-Content -Raw -LiteralPath $source
        $text = $text.Replace('__PROJECT_ID__', $projectId)
        $text = $text.Replace('__PROJECT_NAMESPACE__', $namespace)
        $text = $text.Replace('__DISPLAY_NAME__', $Name)
        $text = $text.Replace('__ENGINE_PATH__', $engineRelative.Replace('\', '\\'))
        Write-Utf8NoBom (Join-Path $projectRoot $file.Target) $text
    }
    $result = [pscustomobject]@{
        ProjectRoot = $projectRoot
        ProjectManifest = Join-Path $projectRoot "$Name.sharpproject"
        EngineRoot = $engineFull
        EngineVersion = [string]$engineManifest.version
    }
    if ($Json) { $result | ConvertTo-Json -Depth 5 } else {
        Write-Output "Created SharpEngine project: $projectRoot"
        Write-Output "Next: & '$toolRoot\Tools\SharpEngineProject.ps1' -Command Build -ProjectFile '$($result.ProjectManifest)'"
    }
}

function Invoke-Resolve([string]$Path) {
    $resolved = Resolve-ProjectManifest $Path
    if ($Json) { $resolved | ConvertTo-Json -Depth 5 } else {
        Write-Output "Project: $($resolved.ProjectRoot)"
        Write-Output "Engine:  $($resolved.EngineRoot)"
        Write-Output "Version: $($resolved.EngineVersion)"
        Write-Output "Modules: $($resolved.Modules.Count)"
    }
}

function Invoke-Doctor([string]$Path) {
    $resolved = Resolve-ProjectManifest $Path
    $missing = @($resolved.Modules | Where-Object {
        !(Test-Path -LiteralPath $_ -PathType Leaf)
    })
    if ($missing.Count -gt 0) {
        throw "Project Module files are missing: $($missing -join ', ')"
    }
    if ($Json) {
        [pscustomobject]@{
            Healthy = $true
            ProjectRoot = $resolved.ProjectRoot
            EngineRoot = $resolved.EngineRoot
            Modules = $resolved.Modules
        } | ConvertTo-Json -Depth 5
    } else {
        Write-Output "Healthy: $($resolved.ProjectId)"
        Write-Output "Engine root: $($resolved.EngineRoot)"
        Write-Output "Modules: $($resolved.Modules.Count)"
    }
}

function Invoke-Build([string]$Path) {
    $resolved = Resolve-ProjectManifest $Path
    $builder = Join-Path $resolved.EngineRoot 'Tools\ProjectBuild\SharpEngine.ProjectBuild.csproj'
    if (!(Test-Path -LiteralPath $builder -PathType Leaf)) {
        throw "SharpEngine project builder not found: $builder"
    }
    & dotnet run --project $builder -- build $resolved.ProjectManifest `
        --target $Target --configuration $Configuration --platform $Platform
    if ($LASTEXITCODE -ne 0) { throw "SharpEngine project build failed with exit code $LASTEXITCODE" }
}

switch ($Command) {
    'New' { Invoke-NewProject }
    'Resolve' { Invoke-Resolve (Find-ProjectFile) }
    'Doctor' { Invoke-Doctor (Find-ProjectFile) }
    'Build' { Invoke-Build (Find-ProjectFile) }
}
