# Engine projects and targets

Functional reuse and nesting follow the [composition architecture](CompositionArchitecture.md).
Functional boundaries are distinct from the Module/Plugin build and discovery boundaries below.

Each engine `.csproj` or `.vcxproj` declares one `SharpEngineRole`. Project references
define managed build dependencies. `EngineBuildGraph` discovers these declarations;
there is no second engine-project list in the solution generator or layout checker.

C# namespaces follow source responsibilities and directories. The core Runtime and
Editor projects use `SharpEngine` as their root namespace: `Animation/` contains
`SharpEngine.Animation`, `Input/` contains `SharpEngine.Input`, `Settings/` contains
`SharpEngine.Settings`, and `Modules/` contains `SharpEngine.Modules`. UI and component
runtime interfaces live alongside their implementations in `UI/` and `Components/`.
Editor-specific code uses `Editor/`, including `Editor/Hosting/` and `Editor/Layout/`;
World authoring stays in `World/` under `SharpEngine.World`. Build project and assembly
names describe build boundaries and do not determine source namespaces or placement.

## Content and Resources

`Content` is the authoritative asset root. Files represented by an engine or project
asset manifest receive stable `engine://` or `project://` IDs, can appear in Content
Manager, and participate in dependency discovery and Cook. `Resources` is for
implementation files loaded by a known subsystem, such as editor icons, fonts, and
plugin editor support files. Resource files do not become asset records merely because
they are present on disk.

Asset manifests use strict `schemaVersion: 2`. Each asset `path` is relative to its
owning engine, project or plugin **Content** directory, for example
`UI/UiReuseLab/Screen.sui` or `Assets/Meshes/Cube.mesh.json`. It never includes a
machine root or the owning `Content/` prefix. Stable IDs and plugin ownership
identify the mount; equal paths in separate mounts remain distinct assets.
`AssetRegistry.ResolvePath` resolves a physical path only for host file access;
`AssetDescriptor.ResolvePath(ContentRoot)` takes that owning Content directory.
Exported indexes (`ToJson`, `assets.describe`, `sharpengine://assets`) carry
`schemaVersion: 2`, omit `engineRoot`/`projectRoot`, expose logical plugin manifest
URIs, and redact physical paths in diagnostics and metadata. Source metadata is
preserved for authoring; it is not used as a runtime asset location.

Version 1 asset manifests are rejected. Explicit migration preserves every ID,
removes the leading `Content/` from each v1 asset path and sets schemaVersion to 2
(and `$schema` to `assets-v2.json` when present). Plugin paths are relative to the
plugin Content, never to `Plugins/<Name>/Content`. The engine, BasicProject,
NetworkDedicatedServer example and Sandbox manifests have been migrated.
Owner configuration fields such as `assets.manifest` and `world.startup` still
locate files relative to their owner root; they are not asset index entries.
Content Manager's `Plugins/<Name>/Content` is a virtual browsing location, not an
asset path or a runtime lookup key.

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
| SharpEngine.Animation.Editor | Editor Module | Skeletal-animation and state-machine asset editors; consumes Timeline UI and core Editor infrastructure |
| SharpEngine.Host | Host | Native-to-managed lifecycle entry points; compiled with Editor entry or Game entry according to Target |
| Engine | NativeHost | One native project; selects EditorMain or GameMain at build time and shares CLR bridge and scene presentation |
| SharpEngine.ComponentGenerator | Generator | Compile-time Roslyn analyzer; never a runtime assembly reference |
| SharpEngine.ProjectSelector | Tool | Association, project generation/migration, engine build orchestration and launch selection |

Engine feature code must belong to an explicit Engine Module project. A source folder
does not create a dependency or loading boundary: SDK-style projects compile all
source files beneath their project directory by default. Timeline is an Engine-owned
feature, so its Runtime and Editor projects live under `Source/` and are referenced by
the Engine Host; it does not need a Plugin descriptor.

