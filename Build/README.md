# SharpEngine Build Layout

`SharpEngine.Build.props` is the engine build-time authority for output paths.
Engine Build Modules import it through `Source/Directory.Build.props`. The
project template carries the same property contract because a project may live
outside the Engine root; `SharpEngineLayout.ps1 -Mode Check` guards both sides.

The supported directory shape is:

```text
Binaries/<Platform>/<Configuration>/<Target>/
Intermediate/<Platform>/<Configuration>/<Target>/
```

Supported values are `Win64` and `Debug` / `Development` / `Shipping`. Engine
outputs use `Editor`, `Game`, and `Tools`; project outputs use `Editor` and `Game`.
Game has a standalone native/managed entry and does not depend on Editor. Projects
declare `SharpEngineRole` before SDK props. Tool and Generator roles use the Tools
output directory; Runtime and Host follow the selected Target. `EngineBuildGraph`
discovers engine projects and invokes their real project-reference graph.

Use `Tools/SharpEngineLayout.ps1` to inspect or maintain this structure:

```powershell
# Read-only full-workspace audit; this is the default mode and scope.
./Tools/SharpEngineLayout.ps1 -EngineRoot . -Mode Check
./Tools/SharpEngineLayout.ps1 -ProjectFile D:/Games/MyGame/MyGame.sharpproject -Mode Check

# Read-only build preflight for one output combination.
./Tools/SharpEngineLayout.ps1 -EngineRoot . -Mode Check -Scope Target `
  -Platform Win64 -Configuration Debug -Target Editor

# Create one selected output combination and remove only documented legacy roots.
./Tools/SharpEngineLayout.ps1 -EngineRoot . -Mode Apply -Configuration Development

# Remove only the selected Target subtree from Binaries and Intermediate.
./Tools/SharpEngineLayout.ps1 -ProjectFile D:/Games/MyGame/MyGame.sharpproject `
  -Mode Clean -Target Game -Configuration Shipping
```

Native and project builds use `-Scope Target`: they validate the source contract
and the selected output paths, including their ancestors and reparse-point checks.
Unrelated validation artifacts and other output combinations do not block these
builds. The default `-Scope All` still rejects unknown output entries and legacy
directories across the workspace. Store temporary validation artifacts under
`Saved/Validation` so they also remain outside the full layout audit.

Run `./Tools/LayoutValidation.ps1` for the layout-check regression suite.

`Apply` does not migrate existing files. `Clean` does not remove other valid
platform, configuration, or target combinations. Both mutation modes reject
paths involving reparse points and verify every deletion remains strictly
inside the selected root.
