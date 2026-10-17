# Engine projects and targets

Each engine `.csproj` or `.vcxproj` declares one `SharpEngineRole`. Project references
define managed build dependencies. `EngineBuildGraph` discovers these declarations;
there is no second engine-project list in the solution generator or layout checker.

## Content and Resources

`Content` is the authoritative asset root. Files represented by an engine or project
asset manifest receive stable `engine://` or `project://` IDs, can appear in Content
Manager, and participate in dependency discovery and Cook. `Resources` is for
implementation files loaded by a known subsystem, such as editor icons, fonts, and
plugin editor support files. Resource files do not become asset records merely because
they are present on disk.

The engine resource URI is `sharpengine-resource://engine/<relative-path>` or
`sharpengine-resource://project/<relative-path>`. The relative path must be under the
corresponding `Resources/` directory or under a plugin's `Plugins/<Name>/Resources/`
directory. Built-in toolbar icon names are shorthand for
`sharpengine-resource://engine/Resources/Editor/Icons/<name>.tga`.

The intended layout is:

```text
EngineRoot/
  Content/                  # registered engine assets and UI source documents
  Resources/                # engine implementation resources
Plugins/<Name>/
  Content/                  # plugin assets (when registered by a manifest)
  Resources/                # plugin implementation/editor resources
ProjectRoot/
  Content/                  # registered project assets
  Resources/                # project implementation resources
```

Plugin manifests declare Modules and may declare an `assets.manifest` under the
plugin's `Content/` directory. A declared plugin manifest is loaded into the
`AssetRegistry` with a stable `engine://plugins/<plugin-id>/...` or
`project://plugins/<plugin-id>/...` namespace and is browsable below
`Plugins/<Name>/Content`. Files merely placed under `Plugins/` remain invisible.
The full Cook pipeline still owns packaging and derived output publication.

| Project | Role | Owns |
| --- | --- | --- |
| SharpEngine.Runtime | Runtime Module | Core module contracts, components, RuntimeWorld, assets, input, runtime UI contracts, settings storage and scene rendering |
| SharpEngine.Editor | Editor Module | Core authoring transactions, Inspector, Dock, asset browsing, editor UI, preview overlays, project code watching and reload |
| SharpEngine.Timeline.Runtime | Runtime Module | Built-in Timeline document, Playable composition and Timeline asset loading |
| SharpEngine.Timeline.Editor | Editor Module | Built-in Timeline semantic view and editor-facing Timeline presentation |
| SharpEngine.Host | Host | Native-to-managed lifecycle entry points; compiled with Editor entry or Game entry according to Target |
| Engine | NativeHost | One native project; selects EditorMain or GameMain at build time and shares CLR bridge and scene presentation |
| SharpEngine.ComponentGenerator | Generator | Compile-time Roslyn analyzer; never a runtime assembly reference |
| SharpEngine.ProjectSelector | Tool | Association, project generation/migration, engine build orchestration and launch selection |

Engine feature code must belong to an explicit Engine Module project. A source folder
does not create a dependency or loading boundary: SDK-style projects compile all
source files beneath their project directory by default. Timeline is an Engine-owned
feature, so its Runtime and Editor projects live under `Source/` and are referenced by
the Engine Host; it does not need a Plugin descriptor.

Engine feature Modules carry a `SharpEngineModuleAttribute` in their assembly. The
Host supplies the statically linked Engine assemblies to `EngineModuleHost`, which
validates the entry and starts Runtime Modules for both targets and Editor Modules
only for Editor. They shut down in reverse startup order with the Host. This is a
different lifetime from project Modules: project assemblies come from
`modules.runtime.json` and can be replaced by the Editor's project-code reload;
Engine feature assemblies stay fixed until the process exits. `SharpEngine.Runtime`
and `SharpEngine.Editor` are core API assemblies loaded by the Host and do not need
an `ISharpEngineModule` entry of their own.

User Modules still have one project, one assembly and one `ISharpEngineModule` entry.
Runtime Modules reference `SharpEngine.Runtime`; Editor Modules additionally reference
`SharpEngine.Editor`. Runtime never references Editor. Host references Editor only for
Editor Target. Game Target compiles neither RmlUi editor-shell code nor Editor entry code.

## Plugins

