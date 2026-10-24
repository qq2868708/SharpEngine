[CmdletBinding()]
param(
    [string]$EngineRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')),
    [ValidateSet('Debug', 'Development')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$VsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$VsRoot = & $VsWhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$VsRoot)
{
    throw 'Visual C++ build tools are required.'
}
Import-Module (Join-Path $VsRoot 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $VsRoot -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$Output = Join-Path $EngineRoot ('Saved/Validation/RmlUiSourceUrl/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $Configuration)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$NativeConfiguration = 'RelWithDebInfo'
$RuntimeFlag = '/MD'
if ($Configuration -eq 'Debug')
{
    $NativeConfiguration = 'Debug'
    $RuntimeFlag = '/MDd'
}
$UiRoot = Join-Path $EngineRoot "Intermediate/Win64/$Configuration/Editor/External/UI"
$Library = Join-Path $UiRoot "$NativeConfiguration/rmlui.lib"
if (!(Test-Path -LiteralPath $Library))
{
    throw "Build the $Configuration Editor target first: $Library"
}
$FreeTypeName = 'freetype.lib'
if ($Configuration -eq 'Debug')
{
    $FreeTypeName = 'freetyped.lib'
}
$FreeType = Join-Path $UiRoot "FreeType/$NativeConfiguration/$FreeTypeName"
$Executable = Join-Path $Output 'RmlUiSourceUrlValidation.exe'
$Arguments = @('/nologo', '/std:c++20', '/EHsc', '/utf-8', $RuntimeFlag, '/DRMLUI_STATIC_LIB',
    ("/I" + (Join-Path $EngineRoot 'Source/Engine')),
    ("/I" + (Join-Path $EngineRoot 'External/RmlUi/Include')),
    (Join-Path $PSScriptRoot 'Program.cpp'), ("/Fo" + (Join-Path $Output 'Program.obj')),
    ("/Fe" + $Executable), '/link', $Library, $FreeType, 'user32.lib')
& cl.exe @Arguments *> (Join-Path $Output 'build.log')
if ($LASTEXITCODE -ne 0)
{
    Get-Content (Join-Path $Output 'build.log') -Tail 20
    throw 'Native URL regression build failed.'
}
& $Executable
if ($LASTEXITCODE -ne 0)
{
    throw 'Native URL regression failed.'
}
