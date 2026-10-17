# __DISPLAY_NAME__

This project references a registered SharpEngine distribution from the root
schema 3 `__DISPLAY_NAME__.sharpproject` file. `SHARPENGINE_ROOT` may explicitly
override its Engine association for CI and automation.

The project contains two C# Modules:

- `Source/__PROJECT_NAMESPACE__`: Runtime Module, referencing only
  `SharpEngine.Runtime.dll`.
- `Source/__PROJECT_NAMESPACE__.Editor`: Editor Module, depending on the Runtime
  Module and registering project menus and context commands.

`__DISPLAY_NAME__.sln` is generated when the project is created. It shows the
associated source Engine and the project Modules even when they are in unrelated
directories. Build from the generated Editor or Game Target, or use
the engine-owned project builder:

```powershell
& "$env:SHARPENGINE_ROOT\Tools\SharpEngineProject.ps1" -Command Build `
  -ProjectFile .\__DISPLAY_NAME__.sharpproject `
  -Target Editor -Configuration Debug -Platform Win64

& "$env:SHARPENGINE_ROOT\Tools\SharpEngineProject.ps1" -Command OpenEditor `
  -ProjectFile .\__DISPLAY_NAME__.sharpproject
```

Direct per-Module compilation is rejected. Visual Studio design-time builds are
allowed only for IntelliSense. The unified build validates the complete graph and emits
the schema 2 `Binaries/Win64/<Configuration>/<Target>/modules.runtime.json`
beside the flat Module assemblies. Intermediates use the matching
`Intermediate/Win64/<Configuration>/<Target>/<BuildModule>` hierarchy. Code
changes are built automatically while the project Editor is open. New project
code is loaded when Stopped; Playing/Paused sessions apply it after Stop. Use
File > New C# Class to create a class or Component in an existing Module, or
File > Build Project Code to retry a failed build. Reload failure keeps the
Editor open and can be retried after fixing source; no automatic restart occurs.

Creation copies the configured engine World preset into `Content/Worlds/Main.sworld`
and records it as `world.startup`. Game requires this explicit saved World; Editor
can also work with unsaved Worlds. Use `-Target Game` to build the standalone runtime
and `-Command RunGame` to launch it. Game never loads the Editor Module.
