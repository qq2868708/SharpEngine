[CmdletBinding()]
param(
    [ValidateSet('New', 'Resolve', 'Doctor', 'Build', 'Clean', 'Rebuild',
        'GenerateProjectFiles', 'OpenSolution', 'OpenEditor', 'RunGame', 'Migrate')]
    [string]$Command = 'New',
    [string]$Name,
    [string]$Directory = (Get-Location).Path,
    [string]$EngineRoot,
    [string]$EngineAssociation,
    [string]$ProjectFile,
    [ValidateSet('Editor', 'Game')][string]$Target = 'Editor',
    [ValidateSet('Debug', 'Development', 'Shipping')][string]$Configuration = 'Debug',
    [ValidateSet('Win64')][string]$Platform = 'Win64',
    [ValidateSet('opengl', 'vulkan')][string]$RenderBackend,
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

function Get-RegisteredEngineRoot([string]$Association) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
        'Software\SharpEngine\Builds', $false)
    if ($null -eq $key) { return $null }
    try { return [string]$key.GetValue($Association, $null) }
    finally { $key.Dispose() }
}

function Find-EngineAssociation([string]$Root) {
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
        'Software\SharpEngine\Builds', $false)
    if ($null -eq $key) { return $null }
    try {
        foreach ($name in $key.GetValueNames()) {
            $candidate = [string]$key.GetValue($name, '')
            if (!$candidate) { continue }
            if ([System.IO.Path]::GetFullPath($candidate).TrimEnd('\', '/').Equals(
                    $rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $name
            }
        }
        return $null
    } finally { $key.Dispose() }
}

