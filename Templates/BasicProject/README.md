# __DISPLAY_NAME__

This project references a public SharpEngine distribution from the root
`__DISPLAY_NAME__.sharpproject` file. `SHARPENGINE_ROOT` may override the
manifest-relative engine path.

The project contains two C# Modules:

- `Source/__PROJECT_NAMESPACE__`: Runtime Module, referencing only
  `SharpEngine.Modules.dll`.
- `Source/__PROJECT_NAMESPACE__.Editor`: Editor Module, depending on the Runtime
  Module and registering project menus and context commands.

Build and run through the engine-owned project builder:

```powershell
& "$env:SHARPENGINE_ROOT\Tools\SharpEngineProject.ps1" -Command Build `
  -ProjectFile .\__DISPLAY_NAME__.sharpproject `
  -Target Editor -Configuration Debug -Platform Win64

& "$env:SHARPENGINE_ROOT\x64\Debug\Engine.exe" `
  --project .\__DISPLAY_NAME__.sharpproject
```

Direct per-Module compilation is not a supported project workflow in this
version. The unified build validates the complete graph and emits
`Binaries/Managed/modules.runtime.json`. Code changes require rebuilding the
project and restarting the editor; managed hot reload is not implemented.

`Content/Worlds/Main.world.xml` is the startup scene. Project assets are
registered in `Content/Assets/assets.json` under `project://` IDs.
