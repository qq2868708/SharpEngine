using System.Numerics;
using System.Runtime.InteropServices;
using SharpEngine.Assets;
using SharpEngine.UI;
using SharpEngine.World;

var sourceRoot = Path.GetFullPath(args.Length > 0 ? args[0] :
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var world = WorldDocument.Load(Path.Combine(sourceRoot, "Content", "Worlds", "Main.sworld"));
var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++;
}
void Reject(string xml, string label)
{
    try { WorldDocument.Parse(xml); }
    catch (InvalidDataException) { assertions++; return; }
    throw new InvalidOperationException($"Expected rejection: {label}");
}

Check(world.Entities.Count == 5 && world.MeshCount == 3, "Sample entity counts");
Check(world.ActiveCamera?.Id == "mainCamera", "Scene active camera");
world.Entities[1].Position = new WorldVector3(0.12345678f, 0.75f, 0);
var roundTrip = WorldDocument.Parse(world.ToXml().ToString());
Check(roundTrip.Entities[1].Position == world.Entities[1].Position,
    "Transform round trip must preserve float precision");
Check(roundTrip.ActiveCameraId == world.ActiveCameraId &&
    roundTrip.ActiveCamera?.FieldOfView == world.ActiveCamera?.FieldOfView &&
    roundTrip.ActiveCamera?.NearPlane == world.ActiveCamera?.NearPlane &&
    roundTrip.ActiveCamera?.FarPlane == world.ActiveCamera?.FarPlane,
    "Camera round trip");
Check(roundTrip.Entities[1].Asset == world.Entities[1].Asset,
    "Asset reference round trip");
var hierarchy = WorldDocument.Parse("""
  <world version="2" name="Hierarchy" activeCamera="cam">
    <entity id="root" type="empty" position="10,0,0" />
    <entity id="child" type="mesh" parent="root" asset="engine://meshes/cube" position="1,2,3" />
    <entity id="cam" type="camera" position="0,0,8" />
  </world>
  """);
Check(hierarchy.Entities.First(e => e.Id == "child").ParentId == "root",
    "Parent id must round trip into the world model");
Check(Math.Abs(hierarchy.WorldMatrix(hierarchy.Entities.First(e => e.Id == "child")).M41 - 11) < 0.001f,
    "Child world transform must include parent translation");
var hierarchyRoundTrip = WorldDocument.Parse(hierarchy.ToXml().ToString());
Check(hierarchyRoundTrip.ToJson().Contains("worldMatrix", StringComparison.Ordinal),
    "AI scene description must include resolved world matrices");
Reject("<world version='2'><entity id='root'/><entity id='child' parent='missing'/></world>", "missing parent");
Reject("<world version='2'><entity id='a' parent='b'/><entity id='b' parent='a'/></world>", "parent cycle");

var objPath = Path.Combine(Path.GetTempPath(), $"SharpEngine-{Guid.NewGuid():N}.obj");
var importRoot = Directory.CreateTempSubdirectory("SharpEngine-MeshValidation-");
try
{
    File.WriteAllText(objPath, "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1 2 3 4\n");
    var imported = WavefrontObjImporter.Load(objPath, "Imported Quad");
    Check(imported.Positions.Count == 4 && imported.Indices.Count == 6,
        "OBJ importer must triangulate polygon faces");
    var importedAssets = Directory.CreateDirectory(Path.Combine(importRoot.FullName, "Content", "Assets"));
    File.Copy(objPath, Path.Combine(importedAssets.FullName, "quad.obj"));
    File.WriteAllText(Path.Combine(importedAssets.FullName, "assets.json"), """
      {"schemaVersion":1,"assets":[{"id":"meshes/quad","type":"Mesh","name":"Imported Quad","path":"Content/Assets/quad.obj","importer":"SharpEngine.Obj"}]}
      """);
    var importedManifest = Path.Combine(importRoot.FullName, "Imported.sharpproject");
    File.WriteAllText(importedManifest, """
      {"schemaVersion":3,"projectId":"imported","engine":{"id":"SharpEngine","version":"0.2.0-dev","association":"validation","manifest":"engine.json"},"modules":[],"assets":{"manifest":"Content/Assets/assets.json"}}
      """);
    var importedRegistry = AssetRegistry.LoadFromManifests(
        Path.Combine(sourceRoot, "engine.json"), importedManifest);
    var importedWorld = WorldDocument.Parse("""
      <world version="2" activeCamera="cam">
        <entity id="quad" type="mesh" asset="project://meshes/quad" position="0,0,-2" />
        <entity id="cam" type="camera" position="0,0,0" />
      </world>
      """);
    var importedFrame = WorldViewportRenderer.BuildFrame(importedWorld,
        new UiRect(0, 0, 800, 500), assets: importedRegistry);
    Check(importedFrame.TriangleCount == 2 && importedFrame.Diagnostics.Count == 0,
        "Registered OBJ mesh must render through the normal asset and viewport path");
}
finally { File.Delete(objPath); importRoot.Delete(true); }

