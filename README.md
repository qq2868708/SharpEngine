# SharpEngine

SharpEngine is a Windows native host with a managed C# game layer. The editor
uses RmlUi 6.1, FreeType, and RML/RCSS documents for layout, text, controls,
and input. The executable owns Win32, CoreCLR, and the current OpenGL preview
backend. The existing Vulkan code remains available for later renderer work;
it does not render the current editor viewport.

## Build

The native solution builds the RmlUi/FreeType dependencies and the managed
projects as part of the x64 build. It requires Visual Studio C++, CMake, and
.NET 8. Dependency source versions and licenses are preserved under External.

    & 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' SharpEngine.sln /m /p:Configuration=Debug /p:Platform=x64

The managed output is copied to x64\Debug\Managed. The project targets
net8.0; a compatible .NET 8 runtime must be installed on the machine.

## Run

    .\x64\Debug\Engine.exe

The host loads SharpEngine.Game.dll and the shared SharpEngine.Managed.dll.
RmlUi loads `Content/UI/Rml/Editor.rml` and `Editor.rcss`. The C# editor session
loads `Content/Worlds/Main.world.xml` and the asset registry, then sends real
mesh triangles and lines to the viewport. Hierarchy selection and viewport
ray picking update the Inspector. Play rotates a demo mesh through C# state;
Pause freezes it, and Stop restores the edit transforms. Save writes the
project world, or `Saved/Worlds/Main.world.xml` in the default engine workspace,
so the public engine's sample asset remains read-only.

For a bounded run and an actual framebuffer capture:

```powershell
.\x64\Debug\Engine.exe --smoke-test --screenshot D:\SharpEngine\x64\Debug\editor-smoke.bmp
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

Only POD values and opaque native callbacks cross the boundary. Vulkan and C++
implementation types are intentionally kept out of the C# project. The
versioned native API carries UI text/actions, viewport bounds, and blittable
vertex arrays. Both the built-in entry point and new projects delegate editor
behavior to `SharpEngine.Editor.EditorRuntime` from the public managed DLL.

## Public engine and new projects

`Engine/engine.json` is the distribution manifest. A game project stores only
`Project/project.json`, whose `engine.path` points at that distribution; engine
source and binaries are not copied into the project. Set `SHARPENGINE_ROOT` to
override the manifest path when the same project is opened with a separately
installed public engine.

Create a project from the checked-out engine or an installed distribution:

```powershell
& .\Tools\SharpEngineProject.ps1 -Command New -Name MyGame -Directory ..\Samples
& .\Tools\SharpEngineProject.ps1 -Command Resolve -ProjectFile ..\Samples\MyGame\Project\project.json
& .\Tools\SharpEngineProject.ps1 -Command Doctor -ProjectFile ..\Samples\MyGame\Project\project.json
dotnet build ..\Samples\MyGame\Managed\mygame.csproj
```

The generated `Project/SharpEngine.props` exposes the public engine root to
MSBuild (`SharpEngineManagedRoot`, `SharpEngineContentRoot`, and
`SharpEngineToolsRoot`). The native host keeps the sample managed game when no
project is specified. Run a generated project assembly with:

```powershell
dotnet build ..\Samples\MyGame\Managed\mygame.csproj
.\x64\Debug\Engine.exe --project ..\Samples\MyGame\Project\project.json
```

The host reads the project manifest's `managed.runtimeConfig`,
`managed.assembly`, and `entryPoint.typeName` fields.

### Scene navigation, hierarchy, and mesh import

The Scene viewport supports RMB orbit, MMB pan, and mouse-wheel zoom. The navigation
camera is a normal serialized camera entity, so Save writes the new camera transform
to the project world. Entities can use `parent="id"`; local transforms resolve through
the full parent chain for rendering and ray picking. Cycles and missing parents are
rejected during world loading.

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
in red/green/blue, while the UI canvas draws a fixed 2D X-right/Y-down gizmo.
The viewport tabs switch
between `3D Scene` and `UI Canvas`; the latter renders a 2D canvas grid and
uses the UI coordinate origin (X right, Y down), with 3D camera navigation
disabled while it is active. The same state is exposed through the MCP actions
`editor.mode-scene` and `editor.mode-ui` and through `editor.describe`.

The editor menu hierarchy follows the usual Unreal-style split: `File` contains
New Project, Open Project, Save World, and scene export; `Edit` contains Engine
Settings and navigation preferences. The toolbar keeps only frequent Save,
Export, and Play/Pause/Stop controls. Move, Rotate, and Scale are scoped to the
3D viewport header and disappear when the editor switches to the UI Canvas.

### Embedded MCP

The public editor also exposes an engine-owned MCP stdio endpoint. Start it with:

```powershell
@'
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"sharpengine://scene"}}
'@ | .\x64\Debug\Engine.exe --mcp-stdio
```

The transport writes protocol responses only to stdout and diagnostics to stderr.
It exposes `scene.describe`, `assets.describe`, `editor.describe`, `viewport.input`,
`viewport.key`, `viewport.tick`, and `editor.action`, plus `sharpengine://scene`,
`sharpengine://assets`, and `sharpengine://editor` resources. The same JSON is produced by the live C# editor
runtime, so an AI client sees hierarchy, local and resolved transforms, cameras,
asset provenance, and importer metadata without controlling the desktop window.