function Assert-EngineAssociation([string]$Association) {
    $associationGuid = [guid]::Empty
    if ($Association.Length -gt 128 -or
        (!([guid]::TryParse($Association, [ref]$associationGuid)) -and
         $Association -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$')) {
        throw "Invalid Engine association '$Association'."
    }
}

function Resolve-ProjectManifest([string]$Path) {
    $full = Get-FullPath $Path
    if (!(Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Project manifest not found: $full"
    }
    $manifest = Get-Content -Raw -LiteralPath $full | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 3) {
        throw "Unsupported project manifest schema: $($manifest.schemaVersion)"
    }
    if (!$manifest.projectId -or !$manifest.engine.association -or !$manifest.modules) {
        throw "Project manifest is missing projectId, engine.association, or modules: $full"
    }
    Assert-EngineAssociation ([string]$manifest.engine.association)
    $projectRoot = Split-Path -Parent $full
    $environmentPath = [Environment]::GetEnvironmentVariable('SHARPENGINE_ROOT')
    $registeredPath = Get-RegisteredEngineRoot ([string]$manifest.engine.association)
    $engineFull = if ($EngineRoot) { Get-FullPath $EngineRoot }
        elseif ($environmentPath) { Get-FullPath $environmentPath }
        elseif ($registeredPath) { Get-FullPath $registeredPath }
        else { throw "Engine association '$($manifest.engine.association)' is not registered." }
    $engineManifestPath = Join-Path $engineFull ([string]$manifest.engine.manifest)
    if (!(Test-Path -LiteralPath $engineManifestPath -PathType Leaf)) {
        throw "Engine manifest not found: $engineManifestPath"
    }
    $engineManifest = Get-Content -Raw -LiteralPath $engineManifestPath | ConvertFrom-Json
    if ($engineManifest.schemaVersion -ne 2) {
        throw "Unsupported engine manifest schema: $($engineManifest.schemaVersion)"
    }
    if ($engineManifest.engineId -ne [string]$manifest.engine.id) {
        throw "Engine id mismatch. Project requests '$($manifest.engine.id)', resolved '$($engineManifest.engineId)'."
    }
    if ($engineManifest.version -ne [string]$manifest.engine.version) {
        throw "Engine version mismatch. Project requests '$($manifest.engine.version)', resolved '$($engineManifest.version)'."
    }
    [pscustomobject]@{
        ProjectManifest = $full
        ProjectRoot = $projectRoot
        ProjectId = [string]$manifest.projectId
        EngineRoot = $engineFull
        EngineManifest = $engineManifestPath
        EngineId = [string]$engineManifest.engineId
        EngineVersion = [string]$engineManifest.version
        EngineAssociation = [string]$manifest.engine.association
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
    $engineManifestPath = Join-Path $engineFull 'engine.json'
    if (!(Test-Path -LiteralPath $engineManifestPath -PathType Leaf)) {
        throw "Engine distribution manifest not found: $engineManifestPath"
    }
    $engineManifest = Get-Content -Raw -LiteralPath $engineManifestPath | ConvertFrom-Json
    if ($engineManifest.schemaVersion -ne 2) {
        throw "Unsupported engine manifest schema: $($engineManifest.schemaVersion)"
    }
    $association = if ($EngineAssociation) { $EngineAssociation }
        else { Find-EngineAssociation $engineFull }
    if ([Environment]::GetEnvironmentVariable('SHARPENGINE_REGISTER_ON_FIRST_USE') -ne '0')
    {
        $registrationOutput = [System.IO.Path]::GetTempFileName()
        try
        {
            & (Join-Path $PSScriptRoot 'Invoke-ProjectSelector.ps1') -EngineRoot $engineFull `
                -Configuration $Configuration -Platform $Platform -SelectorArguments @(
                    'ensure-registered', '--engine-root', $engineFull, '--no-ui',
                    '--output', $registrationOutput) | Out-Host
            $registration = Get-Content -Raw -LiteralPath $registrationOutput | ConvertFrom-Json
            if (!$association)
            {
                $association = [string]$registration.Association
            }
        }
        finally
        {
            Remove-Item -LiteralPath $registrationOutput -Force -ErrorAction SilentlyContinue
        }
    }
    if (!$association)
    {
        throw "Automatic registration is disabled and Engine root '$engineFull' is not registered. Specify -EngineAssociation, or enable first-use registration."
    }
    Assert-EngineAssociation $association
    $projectId = $Name.ToLowerInvariant() -replace '[^a-z0-9]', ''
    if (!$projectId) { $projectId = 'game' }
    $namespace = $Name -replace '[^A-Za-z0-9_]', '_'
    if ($namespace -match '^[0-9]') { $namespace = "Game_$namespace" }

    $files = @(
        @{ Source = 'BasicProject.sharpproject.template'; Target = "$Name.sharpproject" },
        @{ Source = 'AGENTS.md'; Target = 'AGENTS.md' },
        @{ Source = '.editorconfig'; Target = '.editorconfig' },
        @{ Source = 'Directory.Build.props'; Target = 'Directory.Build.props' },
        @{ Source = 'Source\Runtime\Runtime.csproj.template'; Target = "Source\$namespace\$namespace.csproj" },
        @{ Source = 'Source\Runtime\RuntimeModule.cs'; Target = "Source\$namespace\${namespace}Module.cs" },
        @{ Source = 'Source\Runtime\GameDevelopmentSettings.cs'; Target = "Source\$namespace\GameDevelopmentSettings.cs" },
        @{ Source = 'Source\Editor\Editor.csproj.template'; Target = "Source\$namespace.Editor\$namespace.Editor.csproj" },
        @{ Source = 'Source\Editor\EditorModule.cs'; Target = "Source\$namespace.Editor\${namespace}EditorModule.cs" },
        @{ Source = 'Source\Editor\ProjectEditorExtension.cs'; Target = "Source\$namespace.Editor\ProjectEditorExtension.cs" },
        @{ Source = 'Content\UI\Main.rml'; Target = 'Content\UI\Main.rml' },
        @{ Source = 'Content\UI\Main.rcss'; Target = 'Content\UI\Main.rcss' },
        @{ Source = 'Content\Assets\assets.json'; Target = 'Content\Assets\assets.json' },
        @{ Source = 'Content\Input\Actions\Move.inputaction.json'; Target = 'Content\Input\Actions\Move.inputaction.json' },
        @{ Source = 'Content\Input\Actions\Primary.inputaction.json'; Target = 'Content\Input\Actions\Primary.inputaction.json' },
        @{ Source = 'Content\Input\Contexts\Gameplay.inputmapping.json'; Target = 'Content\Input\Contexts\Gameplay.inputmapping.json' },
        @{ Source = 'README.md'; Target = 'README.md' }
        @{ Source = '.gitignore'; Target = '.gitignore' }
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
        $text = $text.Replace('__ENGINE_ASSOCIATION__', $association)
        Write-Utf8NoBom (Join-Path $projectRoot $file.Target) $text
    }
    $manifestPath = Join-Path $projectRoot "$Name.sharpproject"
    Invoke-Selector 'initialize-world' $manifestPath $true $engineFull
    Invoke-GenerateProjectFiles $manifestPath $engineFull
    $result = [pscustomobject]@{
        ProjectRoot = $projectRoot
        ProjectManifest = $manifestPath
        EngineRoot = $engineFull
        EngineVersion = [string]$engineManifest.version
        EngineAssociation = $association
        Solution = Join-Path $projectRoot "$Name.sln"
    }
    if ($Json) { $result | ConvertTo-Json -Depth 5 } else {
        Write-Output "Created SharpEngine project: $projectRoot"
        Write-Output "Solution: $($result.Solution)"
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
    & (Join-Path $resolved.EngineRoot 'Tools/SharpEngineBuild.ps1') -EngineRoot $resolved.EngineRoot -Target $Target -Configuration $Configuration -Platform $Platform
    if ($LASTEXITCODE) { throw 'Engine Target build failed' }
    $layout = Join-Path $resolved.EngineRoot 'Tools\SharpEngineLayout.ps1'
    if (!(Test-Path -LiteralPath $layout -PathType Leaf)) {
        throw "SharpEngine layout checker not found: $layout"
    }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $layout `
        -EngineRoot $resolved.EngineRoot -Mode Check -Platform $Platform `
        -Configuration $Configuration -Target Editor
    if ($LASTEXITCODE -ne 0) { throw "SharpEngine layout check failed with exit code $LASTEXITCODE" }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $layout `
        -ProjectFile $resolved.ProjectManifest -Mode Check -Platform $Platform `
        -Configuration $Configuration -Target $Target
    if ($LASTEXITCODE -ne 0) { throw "Project layout check failed with exit code $LASTEXITCODE" }
    $builder = Join-Path $resolved.EngineRoot 'Tools\ProjectBuild\SharpEngine.ProjectBuild.csproj'
    if (!(Test-Path -LiteralPath $builder -PathType Leaf)) {
        throw "SharpEngine project builder not found: $builder"
    }
    & dotnet run --project $builder -- build $resolved.ProjectManifest `
        --target $Target --configuration $Configuration --platform $Platform `
        --engine-root $resolved.EngineRoot
    if ($LASTEXITCODE -ne 0) { throw "SharpEngine project build failed with exit code $LASTEXITCODE" }
}

function Invoke-Clean([string]$Path) {
    $resolved = Resolve-ProjectManifest $Path
    $layout = Join-Path $resolved.EngineRoot 'Tools\SharpEngineLayout.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $layout `
        -ProjectFile $resolved.ProjectManifest -Mode Clean -Platform $Platform `
        -Configuration $Configuration -Target $Target
    if ($LASTEXITCODE -ne 0) {
        throw "SharpEngine project clean failed with exit code $LASTEXITCODE"
    }
}

function Invoke-Rebuild([string]$Path) {
    Invoke-Clean $Path
    Invoke-Build $Path
}

function Invoke-Selector([string]$SelectorCommand, [string]$Path, [bool]$NoUi,
    [string]$EngineRootOverride = '') {
    $selectorArguments = @($SelectorCommand, '--project', (Get-FullPath $Path),
        '--configuration', $Configuration)
    $explicitRoot = if ($EngineRootOverride) { Get-FullPath $EngineRootOverride }
        elseif ($EngineRoot) { Get-FullPath $EngineRoot }
        else { [Environment]::GetEnvironmentVariable('SHARPENGINE_ROOT') }
    if ($explicitRoot) { $selectorArguments += @('--engine-root', $explicitRoot) }
    if ($RenderBackend -and $SelectorCommand -in @('open-editor', 'run-game')) {
        $selectorArguments += @('--render-backend', $RenderBackend)
    }
    if ($NoUi) { $selectorArguments += '--no-ui' }
    $selectorRoot = if ($explicitRoot)
    {
        $explicitRoot
    }
    else
    {
        Split-Path -Parent $PSScriptRoot
    }
    & (Join-Path $PSScriptRoot 'Invoke-ProjectSelector.ps1') -EngineRoot $selectorRoot `
        -Configuration $Configuration -Platform $Platform -SelectorArguments $selectorArguments
}

function Invoke-GenerateProjectFiles([string]$Path, [string]$EngineRootOverride = '') {
    Invoke-Selector 'GenerateProjectFiles' $Path $true $EngineRootOverride
}

function Invoke-OpenSolution([string]$Path) {
    Invoke-Selector 'open-solution' $Path $false
}

function Invoke-OpenEditor([string]$Path) {
    Invoke-Selector 'open-editor' $Path $false
}

switch ($Command) {
    'New' { Invoke-NewProject }
    'Resolve' { Invoke-Resolve (Find-ProjectFile) }
    'Doctor' { Invoke-Doctor (Find-ProjectFile) }
    'Build' { Invoke-Build (Find-ProjectFile) }
    'Clean' { Invoke-Clean (Find-ProjectFile) }
    'Rebuild' { Invoke-Rebuild (Find-ProjectFile) }
    'GenerateProjectFiles' { Invoke-GenerateProjectFiles (Find-ProjectFile) }
    'OpenSolution' { Invoke-OpenSolution (Find-ProjectFile) }
    'OpenEditor' { Invoke-OpenEditor (Find-ProjectFile) }
    'RunGame' { Invoke-Selector 'run-game' (Find-ProjectFile) $true }
    'Migrate' { Invoke-Selector 'migrate' (Find-ProjectFile) $true }
}