Reject("<world><entity id='a'/></world>", "missing schema version");
Reject("<world version='1'><entity id='a'/></world>", "legacy schema version");
Reject("<world version='2'><entity id='same'/><entity id='same'/></world>", "duplicate ID");
Reject("<world version='2'><entity id='a' position='NaN,0,0'/></world>", "NaN transform");
Reject("<world version='2'><entity id='a' rotation='Infinity,0,0'/></world>", "infinite transform");
Reject("<world version='2'><entity id='a' scale='1,,2,3'/></world>", "malformed vector");
Reject("<world version='2'><entity id='a' scale='1,0,1'/></world>", "zero scale");
Reject("<world version='2'><entity id='a' type='camera' fieldOfView='180'/></world>", "invalid FOV");
Reject("<world version='2'><entity id='a' type='camera' nearPlane='5' farPlane='1'/></world>", "invalid clip range");
Reject("<world version='2'><entity id='a' type='light'/></world>", "light without color");
Reject("<world version='2' activeCamera='missing'/>", "missing active camera");
var rejectedRuntimeTransform = false;
try { world.Entities[0].Rotation = new WorldVector3(float.NaN, 0, 0); }
catch (ArgumentOutOfRangeException) { rejectedRuntimeTransform = true; }
Check(rejectedRuntimeTransform, "Runtime transform mutation must reject NaN");

var bounds = new UiRect(300, 100, 800, 500);
var registry = AssetRegistry.Load(sourceRoot);
Check(registry.DefaultWorldId == "engine://worlds/main", "Engine default World must resolve by stable asset id");
var frame = WorldViewportRenderer.BuildFrame(world, bounds, "player", registry);
Check(frame.TriangleCount >= 24 && frame.LineCount > 0,
    "3D scene should produce cube/plane triangles and grid lines");
Check(frame.Diagnostics.Count == 0, "Registered sample geometry and material should load");
Check(frame.Lines.Count(v => v.X > frame.Bounds.Width - 130 && v.Y < 130) >= 18,
    "3D scene should submit a fixed top-right orientation gizmo");
var transformHandles = WorldViewportRenderer.GetTransformGizmoHandles(world, bounds, "player");
Check(transformHandles.Count >= 2, "Selected scene object should expose projected transform gizmo axes");
var moveDefinition = WorldViewportRenderer.GetTransformGizmoDefinition("move");
var rotateDefinition = WorldViewportRenderer.GetTransformGizmoDefinition("rotate");
var scaleDefinition = WorldViewportRenderer.GetTransformGizmoDefinition("scale");
Check(moveDefinition.HandleKind == TransformGizmoHandleKind.AxisArrow &&
    rotateDefinition.HandleKind == TransformGizmoHandleKind.RotationRing &&
    scaleDefinition.HandleKind == TransformGizmoHandleKind.ScaleBox,
    "Transform tools should expose distinct shared visual definitions");
Check(rotateDefinition.PixelLength > moveDefinition.PixelLength &&
    scaleDefinition.PickTolerance >= moveDefinition.PickTolerance,
    "Transform definitions should carry mode-specific sizing and hit tolerance");
Check(WorldViewportRenderer.GetRotationDragDegrees(Vector2.UnitX,
        new Vector2(0, -20)) > 0 &&
    WorldViewportRenderer.GetRotationDragDegrees(Vector2.UnitX,
        new Vector2(0, 20)) < 0,
    "Rotation drag should be positive counter-clockwise in screen space");
var handleProbe = transformHandles[0];
var handlePoint = (handleProbe.Pivot + handleProbe.End) * 0.5f;
Check(WorldViewportRenderer.TryPickTransformGizmo(world, bounds, "player",
    bounds.X + handlePoint.X, bounds.Y + handlePoint.Y, out var pickedAxis) &&
    pickedAxis.Axis == handleProbe.Axis,
    "Transform gizmo axis should be pickable in viewport coordinates");
