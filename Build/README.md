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
outputs use `Editor` and `Tools`; project outputs use `Editor` and `Game`.
`Game` remains a build-only project Target until its native host is implemented.

Use `Tools/SharpEngineLayout.ps1` to inspect or maintain this structure:

```powershell
# Read-only; this is the default mode.
./Tools/SharpEngineLayout.ps1 -EngineRoot . -Mode Check
./Tools/SharpEngineLayout.ps1 -ProjectFile D:/Games/MyGame/MyGame.sharpproject -Mode Check

# Create one selected output combination and remove only documented legacy roots.
./Tools/SharpEngineLayout.ps1 -EngineRoot . -Mode Apply -Configuration Development

# Remove only the selected Target subtree from Binaries and Intermediate.
./Tools/SharpEngineLayout.ps1 -ProjectFile D:/Games/MyGame/MyGame.sharpproject `
  -Mode Clean -Target Game -Configuration Shipping
```

`Apply` does not migrate existing files. `Clean` does not remove other valid
platform, configuration, or target combinations. Both mutation modes reject
paths involving reparse points and verify every deletion remains strictly
inside the selected root.
