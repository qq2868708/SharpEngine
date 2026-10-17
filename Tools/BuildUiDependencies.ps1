param([ValidateSet('Debug', 'Development', 'Shipping')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
$engineRoot=Split-Path $PSScriptRoot -Parent
$cmakeConfiguration=switch ($Configuration) { 'Debug' {'Debug'} 'Development' {'RelWithDebInfo'} 'Shipping' {'Release'} }
$buildDirectory=Join-Path $engineRoot "Intermediate/Win64/$Configuration/Editor/External/UI"
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio=& $vswhere -latest -prerelease -property installationPath
$cmake=Join-Path $visualStudio 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (!(Test-Path $cmake)) { $cmake=(Get-Command cmake -ErrorAction Stop).Source }
& $cmake -S "$engineRoot/External/UiBuild" -B $buildDirectory -G 'Visual Studio 18 2026' -A x64
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& $cmake --build $buildDirectory --config $cmakeConfiguration --parallel 8
exit $LASTEXITCODE
