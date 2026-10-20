[CmdletBinding(DefaultParameterSetName = 'Engine')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Engine')]
    [string]$EngineRoot,

    [Parameter(Mandatory = $true, ParameterSetName = 'Project')]
    [string]$ProjectFile,

    [ValidateSet('Check', 'Apply', 'Clean')]
    [string]$Mode = 'Check',

    [ValidateSet('All', 'Target')]
    [string]$Scope = 'All',

    [ValidateSet('Win64')]
    [string]$Platform = 'Win64',

    [ValidateSet('Debug', 'Development', 'Shipping')]
    [string]$Configuration = 'Debug',

    [ValidateSet('Editor', 'Game', 'Server', 'Tools')]
    [string]$Target = 'Editor'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$SupportedPlatforms = @('Win64')
$SupportedConfigurations = @('Debug', 'Development', 'Shipping')
$EngineTargets = @('Editor', 'Game', 'Server', 'Tools')
$ProjectTargets = @('Editor', 'Game', 'Server')
$BinaryTemplate = 'Binaries/{platform}/{configuration}/{target}'
$IntermediateTemplate = 'Intermediate/{platform}/{configuration}/{target}'
$PropsBinariesDir = '$(SharpEngineRoot)Binaries\$(SharpEnginePlatform)\$(Configuration)\$(SharpEngineOutputTarget)\'
$PropsIntermediateDir = '$(SharpEngineRoot)Intermediate\$(SharpEnginePlatform)\$(Configuration)\$(SharpEngineOutputTarget)\'

function Get-AbsolutePath([string]$Path, [string]$BaseDirectory = (Get-Location).Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $BaseDirectory $Path))
}

