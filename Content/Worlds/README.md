# World description v2

`Main.sworld` is a data-only World asset. It is consumed by
`SharpEngine.World.WorldDocument` and is the engine's default World source.
Each entity has a stable `id`, display `name`, type (`mesh`, `camera`, `light`, or `empty`),
and a position, Euler rotation, and positive scale. Values are comma-separated numbers
using the invariant culture. Units are meters; the system is right-handed with Y up.
Rotation is degrees: pitch X, yaw Y, roll Z. A camera looks along its local -Z axis.

The `version="2"` attribute is required; legacy or missing versions are rejected.
The world's `activeCamera` chooses the camera used by the viewport. Cameras expose
`fieldOfView` (vertical degrees), `nearPlane`, and `farPlane`. Lights are directional;
their rotation controls local -Z light direction, `intensity` controls diffuse light,
and every light must declare `color`. Scene lights are the only source of mesh RGB:
there is no hidden preview or ambient light. Multiple lights accumulate linear RGB
before the current LDR viewport clamps the result.
Mesh assets use registered URIs such as `engine://meshes/cube` and
`engine://meshes/plane`.

Entities may set `parent="anotherEntityId"`. Position, rotation, and scale are
then local to that parent. The loader rejects missing parents, self-parenting, and
cycles. `WorldDocument.WorldMatrix(entity)` resolves the complete local-to-world
transform for rendering, picking, and AI inspection.
Camera and light direction inherit the full parent rotation chain while ignoring
parent scale; parent scale still affects their local position offset.

`WorldViewportRenderer.BuildFrame(world, bounds, selectedEntityId, assets)` produces triangle
and line lists. Every vertex is seven sequential floats: viewport-relative X/Y pixels,
0..1 perspective depth, then RGBA. Backends must scissor to the provided viewport and
enable depth testing. Rendering data is independent of the UI layout and native API.
The supplied RmlUi viewport bounds therefore determine the exact camera aspect ratio.
Passing the asset registry loads the actual mesh positions/indices and the referenced
material base color. Geometry is cached for that registry; `InvalidateAssets` clears
the cache after reimport. Missing or invalid resources are skipped with frame diagnostics.

Loading rejects duplicate entity IDs, malformed or non-finite transforms, non-positive
scale, unsupported versions/types, invalid camera ranges, and a missing active camera.
Triangles and lines are clipped to all six camera planes before perspective division.
`TryPick` converts the viewport pointer position to a 3D ray and selects the nearest
actual mesh triangle. It uses the same camera, asset geometry, and transforms as rendering.

The editor viewport supports W/A/S/D movement, Q/E vertical movement, Shift speed boost,
RMB orbit, MMB pan, and mouse-wheel zoom. Camera changes are regular world entity changes
and are persisted by Save. The 3D world axes are X right/red, Y up/green, and Z depth/blue;
the UI coordinate overlay uses X right and Y down. `WorldDocument.Describe()`
and `WorldDocument.ToJson()` expose the complete scene, including local transforms,
parent IDs, resolved world matrices, camera parameters, and mesh/material references.
Hierarchy reparent and ordering use `WorldAuthoringSession`, preserve world transform,
validate expected document revision, and participate in the shared Undo/Redo history.

Wavefront OBJ meshes can be imported into a project with:

```powershell
& .\Tools\SharpEngineMesh.ps1 -Command Import -ProjectRoot .\MyGame `
  -Input .\assets\character.obj -Name Character -Id meshes/character
```

The importer triangulates polygon faces into the same JSON Mesh asset consumed by the
viewport. Direct `SharpEngine.Obj` manifest entries are also supported for projects
that want to keep the source OBJ file.

Run the model and projection checks with:

```powershell
dotnet run --project Tools/WorldValidation/SharpEngine.WorldValidation.csproj -- D:/SharpEngine
```
