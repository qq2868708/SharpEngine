# Graphics backend selection

Editor and Game support OpenGL and, in a dual-backend build, Vulkan. Select one graphics backend for
the entire process, including scene rendering, RmlUi, and Editor Dock windows:

```powershell
& .\Binaries\Win64\Debug\Editor\Engine.exe --render-backend=vulkan
& .\Binaries\Win64\Debug\Game\Engine.exe --project C:\Games\Demo\Demo.sharpproject --render-backend=vulkan
```

Use `--render-backend=opengl` for OpenGL. Both targets default to OpenGL when
the option is omitted. The equivalent `--render-backend vulkan` spelling is
also accepted. Unknown values, a missing value, or conflicting selections fail
before window/device creation; there is no silent backend fallback.

The ProjectSelector `open-editor` and `run-game` commands forward this same
option. The PowerShell launcher exposes it as `-RenderBackend opengl|vulkan`.
This is runtime selection within the graphics implementations compiled into the target.
Game continues to exclude the managed Editor assemblies.

## Build selection

Schema 3 `.sharpproject` files accept the optional top-level property
`"renderBackends": "OpenGL"` or `"renderBackends": "Both"`. New projects explicitly
select `OpenGL`; an omitted property retains `Both` for existing projects.
Other values and non-string values are rejected. Regenerate project files after
changing this property so Rider's generated Targets use the selected setting.

`OpenGL` excludes Vulkan engine and RmlUi sources, Vulkan SDK include/library
paths, `vulkan-1.lib`, and SPIR-V generation. OpenGL GLSL is embedded as source
and compiled by the graphics driver at runtime; this mode requires no Vulkan SDK.
`Both` builds both implementations and requires `VULKAN_SDK` with the Vulkan
headers/libraries, `glslc`, `spirv-val`, and `glslangValidator`. The build launcher
also reads the persisted user/machine SDK variable when the IDE was opened before
SDK installation. The SDK supplies build tools; it does not change the default
runtime backend, which remains OpenGL in both modes.

The project command uses the manifest by default; an explicit build override takes
precedence. Engine-only builds default to `Both`:

```powershell
./Tools/SharpEngineProject.ps1 -Command Build -ProjectFile D:/Games/Demo/Demo.sharpproject -RenderBackends OpenGL
./Tools/SharpEngineBuild.ps1 -Target Editor -Configuration Development -RenderBackends Both
```

The corresponding native MSBuild property is `SharpEngineRenderBackends`.
Generated Rider Targets forward this property to the unified builder. Build mode
is not a new output-layout dimension: the selected mode replaces the same
Platform/Configuration/Target output, so projects sharing that engine output must
rebuild their selected mode and restart the executable when switching.
Server remains free of graphics dependencies.

An OpenGL build rejects `--render-backend=vulkan` before creating a graphics
device with `RENDER_BACKEND_UNAVAILABLE`; it never silently falls back.
The four-backend integration check below requires `Both` builds.

For OpenGL builds, run the same validation script with
`--render-backends OpenGL`; it checks Vulkan rejection, the default backend,
rendered frames and headless modes. Configuration parsing and generated Target
propagation can be checked with
`dotnet run --project Tools/ProjectSelectorValidation -- --render-build-only`.

The backend owns the window graphics context, UI render interface, GPU frame
boundaries, presentation, and frame readback. UI hosts own RmlUi contexts,
documents, input routing, and button behavior. Editor renders its scene inside
the UI viewport; Game renders the scene followed by its runtime UI. Neither
graphics implementation depends on `SHARPENGINE_GAME`.

Backend choice is transient startup configuration. It does not modify project
manifests, scene assets, or Undo/Redo state. The active backend is reported in
the native `RenderCore` startup log. Editor retains its semantic UI snapshots
and pinned rendered-frame resources with either API.

Game's existing `--render-offscreen` and `--null-rhi` validation modes remain
available. They cannot be combined with an explicit graphics backend. They do
not create windows or provide visible runtime UI.

After building both targets, validate all four combinations with a project
whose Game modules and startup World are built:

```powershell
python Tools/ValidateRenderBackends.py --project C:\Games\Demo\Demo.sharpproject
dotnet run --project Tools/McpUiValidation/SharpEngine.McpUiValidation.csproj -- Debug --render-backend=opengl
dotnet run --project Tools/McpUiValidation/SharpEngine.McpUiValidation.csproj -- Debug --render-backend=vulkan
```

The first check covers parameter errors, selected/default backends, bounded
scene/UI presentation and BMP readback, and headless-mode conflicts. The MCP
checks exercise coordinate input, focus, keyboard routing and pinned UI frames.
Game publishes the same `ui.describe`, `ui.pointer`, `ui.key` and pinned PNG
frame contracts through its own MCP endpoint, without loading Editor assemblies:

```powershell
python Tools/ValidateGameUiMcp.py --project C:\Games\Demo\Demo.sharpproject --render-backend opengl
python Tools/ValidateGameUiMcp.py --project C:\Games\Demo\Demo.sharpproject --render-backend vulkan --vulkan-validation
```

These checks discover each button's bounds and clip from MCP, send coordinate
down/up events, verify focus and bubbling, and compare status-region pixels from
the matching frame resource. They also check pinned-frame stability, key routing,
and rejection of stale frames, wrong targets, invalid sequences and coordinates.
No desktop automation is used. A Game MCP client uses `Game/Engine.exe --mcp-stdio`;
list and attach a Game session before calling UI tools. Session registrations
are transient per-user files under `SharpEngine/GameMcpSessions` in Local AppData.
An explicit `--mcp-session-id <id>` on the bridge can attach directly.

`--vulkan-validation` enables the installed Khronos validation layer and
synchronization checks, and rejects validation errors in the runtime log.
When a running Editor locks its executable, a native build with a different
`TargetName` can be selected using `--editor-executable` in the backend check
and `SHARPENGINE_VALIDATION_EXE` in the Editor MCP check.
