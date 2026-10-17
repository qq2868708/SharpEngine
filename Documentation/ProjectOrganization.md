# Engine projects and targets

Each engine `.csproj` or `.vcxproj` declares one `SharpEngineRole`. Project references
define managed build dependencies. `EngineBuildGraph` discovers these declarations;
there is no second engine-project list in the solution generator or layout checker.

| Project | Role | Owns |
| --- | --- | --- |
| SharpEngine.Runtime | Runtime | Module contracts, components, RuntimeWorld, assets, input, runtime UI contracts, settings storage, playables, animation and timeline execution, scene rendering |
| SharpEngine.Editor | Editor | Authoring transactions, Inspector, Dock, asset browsing, editor UI, preview overlays, project code watching and reload |
| SharpEngine.Host | Host | Native-to-managed lifecycle entry points; compiled with Editor entry or Game entry according to Target |
| Engine | NativeHost | One native project; selects EditorMain or GameMain at build time and shares CLR bridge and scene presentation |
| SharpEngine.ComponentGenerator | Generator | Compile-time Roslyn analyzer; never a runtime assembly reference |
| SharpEngine.ProjectSelector | Tool | Association, project generation/migration, engine build orchestration and launch selection |

User Modules still have one project, one assembly and one `ISharpEngineModule` entry.
Runtime Modules reference `SharpEngine.Runtime`; Editor Modules additionally reference
`SharpEngine.Editor`. Runtime never references Editor. Host references Editor only for
Editor Target. Game Target compiles neither RmlUi editor-shell code nor Editor entry code.

## Build and generated files

Run `Tools/SharpEngineBuild.ps1 -Command Generate` to regenerate the engine solution.
Use `Tools/SharpEngineProject.ps1 -Command GenerateProjectFiles -ProjectFile <project>`
for a user solution. Solutions expose Debug/Development/Shipping × Editor/Game.
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
Packaging/Cook distribution and a full game UI renderer are separate work.

## Existing project migration

`SharpEngineProject.ps1 -Command Migrate -ProjectFile <project>` explicitly updates old
Modules/Managed assembly references, API properties and module namespaces, saving original
source files under `Saved/Migrations/RuntimeEditorSplit`, then regenerates IDE files.
Normal project-file generation never rewrites authored source files. Asset IDs and data
schema contracts are unaffected. Existing projects must set `world.startup` before Game
launch; new projects copy the configured engine World preset into their own Content and
record it explicitly in the project manifest.
