# SharpEngine

SharpEngine is a Windows native host with a managed C# game layer. This delivery
contains two complete rendering schemes. The Editor target uses a Win32 window,
OpenGL, and RmlUi 6.1 with FreeType and RML/RCSS documents for the editor shell,
viewport, text, controls, and input. The Game target uses a Vulkan swapchain for
the runtime scene and RmlUi's Vulkan renderer for the visible game UI in the same
frame.

## Build

The native solution builds the RmlUi/FreeType dependencies and the managed
projects as part of the Win64 build. It requires Visual Studio C++, CMake, and
.NET 8. Dependency source versions and licenses are preserved under External.

    .\Tools\GenerateProjectFiles.ps1 -EngineRoot D:\SharpEngine
    .\Tools\SharpEngineBuild.ps1 -Target Editor -Configuration Debug
    .\Tools\SharpEngineBuild.ps1 -Target Game -Configuration Development

Engine, `SharpEngine.Host`, `SharpEngine.Editor`, `SharpEngine.Runtime`, and
`SharpEngine.ComponentGenerator`, and `SharpEngine.ProjectSelector`
build from `Source/<BuildModule>`. Their products are flat in
`Binaries/Win64/<Configuration>/<Target>`; intermediates use the matching
`Intermediate/Win64/<Configuration>/<Target>/<BuildModule>` hierarchy. Supported
configurations are Debug, Development, and Shipping. The managed projects target
net8.0, so a compatible .NET 8 runtime must be installed on the machine. The
Project Selector and compile-time component generator are emitted separately under
`Binaries/Win64/<Configuration>/Tools`.

Project roles and references are declared in project files. Solutions, Target wrappers
and launch profiles are generated; do not maintain another engine-project list.
See [project organization and startup contracts](Documentation/ProjectOrganization.md).

## Run

    .\Binaries\Win64\Debug\Editor\Engine.exe

Standalone Game uses a different Target binary and requires a project with a valid
saved `world.startup`:

    .\Binaries\Win64\Development\Game\Engine.exe --project D:\Games\MyGame\MyGame.sharpproject

Game loads Runtime Modules only, starts the World immediately and does not load the
Editor assembly. Editor can open without a project; Game rejects a missing project
or startup World. Both use the same component RuntimeWorld and scene renderer,
while each target presents through its own complete backend: Editor/OpenGL and
Game/Vulkan. Game also loads `Content/UI/Main.rml` and presents its RmlUi controls
over the Vulkan scene.

The host loads the engine-owned `SharpEngine.Host.dll` bootstrap, the shared
`SharpEngine.Runtime.dll` contract, and `SharpEngine.Editor.dll` editor API.
RmlUi loads `Content/UI/Rml/Editor.rml` and `Editor.rcss`. The C# editor session
loads the startup `.sworld` and the asset registry, then sends real
mesh triangles and lines to the viewport. Hierarchy selection and viewport
ray picking update the component Inspector. Play reconstructs an independent
Runtime World from committed Authoring data; Pause freezes simulation, and Stop
discards Runtime changes and restores the Authoring document.
Projects without a startup World receive an `Untitled*` deep copy of the engine
default World; its first Save creates a project `.sworld`, registers the asset,
and updates the project startup path as one checked operation.

For bounded runs and actual framebuffer captures from both schemes:

```powershell
.\Binaries\Win64\Debug\Editor\Engine.exe --smoke-test --screenshot D:\SharpEngine\Binaries\Win64\Debug\Editor\editor-smoke.bmp
.\Binaries\Win64\Debug\Game\Engine.exe --project D:\Games\MyGame\MyGame.sharpproject --smoke-test --screenshot D:\SharpEngine\Binaries\Win64\Debug\Game\game-smoke.bmp
```

The Editor capture verifies the OpenGL scene and RmlUi editor shell. The Game
capture verifies Vulkan scene geometry and the visible RmlUi game UI together.