A Plugin is a distribution and discovery container; it is not a second executable code
boundary. A `.sharpplugin` descriptor declares a stable `id`, `version`, and an explicit
list of Module `.csproj` paths. Each listed project remains an ordinary Module with the
same `ISharpEngineModule` entry, `Startup`/`Shutdown` lifecycle, Runtime/Editor target
rules, and dependency validation. The Plugin itself has no lifecycle callback.

Projects enable Plugins explicitly in their `.sharpproject` manifest:

```json
{
  "plugins": [
    "project://Plugins/Example/Example.sharpplugin",
    "engine://Plugins/Diagnostics/Diagnostics.sharpplugin"
  ]
}
```

Only `ProjectRoot/Plugins` and `EngineRoot/Plugins` are valid roots. A Plugin descriptor
is required for an installable/enabled Plugin because it gives the builder a deterministic
set of Modules, identity, and a hashable input. It is not required for ordinary project
Modules, which remain listed directly in `.sharpproject.modules`. After discovery, project
and Plugin Modules are merged into one dependency graph and one runtime manifest.

## Build and generated files

Run `Tools/GenerateProjectFiles.ps1 -EngineRoot <engine>` to regenerate the Engine
solution. Use `Tools/SharpEngineProject.ps1 -Command GenerateProjectFiles
-ProjectFile <project>` for a user solution, or pass `-ProjectFile` to the same
`GenerateProjectFiles.ps1` wrapper. Solutions expose Debug/Development/Shipping ×
Editor/Game. The generated solution keeps three roots: `Engine` contains engine
source and Plugins, `Project` contains project `Source`, project `Plugins`, and
`Config`, and `BuildTarget` contains the generated `Editor`/`Game` build
wrappers. A Plugin's Modules are nested below its own `Plugins/<Name>/Source`
folder. An Engine-only solution omits the empty `Project` root.
After registering the Engine, the `.sharpproject` context menu exposes the same
`Generate Project Files` operation and writes the generated solution beside the
project manifest.
Only the selected generated Target wrapper participates in Solution Build. It invokes
the same build pipeline as the CLI, which builds engine dependencies before user Modules.
The other projects remain visible for editing and design-time analysis.

`SharpEngine.sln`, user solutions, Target wrappers, launch profiles and Generated.props
are derived files. Do not add project entries or dependency lists by hand. Generated
project cache stamps include the discovered engine project definitions.

```
./Tools/SharpEngineBuild.ps1 -Target Editor -Configuration Development
./Tools/SharpEngineBuild.ps1 -Target Game -Configuration Development
./Tools/SharpEngineProject.ps1 -Command Build -ProjectFile <project> -Target Game -Configuration Development
```

Artifacts remain under `Binaries/<Platform>/<Configuration>/<Target>`; intermediates
are isolated by the same tuple. Runtime module publication validates the graph and
publishes only Runtime Modules for Game, and Runtime plus Editor Modules for Editor.
Game does not need to compile Editor Modules. Tools and validation programs are not
project Modules and do not require `ISharpEngineModule` entry classes.

## Launch contract

Editor starts `Binaries/Win64/<Configuration>/Editor/Engine.exe`. It can run without a
project; a project with no startup World receives an unsaved default authoring World.
Play constructs `RuntimeWorld` from committed Authoring state; Stop discards it.

Game starts `Binaries/Win64/<Configuration>/Game/Engine.exe --project <project>`.
It requires a project and a valid saved `world.startup` within that project. It loads
Runtime Modules, registers their components/settings, starts the World, runs the normal
runtime lifecycle and input routing, and renders through the shared scene renderer.
It does not start editor panels, authoring tools, code watchers or editor MCP.
Missing project/world or invalid startup data causes an explicit diagnostic and a
nonzero exit. `--frames N --hidden --screenshot <bmp>` supports bounded acceptance runs.
The delivery includes the complete Game presentation path: Vulkan scene rendering
and the visible RmlUi game UI share the same swapchain frame. Packaging/Cook
distribution remains separate work.

## Existing project migration

`SharpEngineProject.ps1 -Command Migrate -ProjectFile <project>` explicitly updates old
Modules/Managed assembly references, API properties and module namespaces, saving original
source files under `Saved/Migrations/RuntimeEditorSplit`, then regenerates IDE files.
Normal project-file generation never rewrites authored source files. Asset IDs and data
schema contracts are unaffected. Existing projects must set `world.startup` before Game
launch; new projects copy the configured engine World preset into their own Content and
record it explicitly in the project manifest.
