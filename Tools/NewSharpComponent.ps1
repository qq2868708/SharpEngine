[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ModuleDirectory,
    [Parameter(Mandatory=$true)][string]$Name,
    [string]$Directory = '',
    [string]$Namespace = '',
    [ValidateSet('Debug', 'Development', 'Shipping')][string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$componentDirectory = (Resolve-Path -LiteralPath $ModuleDirectory).Path
$componentProjects = @(Get-ChildItem -LiteralPath $componentDirectory -Filter '*.csproj' -File)
if ($componentProjects.Count -ne 1) { throw 'Choose a Module directory containing exactly one .csproj.' }
$componentRoot = [System.IO.DirectoryInfo]::new($componentDirectory)
$componentManifest = $null
while ($componentRoot -and !$componentManifest) {
    $manifests = @(Get-ChildItem -LiteralPath $componentRoot.FullName -Filter '*.sharpproject' -File)
    if ($manifests.Count -gt 1) { throw 'Project manifest is ambiguous.' }
    if ($manifests.Count -eq 1) { $componentManifest = $manifests[0]; break }
    $componentRoot = $componentRoot.Parent
}
if (!$componentManifest) { throw 'No owning .sharpproject was found.' }
$componentRelative = $componentProjects[0].FullName.Substring($componentRoot.FullName.TrimEnd('\').Length + 1).Replace('\', '/')
$componentEngine = Split-Path -Parent $PSScriptRoot
$componentToolRoot = (Join-Path $componentEngine 'Saved/Tools/SourceCreation') + '/'
$componentArguments = @('run', '--project', (Join-Path $PSScriptRoot 'SourceCreation/SharpEngine.SourceCreation.csproj'),
    "-p:SharpEngineRoot=$componentToolRoot", '--', '--project', $componentManifest.FullName,
    '--engine-root', $componentEngine, '--module', $componentRelative, '--name', $Name,
    '--configuration', $Configuration, '--template', 'component')
if ($Directory) { $componentArguments += @('--directory', $Directory) }
if ($Namespace) { $componentArguments += @('--namespace', $Namespace) }
& dotnet @componentArguments
if ($LASTEXITCODE -ne 0) { throw 'Component source creation failed.' }