For headless rendering validation, Game also accepts UE-like backend switches:

```powershell
.\Binaries\Win64\Debug\Game\Engine.exe --project D:\Games\MyGame\MyGame.sharpproject -RenderOffscreen --frames 1 --screenshot D:\Temp\scene.bmp
.\Binaries\Win64\Debug\Game\Engine.exe --project D:\Games\MyGame\MyGame.sharpproject -NullRHI --frames 2
```

`RenderOffscreen` uses a deterministic CPU framebuffer. It accepts the same
renderer-neutral scene vertices as the Vulkan path, writes a 32-bit BMP, and
does not create an HWND, Vulkan instance, swapchain, or graphics context. If
no frame count is supplied it advances one frame, making the command suitable
for bounded CI checks. `NullRHI` creates no framebuffer at all; it still runs
the managed Game lifecycle and scene generation, then reports frame and draw
counts in the Game log. `RC_GetStats` exposes the same transient evidence to
native validation callers without adding it to authored or Cooked state.

## Managed lifecycle

The native host calls:

    HostEntrypoint.Initialize
    HostEntrypoint.Update
    HostEntrypoint.Shutdown
    HostEntrypoint.InputEvent

Editor Target additionally exposes UI actions, viewport interaction and MCP callbacks.
The common Host project compiles the appropriate entry for each Target; it is a library
loaded by the native executable, not a second executable with an empty Main method.

These callbacks belong only to the engine bootstrap; user projects no longer
copy an unmanaged entry point. The bootstrap loads every project Module from
`Binaries/Win64/<Configuration>/<Target>/modules.runtime.json` and calls Module
Startup/Shutdown in dependency order. Only POD values and opaque native
callbacks cross the boundary. Vulkan and C++ implementation types are
intentionally kept out of the C# project. The
versioned native API carries UI text/actions, viewport bounds, and blittable
vertex arrays. Both the built-in entry point and new projects delegate editor
behavior to `SharpEngine.Editor.EditorRuntime` from the public managed DLL.

## Public engine and new projects

`engine.json` is the schema 2 distribution manifest for engine version
`0.2.0-dev`. A game project stores a root
`<Name>.sharpproject` schema 3 manifest. Its `engine.association` resolves through
`HKCU\Software\SharpEngine\Builds`; engine source and binaries are not copied
into the project. `SHARPENGINE_ROOT` or `-EngineRoot` remains an explicit CI and
automation override.

The first Editor launch, project creation, or project-file generation automatically
registers the Engine and `.sharpproject` shell actions for the current Windows user.
Source builds, installed engines, and extracted distributions share the same
`ensure-registered` operation. Distributions must include the Project Selector
executable and its dependencies under `Binaries/Win64/<Configuration>/Tools`;
registration uses these binaries without compiling source. Existing installer
associations are preserved; otherwise each engine root receives its own GUID.
Later use repairs missing or outdated shell actions without rewriting correct
entries, and notifies Explorer when changes occur. Compilation alone does not
register anything. CI can disable automatic setup with
`SHARPENGINE_REGISTER_ON_FIRST_USE=0`.

To register an existing executable manually:

```powershell
.\Binaries\Win64\Debug\Tools\SharpEngine.ProjectSelector.exe register `
  --engine-root D:\SharpEngine
