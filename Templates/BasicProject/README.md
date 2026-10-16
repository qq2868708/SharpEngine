# __DISPLAY_NAME__

This project references a SharpEngine distribution through
`Project/project.json`. The generated `Project/SharpEngine.props` exposes
`SharpEngineRoot`, `SharpEngineManagedRoot`, `SharpEngineContentRoot`, and
`SharpEngineToolsRoot` to MSBuild without copying engine source files.

The generated project references the public `SharpEngine.Managed.dll` for
`SharpEngine.Assets`, `SharpEngine.UI`, `SharpEngine.World`, and
`SharpEngine.Editor`. Build the public engine in the same configuration before
building the game; `SharpEngineManagedApi` can override the installed DLL path.
`GameEntrypoint` delegates the editor UI and viewport to that shared runtime,
while your C# logic lives in `Game.Update`.

`Managed/Game.cs` also contains a project-owned `ProjectEditorExtension` that
implements `IEditorExtension`. It registers a top-level **Project** menu plus
Scene, Object, Asset, and Folder context commands. **Snap To Grid** uses
`GetChecked` to display the real transform-snap state. Edit this class to add
commands or placements; no changes to engine RML or native C++ are needed. The
editor calls `Shutdown` to release every registration handle.
Commands can also be invoked through MCP `editor.action`, using the action
`editor.command:project.reset-transform`. Rebuild the project to load code changes
in a newly opened editor; managed hot reload is not provided by this API.

`Content/Worlds/Main.world.xml` is the startup scene and references the public
Cube/Plane assets using `engine://meshes/...` URIs. Your project assets are
registered in `Content/Assets/assets.json` under `project://...` IDs and appear
alongside the public engine assets without modifying the engine installation.

## Build the project-owned managed code

From this directory:

```powershell
dotnet build Managed/__PROJECT_ID__.csproj
```

If the engine is installed elsewhere, set the override before building:

```powershell
$env:SHARPENGINE_ROOT = 'C:\Engines\SharpEngine'
dotnet build Managed/__PROJECT_ID__.csproj
```

Resolve and validate the reference with the engine tool:

```powershell
& "$env:SHARPENGINE_ROOT\Tools\SharpEngineProject.ps1" -Command Resolve -ProjectFile .\Project\project.json
```

Build and run this project through the public native host:

```powershell
dotnet build Managed\__PROJECT_ID__.csproj
& "$env:SHARPENGINE_ROOT\x64\Debug\Engine.exe" --project .\Project\project.json
```

The `--project` option loads the generated `Build/Managed` assembly and
runtimeconfig through the `entryPoint.typeName` contract. Running the host
without `--project` retains the built-in sample game behavior.