function Assert-ExistingDirectory([string]$Path, [string]$Label) {
    if (!(Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label directory not found: $Path"
    }
}

function Test-PathInsideRoot([string]$Root, [string]$Candidate) {
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    if ($candidateFull.Equals($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    return $candidateFull.StartsWith(
        $rootFull + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePoint([string]$Root, [string]$Candidate) {
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    if (!(Test-PathInsideRoot $rootFull $candidateFull)) {
        throw "Unsafe path outside root '$rootFull': $candidateFull"
    }

    $rootItem = Get-Item -LiteralPath $rootFull -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Root cannot be a reparse point: $rootFull"
    }

    $relative = $candidateFull.Substring($rootFull.Length).TrimStart('\', '/')
    $cursor = $rootFull
    foreach ($segment in $relative.Split(@('\', '/'), [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $segment
        if (!(Test-Path -LiteralPath $cursor)) { break }
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Unsafe reparse point in mutation path: $cursor"
        }
    }
}

function Assert-SafeMutationTarget([string]$Root, [string]$Candidate) {
    $full = [System.IO.Path]::GetFullPath($Candidate)
    if (!(Test-PathInsideRoot $Root $full)) {
        throw "Mutation target must be strictly inside '$Root': $full"
    }
    Assert-NoReparsePoint $Root $full
    return $full
}

function Remove-ExactDirectory([string]$Root, [string]$RelativePath) {
    $candidate = Assert-SafeMutationTarget $Root (Join-Path $Root $RelativePath)
    if (!(Test-Path -LiteralPath $candidate)) { return }
    if (!(Test-Path -LiteralPath $candidate -PathType Container)) {
        throw "Expected a directory at the exact cleanup path: $candidate"
    }
    $nestedReparsePoint = Get-ChildItem -LiteralPath $candidate -Force -Recurse |
        Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1
    if ($null -ne $nestedReparsePoint) {
        throw "Refusing to recursively delete a tree containing a reparse point: $($nestedReparsePoint.FullName)"
    }
    Remove-Item -LiteralPath $candidate -Recurse -Force
    Write-Output "Removed: $candidate"
}

function Remove-ExactFile([string]$Root, [string]$RelativePath) {
    $candidate = Assert-SafeMutationTarget $Root (Join-Path $Root $RelativePath)
    if (!(Test-Path -LiteralPath $candidate)) { return }
    if (!(Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Expected a file at the exact cleanup path: $candidate"
    }
    Remove-Item -LiteralPath $candidate -Force
    Write-Output "Removed: $candidate"
}

function New-SafeDirectory([string]$Root, [string]$RelativePath) {
    $candidate = Assert-SafeMutationTarget $Root (Join-Path $Root $RelativePath)
    if (Test-Path -LiteralPath $candidate) {
        if (!(Test-Path -LiteralPath $candidate -PathType Container)) {
            throw "Expected a directory at the output path: $candidate"
        }
        return
    }
    New-Item -ItemType Directory -Path $candidate -Force | Out-Null
    Write-Output "Created: $candidate"
}

function Remove-EmptyDirectory([string]$Root, [string]$RelativePath) {
    $candidate = Assert-SafeMutationTarget $Root (Join-Path $Root $RelativePath)
    if (!(Test-Path -LiteralPath $candidate)) { return }
    if (!(Test-Path -LiteralPath $candidate -PathType Container)) {
        throw "Expected a directory at the exact cleanup path: $candidate"
    }
    if (@(Get-ChildItem -LiteralPath $candidate -Force).Count -ne 0) { return }
    Remove-Item -LiteralPath $candidate -Force
    Write-Output "Removed empty: $candidate"
}

function Read-JsonFile([string]$Path, [string]$Label) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label not found: $Path"
    }
    try {
        return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    } catch {
        throw "$Label is not valid JSON: $Path`n$($_.Exception.Message)"
    }
}

function Get-JsonProperty($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-MsBuildProperty([object[]]$PropertyGroups, [string]$Name) {
    foreach ($group in $PropertyGroups) {
        foreach ($node in @($group.ChildNodes)) {
            if ($node.Name -eq $Name) { return [string]$node.InnerText }
        }
    }
    return ''
}

function Assert-NoLegacyDirectories([string]$Root, [string[]]$RelativePaths) {
    foreach ($relativePath in $RelativePaths) {
        $candidate = [System.IO.Path]::GetFullPath((Join-Path $Root $relativePath))
        if (!(Test-PathInsideRoot $Root $candidate)) {
            throw "Configured legacy path escaped root: $relativePath"
        }
        if (Test-Path -LiteralPath $candidate) {
            throw "Legacy output directory is not allowed: $candidate"
        }
    }
}

function Assert-OutputHierarchy([string]$Root, [string]$DirectoryName,
    [string[]]$AllowedTargets, [bool]$AllowIntermediateRoots = $false) {
    $outputRoot = Join-Path $Root $DirectoryName
    if (!(Test-Path -LiteralPath $outputRoot)) { return }
    if (!(Test-Path -LiteralPath $outputRoot -PathType Container)) {
        throw "Output root must be a directory: $outputRoot"
    }
    Assert-NoReparsePoint $Root $outputRoot

    foreach ($platformDirectory in @(Get-ChildItem -LiteralPath $outputRoot -Force)) {
        if ($AllowIntermediateRoots -and $DirectoryName -eq 'Intermediate' -and
            $platformDirectory.PSIsContainer -and
            $platformDirectory.Name -in @('ProjectFiles', 'RenderCache')) {
            Assert-NoReparsePoint $Root $platformDirectory.FullName
            continue
        }
        if (!$platformDirectory.PSIsContainer -or $platformDirectory.Name -notin $SupportedPlatforms) {
            throw "Unknown $DirectoryName platform entry: $($platformDirectory.FullName)"
        }
        Assert-NoReparsePoint $Root $platformDirectory.FullName
        foreach ($configurationDirectory in @(Get-ChildItem -LiteralPath $platformDirectory.FullName -Force)) {
            if (!$configurationDirectory.PSIsContainer -or
                $configurationDirectory.Name -notin $SupportedConfigurations) {
                throw "Unknown $DirectoryName configuration entry: $($configurationDirectory.FullName)"
            }
            Assert-NoReparsePoint $Root $configurationDirectory.FullName
            foreach ($targetDirectory in @(Get-ChildItem -LiteralPath $configurationDirectory.FullName -Force)) {
                if (!$targetDirectory.PSIsContainer -or $targetDirectory.Name -notin $AllowedTargets) {
                    throw "Unknown $DirectoryName target entry: $($targetDirectory.FullName)"
                }
                Assert-NoReparsePoint $Root $targetDirectory.FullName
            }
        }
    }
}

function Assert-EngineContract([string]$Root) {
    $manifestPath = Join-Path $Root 'engine.json'
    $manifest = Read-JsonFile $manifestPath 'Engine manifest'
    if ($manifest.schemaVersion -ne 2) {
        throw "Engine manifest schemaVersion must be 2: $manifestPath"
    }
    if ([string]$manifest.engineId -ne 'SharpEngine') {
        throw "Engine manifest engineId must be 'SharpEngine': $manifestPath"
    }
    if ($manifest.projectContract.schemaVersion -ne 3) {
        throw "Engine projectContract.schemaVersion must be 3: $manifestPath"
    }

    $propsPath = Join-Path $Root 'Build\SharpEngine.Build.props'
    if (!(Test-Path -LiteralPath $propsPath -PathType Leaf)) {
        throw "Build path authority not found: $propsPath"
    }
    try { [xml]$props = Get-Content -Raw -LiteralPath $propsPath } catch {
        throw "Build props is not valid XML: $propsPath`n$($_.Exception.Message)"
    }
    $propertyGroups = @($props.Project.PropertyGroup)
    $rootExpression = Get-MsBuildProperty $propertyGroups 'SharpEngineRoot'
    $platformDefault = Get-MsBuildProperty $propertyGroups 'SharpEnginePlatform'
    $targetDefault = Get-MsBuildProperty $propertyGroups 'SharpEngineTarget'
    $binaryRoot = Get-MsBuildProperty $propertyGroups 'SharpEngineBinariesDir'
    $intermediateRoot = Get-MsBuildProperty $propertyGroups 'SharpEngineIntermediateDir'
    $outputPath = Get-MsBuildProperty $propertyGroups 'OutputPath'
    $baseIntermediateOutputPath = Get-MsBuildProperty $propertyGroups 'BaseIntermediateOutputPath'
    $expectedBinariesDir = Get-MsBuildProperty $propertyGroups 'SharpEngineExpectedBinariesDir'
    $expectedIntermediateDir = Get-MsBuildProperty $propertyGroups 'SharpEngineExpectedIntermediateDir'
    $expectedProjectIntermediateDir = Get-MsBuildProperty $propertyGroups 'SharpEngineExpectedProjectIntermediateDir'
    $binaryTemplate = Get-MsBuildProperty $propertyGroups 'SharpEngineBinariesTemplate'
    $intermediateTemplate = Get-MsBuildProperty $propertyGroups 'SharpEngineIntermediateTemplate'
    if ($platformDefault -ne 'Win64' -or $targetDefault -ne 'Editor') {
        throw 'Build props defaults must be SharpEnginePlatform=Win64 and SharpEngineTarget=Editor.'
    }
    if ($rootExpression -notlike '*EnsureTrailingSlash*' -or
        $rootExpression -notlike '*GetFullPath*') {
        throw 'Build props SharpEngineRoot must resolve the parent of Build to an absolute path with a trailing separator.'
    }
    if ($binaryRoot -ne $PropsBinariesDir -or $intermediateRoot -ne $PropsIntermediateDir) {
        throw 'Build props output path templates do not match the SharpEngine layout contract.'
    }
    if ($outputPath -ne '$(SharpEngineBinariesDir)' -or
        $baseIntermediateOutputPath -ne '$(SharpEngineIntermediateDir)$(MSBuildProjectName)\') {
        throw 'Build props must keep final artifacts flat and isolate intermediates by Build Module.'
    }
    if ($expectedBinariesDir -notlike '*GetFullPath*' -or
        $expectedIntermediateDir -notlike '*GetFullPath*' -or
        $expectedProjectIntermediateDir -ne '$(SharpEngineExpectedIntermediateDir)$(MSBuildProjectName)\') {
        throw 'Build props must reject noncanonical output paths before build generation.'
    }
    $guard = $props.Project.Target | Where-Object {
        $_.Name -eq 'ValidateSharpEngineOutputContract'
    } | Select-Object -First 1
    if ($null -eq $guard -or
        [string]$guard.BeforeTargets -notlike '*Restore*' -or
        [string]$guard.BeforeTargets -notlike '*ResolvePackageAssets*' -or
        [string]$guard.BeforeTargets -notlike '*PrepareForBuild*') {
        throw 'Build props output guard must run before restore, dependency resolution, and build preparation.'
    }
    if ($binaryTemplate.Replace('\', '/') -ne $BinaryTemplate -or
        $intermediateTemplate.Replace('\', '/') -ne $IntermediateTemplate) {
        throw 'Build props manifest templates do not match the SharpEngine layout contract.'
    }

    $paths = Get-JsonProperty $manifest 'paths'
    $manifestBinary = if ($null -ne $paths) { [string](Get-JsonProperty $paths 'binaries') } else { '' }
    $manifestIntermediate = if ($null -ne $paths) { [string](Get-JsonProperty $paths 'intermediate') } else { '' }
    if ($manifestBinary.Replace('\', '/') -ne $binaryTemplate.Replace('\', '/') -or
        $manifestIntermediate.Replace('\', '/') -ne $intermediateTemplate.Replace('\', '/')) {
        throw 'Engine manifest binaries/intermediate templates must match Build props.'
    }

    # Project discovery belongs to EngineBuildGraph; do not keep a second list here.
    foreach ($relativePath in @('Config\DefaultEditorLayout.json')) {
        $requiredPath = Join-Path $Root $relativePath
        if (!(Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required engine file not found: $requiredPath"
        }
    }
}

function Assert-EngineIdentity([string]$Root) {
    $manifestPath = Join-Path $Root 'engine.json'
    $manifest = Read-JsonFile $manifestPath 'Engine manifest'
    if ([string]$manifest.engineId -ne 'SharpEngine') {
        throw "Refusing to mutate a directory that is not a SharpEngine root: $Root"
    }
}

function Resolve-ProjectContract([string]$ManifestPath) {
    $manifestFull = Get-AbsolutePath $ManifestPath
    $manifest = Read-JsonFile $manifestFull 'Project manifest'
    if ($manifest.schemaVersion -ne 3) {
        throw "Project manifest schemaVersion must be 3: $manifestFull"
    }
    if (!$manifest.engine.association) {
        throw "Project manifest must declare engine.association: $manifestFull"
    }
    $root = Split-Path -Parent $manifestFull
    Assert-ExistingDirectory $root 'Project root'
    $sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'Source'))
    $modules = @($manifest.modules)
    if ($modules.Count -eq 0) { throw "Project manifest must declare at least one module: $manifestFull" }
    foreach ($module in $modules) {
        $moduleText = [string]$module
        if ([System.IO.Path]::IsPathRooted($moduleText)) {
            throw "Project module paths must be relative and under Source: $moduleText"
        }
        $moduleFull = [System.IO.Path]::GetFullPath((Join-Path $root $moduleText))
        if (!$moduleFull.StartsWith($sourceRoot + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Project module is outside Source: $moduleText"
        }
        if (!(Test-Path -LiteralPath $moduleFull -PathType Leaf)) {
            throw "Project module not found: $moduleFull"
        }
    }
    return [pscustomobject]@{ Root = $root; Manifest = $manifest; ManifestPath = $manifestFull }
}

function Assert-TargetOutputHierarchy([string]$Root, [string]$DirectoryName)
{
    $CurrentPath = $Root
    foreach ($Segment in @($DirectoryName, $Platform, $Configuration, $Target))
    {
        $CurrentPath = Join-Path $CurrentPath $Segment
        if (!(Test-Path -LiteralPath $CurrentPath))
        {
            return
        }
        if (!(Test-Path -LiteralPath $CurrentPath -PathType Container))
        {
            throw "Output path must be a directory: $CurrentPath"
        }
        Assert-NoReparsePoint $Root $CurrentPath
    }
}

function Invoke-Check([string]$Root, [bool]$IsEngine)
{
    if ($IsEngine)
    {
        Assert-EngineContract $Root
    }
    if ($Scope -eq 'Target')
    {
        Assert-TargetOutputHierarchy $Root 'Binaries'
        Assert-TargetOutputHierarchy $Root 'Intermediate'
        Write-Output "Target layout check passed: $Root ($Platform/$Configuration/$Target)"
        return
    }

    if ($IsEngine)
    {
        Assert-NoLegacyDirectories $Root @('x64', 'Engine\x64', 'Build\Verify',
            'Build\Validation', 'Build\McpValidation',
            'Build\EditorValidationIsolated', 'Build\McpNative',
            'External\Build', 'External\BuildVS18', 'Engine\Engine\x64',
            'Engine\Engine', 'Engine\.vs', 'Engine\Engine.vcxproj.user',
            'Engine', 'Managed', 'Templates\BasicProject\Managed',
            'Templates\BasicProject\Project',
            'Templates\BasicProject\Content\Worlds', 'Project')
        $allowedTargets = $EngineTargets
    }
    else
    {
        Assert-NoLegacyDirectories $Root @('x64', 'Binaries\Managed',
            'Intermediate\Managed', 'Intermediate\ProjectFiles\x64')
        $allowedTargets = $ProjectTargets
    }
    Assert-OutputHierarchy $Root 'Binaries' $allowedTargets
    Assert-OutputHierarchy $Root 'Intermediate' $allowedTargets $true
    Write-Output "Layout check passed: $Root"
}

function Invoke-Apply([string]$Root, [bool]$IsEngine) {
    if ($IsEngine) {
        foreach ($legacy in @('x64', 'Engine\x64', 'Build\Verify', 'Build\Validation',
                'Build\McpValidation', 'Build\EditorValidationIsolated',
                'Build\McpNative', 'External\Build', 'External\BuildVS18',
                'Engine\Engine\x64', 'Engine\.vs', 'Binaries\Win64\Editor',
                'Intermediate\Win64\Editor', 'Managed\obj',
                'Managed\Public\obj', 'Managed\Modules\obj')) {
            Remove-ExactDirectory $Root $legacy
        }
        Remove-ExactFile $Root 'Engine\Engine.vcxproj.user'
        foreach ($legacyParent in @('Managed\Public', 'Managed\Modules', 'Managed')) {
            Remove-EmptyDirectory $Root $legacyParent
        }
        Remove-EmptyDirectory $Root 'Engine\Engine'
        Remove-EmptyDirectory $Root 'Engine'
        Remove-EmptyDirectory $Root 'Templates\BasicProject\Managed'
        Remove-EmptyDirectory $Root 'Templates\BasicProject\Project'
        Remove-EmptyDirectory $Root 'Templates\BasicProject\Content\Worlds'
        Remove-EmptyDirectory $Root 'Project'
    } else {
        foreach ($legacy in @('x64', 'Binaries\Managed', 'Intermediate\Managed',
                'Intermediate\ProjectFiles\x64')) {
            Remove-ExactDirectory $Root $legacy
        }
    }
    $combination = Join-Path (Join-Path (Join-Path $Platform $Configuration) $Target) ''
    New-SafeDirectory $Root (Join-Path 'Binaries' $combination)
    New-SafeDirectory $Root (Join-Path 'Intermediate' $combination)
}

function Invoke-Clean([string]$Root) {
    $combination = Join-Path (Join-Path $Platform $Configuration) $Target
    Remove-ExactDirectory $Root (Join-Path 'Binaries' $combination)
    Remove-ExactDirectory $Root (Join-Path 'Intermediate' $combination)
    foreach ($outputRoot in @('Binaries', 'Intermediate')) {
        Remove-EmptyDirectory $Root (Join-Path $outputRoot (Join-Path $Platform $Configuration))
        Remove-EmptyDirectory $Root (Join-Path $outputRoot $Platform)
        Remove-EmptyDirectory $Root $outputRoot
    }
}

$isEngine = $PSCmdlet.ParameterSetName -eq 'Engine'
if ($isEngine) {
    $root = Get-AbsolutePath $EngineRoot
    Assert-ExistingDirectory $root 'Engine root'
    if ($Target -notin $EngineTargets) {
        throw "The engine supports only these Targets: $($EngineTargets -join ', ')."
    }
    if ($Mode -ne 'Check') { Assert-EngineIdentity $root }
} else {
    $project = Resolve-ProjectContract $ProjectFile
    $root = $project.Root
    if ($Target -notin $ProjectTargets) {
        throw "Projects support only these Targets: $($ProjectTargets -join ', ')."
    }
}

switch ($Mode) {
    'Check' { Invoke-Check $root $isEngine }
    'Apply' { Invoke-Apply $root $isEngine; Invoke-Check $root $isEngine }
    'Clean' { Invoke-Clean $root; Invoke-Check $root $isEngine }
}