```

Registration adds `Open in SharpEngine`, `Generate Project Files`, and `Open
Visual Studio Solution` to the `.sharpproject` file context menu. First use from a
new location updates the shell commands to point at that tool. An installer may
also invoke `register --installed` explicitly; subsequent first-use checks reuse
that registration. Simply extracting files does not execute registration: launch
the Editor or a project tool once.

Create a project from the checked-out engine or an installed distribution:

```powershell
& .\Tools\SharpEngineProject.ps1 -Command New -Name MyGame -Directory ..\Samples
& .\Tools\SharpEngineProject.ps1 -Command Resolve -ProjectFile ..\Samples\MyGame\MyGame.sharpproject
& .\Tools\SharpEngineProject.ps1 -Command Doctor -ProjectFile ..\Samples\MyGame\MyGame.sharpproject
& .\Tools\SharpEngineProject.ps1 -Command Build -ProjectFile ..\Samples\MyGame\MyGame.sharpproject -Target Editor -Configuration Debug -Platform Win64
```

`New` also generates `MyGame.sln`. `GenerateProjectFiles` forces a refresh for a
project, while `Tools\GenerateProjectFiles.ps1` can regenerate either the Engine
solution or a project solution. `OpenSolution` refreshes only when its stamp is
stale and then opens it.
A generated solution shows three logical roots: `Engine` contains engine source,
engine `Plugins`, and engine `Config`, while `Project` contains project `Source`,
project `Plugins`, and `Config`. `BuildTarget` contains the generated build-entry
wrappers. Plugin Modules are nested below their owning
`Plugins/<Name>/Source` folder instead of being mixed with ordinary project
Modules. An Engine-only solution omits the empty `Project` root.
The generated SDK-style `MyGame.Editor Target` and `MyGame.Game Target` invoke the
unified build pipeline. The selected Target owns the entire build; other projects
remain visible for editing and IntelliSense and are not built a second time by the solution.

Each project Module is one `.csproj`, one DLL, and one `ISharpEngineModule` entry.
Module ID/type come from evaluated MSBuild properties; dependencies come from
real `ProjectReference` items. Engine feature Modules are also separate
`.csproj`/DLL boundaries, but are statically linked into the Host and identify
their lifecycle metadata with `SharpEngineModuleAttribute`; `EngineModuleHost`
starts Runtime features on both targets and Editor features only in Editor. The
native host keeps the sample workspace when no project is specified. Run a
generated project with:

```powershell
& .\Tools\SharpEngineProject.ps1 -Command OpenEditor `
  -ProjectFile ..\Samples\MyGame\MyGame.sharpproject
```

The build emits the schema 2
`Binaries/Win64/<Configuration>/<Target>/modules.runtime.json` beside the flat
Module assemblies, with resolved entry types and dependency order. Project
intermediates use `Intermediate/Win64/<Configuration>/<Target>/<BuildModule>`.

Engine-owned feature Modules use the same explicit project boundary without a
Plugin descriptor. For example, Timeline is built from separate
`SharpEngine.Timeline.Runtime` and `SharpEngine.Timeline.Editor` projects under
`Source/`; the Engine Host references the Runtime project for Game and both
projects for Editor. A source folder inside `SharpEngine.Runtime` or
`SharpEngine.Editor` is only organization and does not create this boundary.

Plugins are explicit containers around reusable Modules. Add a `.sharpplugin` path to
the project's `plugins` array using `project://Plugins/...` or
`engine://Plugins/...`; the descriptor lists its Module `.csproj` files and may declare
an `assets.manifest` under the Plugin's `Content/` directory. Plugin Modules use the
same `ISharpEngineModule` contract and are merged into the project's dependency graph.
Declared Plugin Content is registered under `engine://plugins/<plugin-id>/...` or
`project://plugins/<plugin-id>/...` and appears below `Plugins/<Name>/Content` in the
Content Manager. The Plugin container has no startup callback, and ordinary project
Modules do not need a Plugin descriptor.

