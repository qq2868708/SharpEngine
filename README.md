# SharpEngine

SharpEngine is a Windows native host with a managed C# game layer. The editor
uses RmlUi 6.1, FreeType, and RML/RCSS documents for layout, text, controls,
and input. The executable owns Win32, CoreCLR, and the current OpenGL preview
backend. The existing Vulkan code remains available for later renderer work;
it does not render the current editor viewport.

## Build

The native solution builds the RmlUi/FreeType dependencies and the managed
projects as part of the Win64 build. It requires Visual Studio C++, CMake, and
.NET 8. Dependency source versions and licenses are preserved under External.

    & 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' SharpEngine.sln /m /restore /p:Configuration=Debug /p:Platform=x64

Engine, `SharpEngine.Game`, `SharpEngine.Managed`, `SharpEngine.Modules`, and
`SharpEngine.ProjectSelector`
build from `Source/<BuildModule>`. Their products are flat in
`Binaries/Win64/<Configuration>/Editor`; intermediates use the matching
`Intermediate/Win64/<Configuration>/Editor/<BuildModule>` hierarchy. Supported
configurations are Debug, Development, and Shipping. The managed projects target
net8.0, so a compatible .NET 8 runtime must be installed on the machine. The
Project Selector is emitted separately under
`Binaries/Win64/<Configuration>/Tools`.

## Run

    .\Binaries\Win64\Debug\Editor\Engine.exe

The host loads the engine-owned `SharpEngine.Game.dll` bootstrap, the shared
`SharpEngine.Modules.dll` contract, and `SharpEngine.Managed.dll` editor API.
RmlUi loads `Content/UI/Rml/Editor.rml` and `Editor.rcss`. The C# editor session
loads the startup `.sworld` and the asset registry, then sends real
mesh triangles and lines to the viewport. Hierarchy selection and viewport
ray picking update the Inspector. Play rotates a demo mesh through C# state;
Pause freezes it, and Stop restores the same Authoring document's edit transforms.
Projects without a startup World receive an `Untitled*` deep copy of the engine
default World; its first Save creates a project `.sworld`, registers the asset,
and updates the project startup path as one checked operation.

For a bounded run and an actual framebuffer capture:

```powershell
.\Binaries\Win64\Debug\Editor\Engine.exe --smoke-test --screenshot D:\SharpEngine\Binaries\Win64\Debug\Editor\editor-smoke.bmp
```

## Managed lifecycle

The native host calls:

    GameEntrypoint.Initialize
    GameEntrypoint.Update
    GameEntrypoint.Shutdown
    GameEntrypoint.PointerDown     (optional)
    GameEntrypoint.InvokeUiAction  (optional)
    GameEntrypoint.ViewportInput  (optional)
    GameEntrypoint.ViewportKey    (optional)
    GameEntrypoint.RunMcp         (optional, used by --mcp-stdio)

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

Register a source Engine once, including the `.sharpproject` shell actions:

```powershell
.\Binaries\Win64\Debug\Tools\SharpEngine.ProjectSelector.exe register `
  --engine-root D:\SharpEngine
```

Create a project from the checked-out engine or an installed distribution:

```powershell
& .\Tools\SharpEngineProject.ps1 -Command New -Name MyGame -Directory ..\Samples
& .\Tools\SharpEngineProject.ps1 -Command Resolve -ProjectFile ..\Samples\MyGame\MyGame.sharpproject
& .\Tools\SharpEngineProject.ps1 -Command Doctor -ProjectFile ..\Samples\MyGame\MyGame.sharpproject
& .\Tools\SharpEngineProject.ps1 -Command Build -ProjectFile ..\Samples\MyGame\MyGame.sharpproject -Target Editor -Configuration Debug -Platform Win64
```

`New` also generates `MyGame.sln`. `GenerateProjectFiles` forces a refresh,
while `OpenSolution` refreshes only when its stamp is stale and then opens it.
A source Engine Solution shows the Engine and Game logical folders, including
Engine Build Modules and project Modules even when they live on different drives.
The generated SDK-style `MyGame.Editor Target` is the only Solution build entry
for project code; Module projects remain visible for editing and IntelliSense but
cannot be built directly.

Each Module is one `.csproj`, one DLL, and one `ISharpEngineModule` entry.
Module ID/type come from evaluated MSBuild properties; dependencies come from
real `ProjectReference` items. The native host keeps the sample workspace when no
project is specified. Run a generated project with:

```powershell
& .\Tools\SharpEngineProject.ps1 -Command OpenEditor `
  -ProjectFile ..\Samples\MyGame\MyGame.sharpproject
```

