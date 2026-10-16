# SharpEngine project contract

A project is rooted by one versioned `<Name>.sharpproject` file. The manifest
directory is the project root; no `Project/` wrapper directory is used.

```text
MyGame/
  MyGame.sharpproject
  Source/MyGame/MyGame.csproj
  Source/MyGame.Editor/MyGame.Editor.csproj
  Content/
  Binaries/Managed/
  Intermediate/Managed/
  Saved/
```

The root manifest lists Module `.csproj` files and the public engine location.
Each Module must explicitly declare `SharpEngineModuleId` and
`SharpEngineModuleType` (`Runtime` or `Editor`). Module dependencies are the
evaluated MSBuild `ProjectReference` graph, never duplicated in JSON.

```powershell
& .\Tools\SharpEngineProject.ps1 -Command New -Name MyGame -Directory ..\Samples
& .\Tools\SharpEngineProject.ps1 -Command Doctor -ProjectFile ..\Samples\MyGame\MyGame.sharpproject
& .\Tools\SharpEngineProject.ps1 -Command Build -ProjectFile ..\Samples\MyGame\MyGame.sharpproject -Target Editor -Configuration Debug -Platform Win64
& .\x64\Debug\Engine.exe --project ..\Samples\MyGame\MyGame.sharpproject
```

The unified builder validates the dependency graph, Runtime/Editor boundaries,
unique Module IDs, one `ISharpEngineModule` entry per Assembly, and project-wide
user type-name uniqueness. It then writes `Binaries/Managed/modules.runtime.json`.
Runtime and Cook consume that derived manifest, not `.csproj` files.
