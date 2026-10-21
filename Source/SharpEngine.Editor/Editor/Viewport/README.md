# World rendering adapter

The generic control lives in `SharpEngine.Runtime/UI/Viewport3D`, not in this folder.
This layer contains only `WorldViewportContent`, which adapts a caller-owned World to
`IViewport3DContent`. It passes the independent observation camera to the existing
renderer and never assigns `ActiveCameraId` or edits Camera/Transform components.

`Editor/Preview/WorldPreview.cs` demonstrates the entire composition:

```csharp
var Preview = new WorldPreview(World, Assets, "previewViewport", "preview");
Preview.Viewport.Frame(Positions);
Preview.Viewport.Render(Host);
```

Mesh/Skeleton/Timeline/Prefab editors add their content preparation and optional
skeleton overlay. Material preview directly supplies generic content and needs no World.
The main scene viewport retains its authoring/runtime camera transactions and Undo.

See `Documentation/EngineArchitecture.md` for the pyramid contract and maintenance map.