The build emits the schema 2
`Binaries/Win64/<Configuration>/<Target>/modules.runtime.json` beside the flat
Module assemblies, with resolved entry types and dependency order. Project
intermediates use `Intermediate/Win64/<Configuration>/<Target>/<BuildModule>`.

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
pitch follow the drag direction; `Engine Settings > Invert Y Axis` reverses the
vertical pitch and is saved under the project `Saved/Config/EngineSettings.json`.
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
New Project, Open Project, Save World, and scene export; `Edit` contains Engine
Settings and navigation preferences. The toolbar keeps only frequent Save,
Export, and Play/Pause/Stop controls. Move, Rotate, and Scale are scoped to the
3D viewport.

### Embedded MCP

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

`SharpEngine.Assets.AssetRegistry.Load(engineRoot, projectRoot)` loads both
manifests. `Assets`, `EngineAssets`, `ProjectAssets`, `Find`, and `Search`
provide the query surface used by the Project dock and viewport. An entry is
portable because its `path` is relative to the owning engine/project root;
`AssetDescriptor.ResolvePath` validates that it cannot escape that root.

`Source/SharpEngine.Modules/SharpEngine.Modules.csproj` builds the minimal
Runtime-safe Module contract. Runtime Modules reference only this DLL.
`Source/SharpEngine.Managed/SharpEngine.Managed.csproj` builds the asset, world,
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

These types live in `SharpEngine.Editor` in the public `SharpEngine.Managed.dll`.
The BasicProject template includes a working top-level `Project` menu and Scene,
Object, Asset, and Folder context menus. `EditorContext` and command callbacks
expose the current world, assets, unified `EditorSelection`, play state, dirty
state, status, save, and UI refresh operations. Right-clicking first replaces the
active selection, then evaluates context command state. Retrieve world entities
through the context when executing. Stop restores simulation transforms on the
same Authoring document. Call `MarkDirty` after changing non-transactional extension
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

`Source/SharpEngine.Managed/UI/UiFramework.cs` and `EngineUi.cs` remain legacy
compatibility/experimentation APIs; new projects no longer receive `*.ui.xml`
assets. Runtime UI source assets use RML and RCSS, while the editor shell itself
continues to use `Content/UI/Rml/Editor.rml`.

World schema v2 uses the `.sworld` extension and requires explicit light colors.
World data describes stable entity IDs, asset URIs, editable Transform,
cameras, and directional lights. The viewport applies model/view/projection,
frustum clipping, lighting, and native depth testing. Mesh and material data
are resolved through the same asset registry as the Project panel. See
`Content/Worlds/README.md` for the format. The `.sasset` format for non-World
assets and the full log/output data pipeline remain later work.

```powershell
dotnet run --project Tools/WorldValidation/SharpEngine.WorldValidation.csproj -- D:/SharpEngine
dotnet run --project Tools/WorldAuthoringValidation/SharpEngine.WorldAuthoringValidation.csproj
dotnet run --project Tools/EditorLayoutValidation/SharpEngine.EditorLayoutValidation.csproj
dotnet run --project Tools/EditorValidation/SharpEngine.EditorValidation.csproj -- D:/SharpEngine
```