`SharpEngine.Animation.Editor` is an Editor-only consumer of `SharpEngine.Timeline.Editor`.
Its `AnimationEditorModule` registers the skeletal-animation and state-machine asset
openers. Timeline Modules do not reference the animation editor Module. Built-in
Editor startup starts Timeline infrastructure before Animation Editor and releases
them in reverse order. Game/Server Host references exclude Animation Editor.

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
the Targets enabled by the project manifest. `Editor` and `Game` are enabled by
default; `Server` is opt-in. The generated solution keeps three roots: `Engine` contains engine
source and Plugins, `Project` contains project `Source`, project `Plugins`, and
`Config`, and `BuildTarget` contains the generated configuration-specific
`Editor`/`Game` build wrappers and any explicitly enabled additional Targets. In Windows Explorer,
right-click the project's `.sharpproject` and select `Add Server Target`
(`Show more options` first on Windows 11). This updates the manifest and
automatically regenerates the solution, Server wrapper, and launch profiles.
Reload the solution in Rider to see the new Target. The equivalent CLI is
`Tools/SharpEngineProject.ps1 -Command AddTarget -Target Server -ProjectFile <project>`
; use the `Remove Server Target` context-menu action or `RemoveTarget` to remove it.
A Plugin's Modules are nested below its own `Plugins/<Name>/Source`
folder. An Engine-only solution omits the empty `Project` root.
First Editor launch, project creation, and project-file generation share the
Project Selector's `ensure-registered` operation. It registers the Engine and file
actions for the current Windows user, reuses an existing installer association,
and allocates a per-root GUID otherwise. Later use repairs missing or outdated
shell entries and notifies Explorer only when changes occur. Compilation has no
registration side effects. Set `SHARPENGINE_REGISTER_ON_FIRST_USE=0` to disable
automatic setup in CI; explicit `register` / `ensure-registered` remain available.
Installed and extracted distributions must include the Project Selector executable
and its runtime dependencies in their Tools binaries. Editor startup and scripts
use that executable without a source build. Extraction itself runs no code; the
first Editor/project-tool launch performs setup. Registration failures are logged
without blocking Editor startup or project-file generation and retried on later use.
The `.sharpproject` context menu exposes the same
`Generate Project Files` operation and writes the generated solution beside the
project manifest.
Only the selected generated Target wrapper participates in Solution Build. It invokes
the same build pipeline as the CLI, which builds engine dependencies before user Modules.
The other projects remain visible for editing and design-time analysis.

Each generated launch Target binds one configuration and host, such as
`Debug Editor Target`. Its build command, bootstrap launcher, solution mapping,
and private `Properties/launchSettings.json` all use that same tuple. Selecting
the Debug Editor launch profile in Rider therefore builds Debug Editor even if
the active solution configuration is Development Editor. Solution Build still
builds only the Target matching the active solution configuration.
The generated `after.<Solution>.sln.targets` beside the solution forwards explicit
Rider project builds to their fixed Target even when it is unchecked in the
active solution configuration. Keep this file with the generated solution.
Regenerate and reload old solutions, then choose the new configuration-specific
launch Target once; old shared Target projects and profiles are removed.
Close the running target before rebuilding its executable.

The optional project `renderBackends` property selects `OpenGL` or `Both` at
build time. New templates choose OpenGL; omitted values preserve Both.
Regenerate the solution after changing it. Generated Targets forward
`SharpEngineRenderBackends` to the unified build. See
[RenderBackends](RenderBackends.md) for SDK requirements and shared-output behavior.

`SharpEngine.sln`, user solutions, `after.*.sln.targets`, Target wrappers, launch profiles and Generated.props
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

Engine source builds accept only `Win64`, the `Debug`/`Development`/`Shipping`
configurations, and the `Editor`/`Game`/`Server`/`Tools` output targets. The build
properties are project-local authority: command-line attempts to redirect binaries,
intermediates, MSBuild extensions, or shader output fail with `SEOUTPUT001` through
`SEOUTPUT014` before a noncanonical directory is created. Validation artifacts belong
under `Saved/Validation`; `Intermediate/RenderCache` is the one explicitly named,
shared derived-cache root outside the target tuple.

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