var scaleHandles = WorldViewportRenderer.GetTransformGizmoHandles(world, bounds, "player", "scale");
var scaleProbe = scaleHandles[0];
Check(WorldViewportRenderer.TryPickTransformGizmo(world, bounds, "player",
    bounds.X + scaleProbe.End.X, bounds.Y + scaleProbe.End.Y, out var pickedScale,
    "scale") && pickedScale.Kind == TransformGizmoHandleKind.ScaleBox,
    "Scale gizmo box should be pickable as a scale handle");
var rotateFrame = WorldViewportRenderer.BuildFrame(world, bounds, "player", registry, "rotate");
var scaleFrame = WorldViewportRenderer.BuildFrame(world, bounds, "player", registry, "scale");
Check(rotateFrame.LineCount > frame.LineCount,
    "Rotate gizmo should render projected rings instead of move arrows");
Check(scaleFrame.LineCount > frame.LineCount,
    "Scale gizmo should render axis end boxes in addition to axes");
var uiGizmo = WorldViewportRenderer.BuildUiFrame(new UiRect(0, 0, 640, 360));
Check(uiGizmo.Triangles.Length == 0 &&
    uiGizmo.Lines.Any(v => v.X > uiGizmo.Bounds.Width - 130 && v.Y < 130),
    "UI canvas should submit a fixed top-right 2D orientation gizmo");
Check(Marshal.SizeOf<WorldViewportVertex>() == 7 * sizeof(float), "Native vertex ABI");
void CheckFinite(WorldViewportFrame f)
{
    Check(f.Triangles.Concat(f.Lines).All(v =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) &&
        v.X >= -0.001f && v.X <= f.Bounds.Width + 0.001f &&
        v.Y >= -0.001f && v.Y <= f.Bounds.Height + 0.001f && v.Z >= 0 && v.Z <= 1),
        "Projected/clipped vertices must stay finite and inside viewport/depth range");
}
CheckFinite(frame);
world.ActiveCamera!.Position = new WorldVector3(9, 7, 11);
var movedCamera = WorldViewportRenderer.BuildFrame(world, bounds, assets: registry);
Check(!movedCamera.Triangles.SequenceEqual(frame.Triangles), "Scene camera must drive projection");

var intersecting = WorldDocument.Parse("""
  <world version="2" activeCamera="cam">
    <entity id="cube" type="mesh" asset="engine://meshes/cube" position="0,0,-0.15" scale="0.3,0.3,0.3" />
    <entity id="cam" type="camera" fieldOfView="60" nearPlane="0.1" farPlane="100" />
  </world>
  """);
var clipped = WorldViewportRenderer.BuildFrame(intersecting, bounds, assets: registry);
Check(clipped.TriangleCount > 0, "Near plane intersection must retain visible triangles");
CheckFinite(clipped);
intersecting.Entities[0].Position = new WorldVector3(0, 0, 2);
Check(WorldViewportRenderer.BuildFrame(intersecting, bounds, assets: registry).TriangleCount == 0,
    "Geometry behind camera must be clipped");
intersecting.Entities[0].Position = new WorldVector3(0, 0, -2);
Check(WorldViewportRenderer.TryPick(intersecting, bounds, bounds.X + bounds.Width / 2,
    bounds.Y + bounds.Height / 2, out var picked, registry) && picked?.Id == "cube",
    "Viewport center ray should hit actual cube triangles");
Check(!WorldViewportRenderer.TryPick(intersecting, bounds, 0, 0, out _, registry),
    "Pointer outside viewport must not pick");
var missing = WorldDocument.Parse("""
  <world version="2" activeCamera="cam">
    <entity id="missing" type="mesh" asset="engine://meshes/missing" position="0,0,-2" />
    <entity id="cam" type="camera" />
  </world>
  """);
var missingFrame = WorldViewportRenderer.BuildFrame(missing, bounds, assets: registry);
Check(missingFrame.TriangleCount == 0 && missingFrame.Diagnostics.Count == 1,
    "Missing assets must be reported and skipped, without a silent cube fallback");

var lightingBounds = new UiRect(0, 0, 640, 480);
WorldDocument LightingWorld(string lights, string camera =
    "<entity id='cam' type='camera' position='0,0,3' />", string extra = "") =>
    WorldDocument.Parse($"""
      <world version="2" activeCamera="cam">
        {extra}
        <entity id="cube" type="mesh" asset="engine://meshes/cube" color="#FFFFFF" />
        {camera}
        {lights}
      </world>
      """);