### Asset manifests

The public engine owns `Content/Assets/assets.json`, referenced by the
`assets.manifest` entry in `Engine/engine.json`. A project gets its own
`Content/Assets/assets.json` from the BasicProject template and references it
from `Project/project.json`. The two manifests are intentionally separate:
engine records are read-only and use `engine://...` IDs, while project records
use `project://...` IDs. This lets the editor show both stores without
silently allowing a project to overwrite a public engine asset.

`SharpEngine.Assets.AssetRegistry.Load(engineRoot, projectRoot)` loads both
manifests. `Assets`, `EngineAssets`, `ProjectAssets`, `Find`, and `Search`
provide the query surface used by the Project dock and viewport. An entry is
portable because its `path` is relative to the owning engine/project root;
`AssetDescriptor.ResolvePath` validates that it cannot escape that root.

`Managed/Public/SharpEngine.Managed.csproj` builds the public .NET 8 API to
`x64/<Configuration>/Managed/SharpEngine.Managed.dll`. It contains the asset,
UI, world, and shared editor runtime APIs; the built-in game and generated
projects both reference this DLL. Generated `Project/SharpEngine.props`
resolves it from the public engine installation and copies the binary to the
project build output, with no copied engine sources.

Built-in meshes are real indexed triangle data:
`engine://meshes/cube` is a unit cube (8 positions, 12 triangles) and
`engine://meshes/plane` is a unit XZ plane (4 positions, 2 triangles).
`engine://materials/default` supplies the standard shader, base color,
metallic, and roughness parameters. Load them through
`AssetContent.LoadMesh(registry, id)` and
`AssetContent.LoadMaterial(registry, id)`; the formats are UTF-8 JSON files in
`Content/Assets/Meshes` and `Content/Assets/Materials`.

The native host accepts `--project <path-to-Project/project.json>`. It resolves
the public engine from `SHARPENGINE_ROOT` when set, otherwise from the
manifest-relative `engine.path`, then loads the project-owned managed DLL and
runtimeconfig. The project directory contains no copied engine sources.

## C# editor commands, menus, Toolbar, and context menus

Project-owned C# code can extend the shared editor without changing the native
host or writing project-specific RML. Implement `IEditorExtension` and register
it after the editor runtime is initialized:

```csharp
editor.Initialize();
editor.RegisterExtension(new ProjectEditorExtension());
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
through the context when executing: Stop replaces the simulated world, so
retaining entity instances across Play/Stop would address old objects.
Call `MarkDirty` after changing edit data; saving clears it. Runtime simulation
changes are discarded on Stop. Editing APIs do not yet provide Undo/Redo.

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
Separate extension DLL discovery, custom Dock/Inspector registration, shortcuts,
and undo transactions remain later work.

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
read-only state. Engine content is always read-only; project content remains
owned by the project filesystem and manifest. The browser state is also
available through `EditorRuntime.DescribeContentJson()`, the
`content.describe` MCP tool, and the `sharpengine://content` resource.

`Managed/UiFramework.cs`, `EngineUi.cs`, and the old `*.ui.xml` files are
legacy compatibility/experimentation APIs; they no longer lay out or render
the editor. Their semantic snapshots likewise describe that legacy tree.
The active UI source is `Content/UI/Rml/Editor.rml`.

World data describes stable entity IDs, asset URIs, editable Transform,
cameras, and directional lights. The viewport applies model/view/projection,
frustum clipping, lighting, and native depth testing. Mesh and material data
are resolved through the same asset registry as the Project panel. See
`Content/Worlds/README.md` for the format. Transform gizmos, free camera
navigation, and arbitrary asset importers are later work.

```powershell
dotnet run --project Tools/WorldValidation/SharpEngine.WorldValidation.csproj -- D:/SharpEngine
dotnet run --project Tools/EditorValidation/SharpEngine.EditorValidation.csproj -- D:/SharpEngine
```
