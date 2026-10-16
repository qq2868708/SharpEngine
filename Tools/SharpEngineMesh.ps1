[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Import')][string]$Command,
    [Parameter(Mandatory=$true)][string]$ProjectRoot,
    [Parameter(Mandatory=$true)][Alias('Input')][string]$InputPath,
    [string]$Name,
    [string]$Id
)

$root = (Resolve-Path $PSScriptRoot).Path
$project = (Resolve-Path $ProjectRoot).Path
$arguments = @('--project', (Join-Path $root 'MeshImport/SharpEngine.MeshImport.csproj'), '--',
    '--project-root', $project, '--input', (Resolve-Path $InputPath).Path)
if ($Name) { $arguments += @('--name', $Name) }
if ($Id) { $arguments += @('--id', $Id) }
& dotnet run @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