static bool IsBlack(WorldViewportFrame value) => value.Triangles.All(vertex =>
    Math.Abs(vertex.R) < 0.0001f && Math.Abs(vertex.G) < 0.0001f &&
    Math.Abs(vertex.B) < 0.0001f);

var noLightFrame = WorldViewportRenderer.BuildFrame(LightingWorld(string.Empty), lightingBounds);
Check(noLightFrame.TriangleCount == 12 && IsBlack(noLightFrame),
    "A World without lights must render mesh RGB as black");
var zeroLightFrame = WorldViewportRenderer.BuildFrame(LightingWorld(
    "<entity id='light' type='light' color='#FFFFFF' intensity='0' />"), lightingBounds);
Check(IsBlack(zeroLightFrame), "Zero-intensity lights must not inject illumination");
var coloredLightFrame = WorldViewportRenderer.BuildFrame(LightingWorld("""
    <entity id='red' type='light' color='#FF0000' intensity='0.5' />
    <entity id='green' type='light' color='#00FF00' intensity='0.25' />
    """), lightingBounds);
Check(Math.Abs(coloredLightFrame.Triangles.Max(vertex => vertex.R) - 0.5f) < 0.0001f &&
    Math.Abs(coloredLightFrame.Triangles.Max(vertex => vertex.G) - 0.25f) < 0.0001f &&
    coloredLightFrame.Triangles.All(vertex => Math.Abs(vertex.B) < 0.0001f),
    "Directional lights must accumulate linear RGB color and intensity");
var clampedLightFrame = WorldViewportRenderer.BuildFrame(LightingWorld("""
    <entity id='a' type='light' color='#FFFFFF' intensity='2' />
    <entity id='b' type='light' color='#FFFFFF' intensity='3' />
    """), lightingBounds);
Check(Math.Abs(clampedLightFrame.Triangles.Max(vertex => vertex.R) - 1) < 0.0001f &&
    Math.Abs(clampedLightFrame.Triangles.Max(vertex => vertex.G) - 1) < 0.0001f &&
    Math.Abs(clampedLightFrame.Triangles.Max(vertex => vertex.B) - 1) < 0.0001f,
    "The current LDR viewport must clamp accumulated light to one");
var parentLight = WorldViewportRenderer.BuildFrame(LightingWorld(
    "<entity id='light' parent='lightParent' type='light' color='#FFFFFF' />",
    extra: "<entity id='lightParent' rotation='0,180,0' />"), lightingBounds);
Check(!parentLight.Triangles.SequenceEqual(WorldViewportRenderer.BuildFrame(LightingWorld(
        "<entity id='light' type='light' color='#FFFFFF' />"), lightingBounds).Triangles),
    "Directional light direction must inherit parent rotation");
var scaledParentLight = WorldViewportRenderer.BuildFrame(LightingWorld(
    "<entity id='light' parent='lightParent' type='light' color='#FFFFFF' />",
    extra: "<entity id='lightParent' scale='7,2,0.5' />"), lightingBounds);
var rootLight = WorldViewportRenderer.BuildFrame(LightingWorld(
    "<entity id='light' type='light' color='#FFFFFF' />"), lightingBounds);
Check(scaledParentLight.Triangles.SequenceEqual(rootLight.Triangles),
    "Parent scale must not alter directional light orientation");
var parentCamera = WorldViewportRenderer.BuildFrame(LightingWorld(string.Empty,
    "<entity id='cam' parent='cameraParent' type='camera' position='1,0,0' />",
    "<entity id='cameraParent' scale='2,3,4' />"), lightingBounds);
var rootCamera = WorldViewportRenderer.BuildFrame(LightingWorld(string.Empty,
    "<entity id='cam' type='camera' position='2,0,0' />"), lightingBounds);
Check(parentCamera.Triangles.SequenceEqual(rootCamera.Triangles),
    "Parent scale may move a camera offset but must not alter camera orientation");

Console.WriteLine($"[WorldValidation] PASS: {assertions} assertions; " +
    $"sample={frame.TriangleCount} triangles/{frame.LineCount} lines, " +
    $"near-clipped={clipped.TriangleCount} triangles; vertex ABI=28 bytes.");
