# __DISPLAY_NAME__

This project references a registered SharpEngine distribution from the root
schema 3 `__DISPLAY_NAME__.sharpproject` file. `SHARPENGINE_ROOT` may explicitly
override its Engine association for CI and automation.

The manifest selects `"renderBackends": "OpenGL"`, which builds without the
Vulkan SDK. Set it to `"Both"` and regenerate project files to include Vulkan,
or pass `-RenderBackends Both` to a build command for a one-time override.
Both modes launch OpenGL by default; `--render-backend=vulkan` requires a Both
build. Changing modes replaces the shared engine target binaries and requires
restarting the Editor. See the engine's `Documentation/RenderBackends.md`.

`AGENTS.md` and `.editorconfig` define the default C# naming convention: PascalCase
for types, members (including private fields), parameters, local variables and
lambda parameters, with no `_`, `m_` or `s_` field prefixes. AI code guidance and
IDE naming suggestions use the same policy. Serialized keys and external API
names retain their required spelling. Edit these two files to customize it.

The project contains two C# Modules:

- `Source/__PROJECT_NAMESPACE__`: Runtime Module, referencing only
  `SharpEngine.Runtime.dll`.
- `Source/__PROJECT_NAMESPACE__.Editor`: Editor Module, depending on the Runtime
  Module and extending the engine-created File menu, main Toolbar, viewport
  CustomBar and context commands.

The generated Editor includes project-owned extension examples:

- **Project Preview** CustomBar inside the Viewport: frame selection, show World
  statistics, toggle grid snapping and show the project preview status.
- **Project** Toolbar dropdown: save/export the current World, show World and
  asset statistics, or describe the current selection.
- **Reset Transform**, **Snap To Grid** and **Main Camera** Toolbar tools.
- Matching commands under the engine-created **File** menu and scene/object/asset/
  folder context menus. The generated Editor also exposes the Animation Timeline
  pane with Play, Pause, Stop and Add Notify controls.

These are registered by `Source/__PROJECT_NAMESPACE__.Editor/ProjectEditorExtension.cs`;
remove or replace that extension when the project needs a different editor workflow.

`__DISPLAY_NAME__.sln` is generated when the project is created. It shows the
associated source Engine and the project Modules even when they are in unrelated
directories. Build from the generated Editor or Game Target, or use
the engine-owned project builder:

In Rider, choose a configuration-specific launch Target, such as
`__DISPLAY_NAME__.Debug.Editor.Target: Debug Editor`. It builds and launches
Debug Editor even when the active solution configuration is Development.
Solution Build still follows the active solution configuration. After upgrading
older generated files, reload the solution and choose the new launch Target once.
Stop that target's running Editor before rebuilding it.

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

For a headless authoritative server, build the `Server` Target. Server loads
Runtime Modules only and emits `Binaries/Win64/<Configuration>/Server/DedicatedServer.exe`.
The project manifest must contain `world.startup`, and a Server Runtime Module
can use the `SharpEngine.Networking` `INetworkServerRuntime` contract to register
its Schema, Commands and authority policy.

Projects do not generate a Server Target by default. Add it when the project
needs a dedicated server, then regenerate the Rider solution:

```powershell
& "$env:SHARPENGINE_ROOT\Tools\SharpEngineProject.ps1" `
  -Command AddTarget -Target Server `
  -ProjectFile .\__DISPLAY_NAME__.sharpproject `
  -EngineRoot $env:SHARPENGINE_ROOT
```

The generated solution then exposes `Debug/Development/Shipping Server` and a
matching launch profile. `RemoveTarget -Target Server` removes the generated
Server wrapper and profiles. The same operations are available as `Add Server
Target` and `Remove Server Target` from the `.sharpproject` file context menu.
