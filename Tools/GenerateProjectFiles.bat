@echo off
setlocal

set "ENGINE_ROOT=%~dp0.."
for %%I in ("%ENGINE_ROOT%") do set "ENGINE_ROOT=%%~fI"

if "%~1"=="" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0GenerateProjectFiles.ps1" ^
        -EngineRoot "%ENGINE_ROOT%"
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0GenerateProjectFiles.ps1" ^
        -EngineRoot "%ENGINE_ROOT%" -ProjectFile "%~f1"
)

exit /b %ERRORLEVEL%