Use **File > New C# Class...** to create a Component or plain class in an existing
Module. Project source edits build in the background and reload when Stopped;
Playing/Paused sessions apply the pending build after Stop. Failed reloads keep
the Editor open and can recover after another source edit. See
[C# creation and project code reload](Documentation/CSharpSourceCreation.md) for
namespace rules, MCP commands, compatibility checks and validation.

### Scene navigation, hierarchy, and mesh import

The Scene viewport supports RMB orbit, MMB pan, and mouse-wheel zoom. The navigation
camera is a normal serialized camera entity, so Save writes the new camera transform
to the project world. Entities can use `parent="id"`; local transforms resolve through
the full parent chain for rendering and ray picking. Cycles and missing parents are
rejected during world loading. Hierarchy drag supports parent, root, before, and
after drops while preserving world transform. Reparent and gizmo edits share the
revisioned Undo/Redo history. This phase disables both while Playing or Paused.

AI-facing scene and asset snapshots are available from C#:

```csharp
string sceneJson = runtime.DescribeSceneJson();
string assetsJson = runtime.DescribeAssetsJson();
string editorSnapshot = runtime.DescribeEditorJson();
// Or directly: world.ToJson(), world.Describe(), registry.ToJson()
```

The scene JSON includes every entity, hierarchy links, local transforms, resolved
world matrices, camera ranges, colors, and asset/material URIs. Mesh assets use the
manifest-backed JSON format and can be imported from Wavefront OBJ:

```powershell
& .\Tools\SharpEngineMesh.ps1 -Command Import -ProjectRoot .\MyGame `
  -Input .\assets\character.obj -Name Character -Id meshes/character
```

The importer triangulates faces and registers a `project://meshes/character` asset.
The viewport also accepts a manifest entry with `importer: "SharpEngine.Obj"` for
direct source OBJ loading.

File > Export Scene JSON writes the combined scene and asset snapshot to
`Saved/Scenes/Main.scene.json`, making the current editor state directly consumable
by tools and AI workflows.

Viewport navigation uses W/A/S/D for planar movement, Q/E for vertical movement,
Shift for a speed boost, RMB orbit, MMB pan, and the wheel for zoom. Orbit yaw and
pitch follow the drag direction. `File > Engine Settings` opens the engine-owned
settings window; Navigation > Invert Y Axis reverses vertical pitch. Engine
defaults come from the engine assembly, while explicit project overrides are
saved under `Config/EngineSettings/<settings-id>.json`.

`File > Project Settings` opens the same schema-driven editor filtered to settings
types declared by project Modules. A Runtime or Editor Module assembly registers
each marked type automatically before its Module entry point starts:

```csharp
[DevelopmentSettings("mygame.gameplay", SettingsScope.Project, 1,
    DisplayName = "Gameplay", Category = "Game")]
public sealed class GameDevelopmentSettings : DevelopmentSettings
{
    [Setting("moveSpeed", DisplayName = "Move Speed", Minimum = 0)]
    public float MoveSpeed { get; init; } = 600.0f;
}

var settings = DevelopmentSettings.Get<GameDevelopmentSettings>();
```

The explicit IDs survive CLR type and property renames. Edits publish a validated
in-memory snapshot immediately, support panel-local Undo/Redo and Revert, and are
written only by Save. Project-defined values are stored under
`Config/ProjectSettings/<settings-id>.json`. Nested marked objects and deterministic
collections are supported; schema cycles, abstract values, delegates, and other
non-deterministic shapes fail during registration instead of becoming hidden nulls.
Objects expand into marked fields; arrays and dictionaries currently use a JSON
value editor. Static reads return detached snapshots, so editing a returned list
cannot bypass validation. Read again to observe subsequent committed changes.
Valid external file edits reload on the editor thread; invalid files or conflicts
with unsaved edits retain the last valid state and show a diagnostic. Unknown schema
versions are rejected; custom version migrations are not implemented yet.
The legacy `Saved/Config/EngineSettings.json` navigation value is imported without
changing that file; Save writes the new project override.

### Unified input routing

Win64 keyboard and mouse input enter one engine route. RmlUi receives the
normalized event first so focus, hit testing, pointer capture, text, and IME
retain native UI behavior. An event consumed by UI stops there; an unhandled
event is evaluated by active `InputMappingContext` assets in deterministic
`Editor`, `RuntimeUi`, then `Gameplay` order. Project code does not subscribe to
Win32 or maintain a separate UI input configuration.

`InputAction` and `InputMappingContext` are versioned JSON assets registered in
the normal asset manifest. Runtime Modules bind semantic actions through the
shared service:

```csharp
_move = context.Input?.BindAction(InputRoute.Gameplay,
    "project://input/actions/gameplay.move", 0, input =>
    {
        if (input.Phase == InputActionPhase.Performed)
            Move(input.Value.X, input.Value.Y);
        return true;
    });
```

Returning `true` consumes the action before lower routes. The default project
template maps W/A/S/D to a continuous Axis2D move action and the left mouse
button to a primary Button action. Engine Editor actions use the same router:
F5 toggles Play/Pause and F8 stops. `sharpengine://input` and `input.describe`
expose loaded actions, active contexts, live controls, diagnostics, and recent
routed events. Native platform events remain an internal engine SPI.

Open **Window > Input Routing Demo** to try the first Win64 keyboard/mouse
implementation. It starts Play and opens a focusable play area, a text field,
and an on/off UI consumption switch. Space increments either the UI counter or
the Gameplay counter; W/A/S/D moves a marker. Typing in the text field does not
activate Gameplay. Stop with F8 after returning focus to the play area.

Run `dotnet run --project Tools/InputValidation/SharpEngine.InputValidation.csproj -c Debug -- .`
for routing and asset validation, and
`dotnet run --project Tools/InputUiValidation/SharpEngine.InputUiValidation.csproj -c Debug -- .`
for rendered RmlUi input acceptance and a PNG capture. This first version loads
input assets at startup; live rebinding/editor authoring, multi-player device
assignment, persistent replay files and a Gamepad backend are not implemented.
The 3D viewport draws a fixed corner orientation gizmo with world X/Y/Z axes
in red/green/blue. It is a scene-only surface; UI authoring no longer changes
the viewport mode.

RML assets open in the dedicated, dockable UI Editor. It owns document tabs,
an element hierarchy, a 1280x720 screen preview, an Inspector, diagnostics, and
document-local Undo/Redo and Save. The preview resolves linked RCSS rules and
supports RmlUi-native Flex layout plus explicit Absolute positioning. RML stores
stable element IDs, optional `data-view-model` contract IDs, `data-bind` paths,
and `data-command` actions. Selecting a `RmlDocument` in Content Manager opens
it as a UI document instead of embedding it in the 3D viewport.

Directional lighting is derived only from serialized scene lights. Multiple
lights accumulate linear RGB color and intensity in the current CPU preview;
there is no hidden preview light or ambient light, so a World with no effective
lights renders mesh RGB as black. Camera and light orientation inherit parent
rotation while ignoring parent scale; parent scale still affects child position.

The editor menu hierarchy follows the usual Unreal-style split: `File` contains
New Project, Open Project, Save World, scene export, Engine Settings and Project
Settings; `Edit` contains Undo/Redo. The toolbar keeps only frequent Save,
Export, and Play/Pause/Stop controls. Move, Rotate, and Scale are scoped to the
3D viewport.

### Embedded MCP

The Editor toolbar shows **MCP : 17700** on the right. Click it to edit the port;
**Confirm & Restart** saves `Config/Mcp.json` and restarts MCP immediately. Connect
HTTP clients to `http://127.0.0.1:17700/mcp` (or the configured port). Each project
has its own configuration; without a project the engine workspace owns the file.

The public editor exposes an engine-owned MCP stdio session bridge. Start it with:

```powershell
@'
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"editor.sessions.list","arguments":{}}}
'@ | .\Binaries\Win64\Debug\Editor\Engine.exe --mcp-stdio
```

The transport writes protocol responses only to stdout and diagnostics to stderr.
It first lists live Editor sessions and lets the user attach to an existing
Editor or launch an isolated, fully rendered Editor. A connection cannot switch
sessions after attachment. The bridge itself never loads a World or executes
project Modules.

The attached session exposes scene, asset, content, editor, capability, and live
RmlUi resources. `ui.describe`, `ui.pointer`, `ui.key`, and pinned PNG frame
resources provide semantic and rendered evidence without desktop control.
`viewport.input` and `editor.action` remain semantic shortcuts and are not proof
that a visible control is operable.

Build the Editor, then run the rendered MCP integration contract with:

```powershell
dotnet run --project Tools/McpUiValidation/SharpEngine.McpUiValidation.csproj -c Debug
```

### Asset manifests

The public engine owns `Content/Assets/assets.json`, referenced by the
`assets.manifest` entry in `engine.json`. A project gets its own
`Content/Assets/assets.json` from the BasicProject template and references it
from the root `.sharpproject`. The two manifests are intentionally separate:
engine records use `engine://...` IDs and project records use `project://...`
IDs. Ownership does not pretend to be a filesystem permission boundary. Engine
Worlds are ordinary editable Worlds; using one as a preset explicitly deep-copies
it, so later changes to the source do not alter existing instances.

`Content` is the authoritative asset root. Non-asset implementation files belong under
`Resources`; editor and plugin icons use `sharpengine-resource://...` references and
are not added to `assets.json`. The built-in editor icons live in
`Resources/Editor/Icons`, while RML source documents remain under `Content/UI/Rml`.

`SharpEngine.Assets.AssetRegistry.Load(engineRoot, projectRoot)` loads the engine and
project manifests plus every Plugin manifest declared by them. `Assets`,
`EngineAssets`, `ProjectAssets`, `PluginAssets`, `Plugins`, `Find`, and `Search`
provide the query surface used by the Project dock and viewport. An entry is
portable because its `path` is relative to the owning engine/project root;
`AssetDescriptor.ResolvePath` validates that it cannot escape that root.

`Source/SharpEngine.Runtime/SharpEngine.Runtime.csproj` builds the minimal
Runtime-safe Module contract. Runtime Modules reference only this DLL.
`Source/SharpEngine.Editor/SharpEngine.Editor.csproj` builds the asset, world,
UI, and editor APIs; Editor Modules reference both public DLLs. The unified
project builder supplies their resolved paths to MSBuild and copies no engine
source.

Built-in meshes are real indexed triangle data:
`engine://meshes/cube` is a unit cube (8 positions, 12 triangles) and
`engine://meshes/plane` is a unit XZ plane (4 positions, 2 triangles).
`engine://materials/default` supplies the standard shader, base color,
metallic, and roughness parameters. Load them through
`AssetContent.LoadMesh(registry, id)` and
`AssetContent.LoadMaterial(registry, id)`; the formats are UTF-8 JSON files in
`Content/Assets/Meshes` and `Content/Assets/Materials`.

Double-clicking `.sharpproject` invokes `SharpEngine.ProjectSelector`, which
resolves the registered Engine association and launches that Engine's Editor.
The native host still accepts an explicit `--project` argument, but validates
the project ID/version binding against `SHARPENGINE_ROOT` rather than selecting
another Engine in-process. The project directory contains no copied engine sources.

## C# editor commands, menus, Toolbar, and context menus

Project-owned C# code can extend the shared editor without changing the native
host or writing project-specific RML. An Editor Module registers its extension
during Module Startup:

```csharp
public sealed class MyEditorModule : ISharpEngineModule
{
    private IDisposable? registration;
    public void Startup(ModuleContext context)
    {
        var editor = (EditorModuleContext)context;
        registration = editor.RegisterEditorExtension(new ProjectEditorExtension());
    }
    public void Shutdown() => registration?.Dispose();
}
```

Register commands separately from their UI placements. The returned handles
unregister the contribution when disposed; an extension releases its handles in
`Shutdown`. The editor owns registered extensions and calls `Shutdown` when it
closes, when the extension's handle is disposed, or if `Startup` throws.

```csharp
var command = editor.Commands.Register(new EditorCommand(
    "project.reset-position", "Reset Position",
    context =>
    {
        context.SelectedEntity!.Position = SharpEngine.World.WorldVector3.Zero;
        context.MarkDirty();
        context.RefreshUi();
    },
    context => context.PlayState == EditorPlayState.Stopped &&
        context.SelectedEntity is not null));

var category = editor.Toolbar.RegisterCategory(
    new EditorToolbarCategory("project", order: 500));
var tool = editor.Toolbar.RegisterTool(new EditorToolbarTool(
    "project-transform", "project", "Transform",
    ["project.reset-position"]));
```

Toolbar icons use a built-in name or a path-safe resource URI. A plugin-owned
editor icon stays under that plugin's `Resources` directory:

```csharp
var tool = editor.Toolbar.RegisterTool(new EditorToolbarTool(
    "plugin-open", "plugin", "Plugin",
    ["plugin.open"], icon:
        "sharpengine-resource://engine/Plugins/EngineTestPlugin/Resources/Editor/Icons/plugin.tga"));
```

Use `project` instead of `engine` in the URI for a project plugin. Resource URIs are
resolved by the native RmlUi file interface and cannot escape the selected root.

These types live in `SharpEngine.Editor` in the public `SharpEngine.Editor.dll`.
The BasicProject template includes a working top-level `Project` menu and Scene,
Object, Asset, and Folder context menus. `EditorContext` and command callbacks
expose the current world, assets, unified `EditorSelection`, play state, dirty
state, status, save, and UI refresh operations. Right-clicking first replaces the
active selection, then evaluates context command state. Retrieve world entities
through the context when executing. Stop restores the original Authoring document.
Component Inspector and `components.edit` operations share transactions. Call `MarkDirty` after changing non-transactional extension
data; saving clears it. Built-in hierarchy and gizmo changes use the shared
revisioned Undo/Redo layer. Arbitrary project extension field writes are not yet
automatically converted into transactions.

`CanExecute` is evaluated each frame and rechecked before execution. Optional
`GetChecked` always reads the owning business state; UI code never caches or
optimistically flips it. Keep both callbacks fast and side-effect free.

`EditorMenuRegistry` registers top-level menus, categories, and command items.
`EditorToolbarRegistry` requires explicit categories; categories are separated by
vertical rules and may align Left, Center, or Right. A tool with one command runs
directly, while a tool with multiple commands renders a dropdown. The built-in
Level category contains Save Current Level and Browse Current Level, and Playback
contains the stateful Play/Pause and Stop tools. `EditorContextMenuRegistry`
places commands in Scene, Object, Asset, and Folder contexts.

Both UI clicks and MCP use `editor.command:<id>` actions. `editor.describe`
includes commands, current checked/enabled state, menu placements, Toolbar
categories/tools, context placements, and unified selection. Registration IDs
are case-insensitive and must be unique in their owning registry. The former
`EditorToolbarButton` and `Section` API was removed without a compatibility shim.
Separate extension DLL discovery and custom Dock/Inspector registration remain
later work.

### Dock layout

The built-in Viewport, UI Editor, Hierarchy, Content Manager, Inspector, and
Console are real Dock panes. UI Editor contains its own asset-document tabs; it
is not a viewport mode. A drag always keeps a floating pane ghost visible. Dropping on
the nearest content edge splits left, right, top, or bottom and previews the
exact result; dropping on a Tab bar merges at the shown insertion line. Dropping
in the content center creates a floating window. Non-Viewport panes can detach
into owned Win32 windows, and each window uses the same recursive Dock model.
Viewport can move within the main window but cannot detach or close. `View >
Save Layout` explicitly saves only the main-window tree to
`Saved/Config/EditorLayout.json`; floating windows are session-only. `Reset
Layout` reloads `Config/DefaultEditorLayout.json`.

## Declarative UI and world data

The active editor UI is RML (XML-like markup) plus RCSS. RmlUi handles flex
layout, font measurement, clipping, scrolling, hover/focus, keyboard input,
and IME integration through its Win32 backend. Actions use stable
`data-action`/`action` attributes, such as `editor.play` and
`editor.select-entity:player`, which dispatch to C#. Public and project asset
lists come from their manifests, rather than hard-coded example buttons.

### Content Manager

The Project dock is an Unreal-style Content Manager. When a project is open it
starts at that project's `Content` directory, lists folders before files, and
supports entering folders, returning to the parent, recursive search, source
and type filters. Manifest metadata is attached when a file is registered, so
the Inspector can show its type, owner, importer, path, availability, and
editability state. Engine ownership is not itself a write prohibition; individual
asset types and records define their editing workflow. The browser state is also
available through `EditorRuntime.DescribeContentJson()`, the
`content.describe` MCP tool, and the `sharpengine://content` resource.

`View` exposes Content Manager 1–4 independently. Only manager 1 is open in the
default layout. Each instance owns its folder, search, filters, selection, and
Tile/List mode (Tile by default, using asset-type icons). Interacting with a
manager makes its selection the editor's current selection; other managers keep
their own selections. External Browse Current Level commands target the last
active manager. Closing it falls back to the most recently used open manager;
if all are closed, external browsing reopens manager 1.

Save Layout stores all four browser states and the active manager in the existing
`Saved/Config/EditorLayout.json` (schema v2). Schema v1 layouts migrate on load,
retaining the original `content` pane as manager 1 and adding hidden managers
2–4. Main-window docking persists; floating window placement remains session-only.
The content MCP snapshot is schema v2 with `activePaneId` and four `managers`.
Instance actions use `editor.content.<operation>:<paneId>:<value>`, for example
`editor.content.view:content-2:list` and `editor.content.select:content:engine://meshes/cube`.

Focused validation:
`dotnet run --project Tools/EditorValidation -c Debug -- D:/SharpEngine --content-only`
and `dotnet run --project Tools/McpUiValidation -c Debug -- Debug --content-only`.
The latter launches an isolated rendered editor and saves frame evidence under
`Saved/Validation/ContentManager` without saving the user's layout.

`Source/SharpEngine.Editor/UI/UiFramework.cs` and `EngineUi.cs` remain legacy
compatibility/experimentation APIs; new projects no longer receive `*.ui.xml`
assets. Runtime UI source assets use RML and RCSS, while the editor shell itself
continues to use `Content/UI/Rml/Editor.rml`.

World schema v3 uses strict JSON in `.sworld` files; pre-stable builds reject old
schemas without migration. Entities contain versioned component records with
stable instance/type IDs and explicit defaults. Runtime Modules use generated
field descriptors, default states, private-field access and factories. The
Inspector supports Add/Remove/Reset/Patch and Runtime context switching.
`.sprefab` assets contain an independently edited subtree and instantiate by
deep copy with remapped IDs. See `Documentation/InspectorComponentDesign.md`
and `Content/Worlds/README.md` for the contract and validation commands.
The viewport applies model/view/projection,
frustum clipping, lighting, and native depth testing. Mesh and material data
are resolved through the same asset registry as the Project panel. See
`Content/Worlds/README.md` for the format. The `.sasset` format for non-World
assets remains later work. Engine and project diagnostics use the unified
structured logging pipeline described in
`Documentation/Logging.md`; compiler transcripts remain in
`Saved/Logs/ProjectBuild.log`.

```powershell
dotnet run --project Tools/WorldValidation/SharpEngine.WorldValidation.csproj -- D:/SharpEngine
dotnet run --project Tools/WorldAuthoringValidation/SharpEngine.WorldAuthoringValidation.csproj
dotnet run --project Tools/EditorLayoutValidation/SharpEngine.EditorLayoutValidation.csproj
dotnet run --project Tools/EditorValidation/SharpEngine.EditorValidation.csproj -- D:/SharpEngine
dotnet run --project Tools/LoggingValidation/SharpEngine.LoggingValidation.csproj -- D:/SharpEngine/Saved/Validation/Logging
```
