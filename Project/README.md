# SharpEngine project contract

Each game project has a `Project/project.json` manifest. Its `engine.path` is
relative to the manifest directory and points at a public SharpEngine
distribution. `SHARPENGINE_ROOT` overrides that path, which allows a project to
move between machines without copying the engine source or binaries into the
project repository.

Create and inspect projects with the engine tool:

```powershell
& .\Tools\SharpEngineProject.ps1 -Command New -Name MyGame -Directory ..\Samples
& .\Tools\SharpEngineProject.ps1 -Command Resolve -ProjectFile ..\Samples\MyGame\Project\project.json
& .\Tools\SharpEngineProject.ps1 -Command Doctor -ProjectFile ..\Samples\MyGame\Project\project.json
```

`Project/SharpEngine.props` exposes the resolved engine root to the generated
managed project. The current native host still uses its sample managed
assembly by default. To run a generated project assembly, pass its manifest:

```powershell
& "$env:SHARPENGINE_ROOT\x64\Debug\Engine.exe" --project .\Project\project.json
```

The host reads `managed.runtimeConfig`, `managed.assembly`, and
`entryPoint.typeName` from the manifest. Omitting `--project` keeps the
backward-compatible sample game path.

Projects also own `Content/Assets/assets.json`, referenced by the `assets`
section in `Project/project.json`. The editor combines that list with the
public engine asset manifest and keeps the IDs separate (`project://...` and
`engine://...`).
