# Generic 3D viewport

`Viewport3D` composes a host surface, an independent `OrbitCameraController` and zero or
more `IViewport3DContent` layers. It has no World, AssetRegistry or Editor dependency.
`Camera3D`, `ViewportVertex` and `ViewportFrame` are lower-level rendering data.

```csharp
var Viewport = new Viewport3D("previewViewport", "preview");
Viewport.AddContent(Content);
Viewport.Frame(Positions);
Viewport.Render(Host);
```

Content receives only bounds and an immutable observation-camera snapshot. The host
supplies bounds and submits the generic frame. Callers own content lifetime; detaching
never saves or destroys it. Input actions use `ui.viewport3d:<elementId>:` with orbit,
pan, zoom and focus operations. Typed navigation APIs are also available through
`Viewport.Navigation`; string commands are the UI transport, not the only API.

Each control owns its camera. Sharing content never shares camera state. A World is
one possible content adapter supplied by the Editor layer; it is not required to create
or render the control. Invalid actions cannot advance state revisions.

`Tools/Viewport3DValidation` references only Runtime and verifies rendering and navigation
without a World or Editor. World-specific isolation is covered by WorldValidation
`--viewport-only`, and real host input by McpUiValidation's asset/timeline suites.
