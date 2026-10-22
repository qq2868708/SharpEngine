using SharpEngine.Rendering;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpEngine.Assets;
using SharpEngine.UI;
using SharpEngine.World;

var sourceRoot = Path.GetFullPath(args.Length > 0 ? args[0] :
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var world = WorldDocument.Load(Path.Combine(sourceRoot, "Content", "Worlds", "Main.sworld"));
var assertions = 0;
WorldViewportNavigationValidation.Run(world, Check);
if (args.Contains("--viewport-only", StringComparer.Ordinal))
{
    Console.WriteLine($"PASS: {assertions} shared viewport navigation assertions");
    return;
}
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++;
}
void Reject(string xml, string label)
{
    try { WorldDocument.Parse(xml); }
    catch (Exception error) when (error is InvalidDataException or SharpEngine.Components.ComponentValidationException) { assertions++; return; }
    throw new InvalidOperationException($"Expected rejection: {label}");
}

var ExpectedEntityIds = new HashSet<string>(StringComparer.Ordinal)
{
    "ground", "player", "crate", "mainCamera", "sun", "skyLight", "player-model", "player-view",
};
Check(ExpectedEntityIds.SetEquals(world.Entities.Select(Entity => Entity.Id)) && world.MeshCount == 3,
    "Sample entities include the character model and player camera, with three renderable meshes");
Check(world.ActiveCamera?.Id == "mainCamera", "Scene active camera");
world.Entities[1].Position = new WorldVector3(0.12345678f, 0.75f, 0);
var roundTrip = WorldDocument.Parse(world.Serialize());
Check(roundTrip.Entities[1].Position == world.Entities[1].Position,
    "Transform round trip must preserve float precision");
Check(roundTrip.ActiveCameraId == world.ActiveCameraId &&
    roundTrip.ActiveCamera?.FieldOfView == world.ActiveCamera?.FieldOfView &&
    roundTrip.ActiveCamera?.NearPlane == world.ActiveCamera?.NearPlane &&
    roundTrip.ActiveCamera?.FarPlane == world.ActiveCamera?.FarPlane,
    "Camera round trip");
Check(roundTrip.Entities[1].Asset == world.Entities[1].Asset,
    "Asset reference round trip");
var hierarchy = WorldDocument.Parse("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Hierarchy\",\"activeCameraId\":\"cam\",\"entities\":[{\"entityId\":\"root\",\"name\":\"root\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"root-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[10,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]},{\"entityId\":\"child\",\"name\":\"child\",\"parentId\":\"root\",\"activeSelf\":true,\"components\":[{\"componentId\":\"child-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[1,2,3],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"child-mesh-renderer\",\"typeId\":\"engine.mesh-renderer\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Mesh\":\"engine://meshes/cube\",\"Material\":null,\"Color\":[1,1,1,1]}}]},{\"entityId\":\"cam\",\"name\":\"cam\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"cam-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,8],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"cam-camera\",\"typeId\":\"engine.camera\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"FieldOfView\":60,\"NearPlane\":0.1,\"FarPlane\":1000}}]}]}");
Check(hierarchy.Entities.First(e => e.Id == "child").ParentId == "root",
    "Parent id must round trip into the world model");
Check(Math.Abs(hierarchy.WorldMatrix(hierarchy.Entities.First(e => e.Id == "child")).M41 - 11) < 0.001f,
    "Child world transform must include parent translation");
var hierarchyRoundTrip = WorldDocument.Parse(hierarchy.Serialize());
Check(hierarchyRoundTrip.ToJson().Contains("worldMatrix", StringComparison.Ordinal),
    "AI scene description must include resolved world matrices");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"root\",\"name\":\"root\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"root-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]},{\"entityId\":\"child\",\"name\":\"child\",\"parentId\":\"missing\",\"activeSelf\":true,\"components\":[{\"componentId\":\"child-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]}]}", "missing parent");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":\"b\",\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]},{\"entityId\":\"b\",\"name\":\"b\",\"parentId\":\"a\",\"activeSelf\":true,\"components\":[{\"componentId\":\"b-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]}]}", "parent cycle");

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
    var importedWorld = WorldDocument.Parse("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":\"cam\",\"entities\":[{\"entityId\":\"quad\",\"name\":\"quad\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"quad-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,-2],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"quad-mesh-renderer\",\"typeId\":\"engine.mesh-renderer\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Mesh\":\"project://meshes/quad\",\"Material\":null,\"Color\":[1,1,1,1]}}]},{\"entityId\":\"cam\",\"name\":\"cam\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"cam-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"cam-camera\",\"typeId\":\"engine.camera\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"FieldOfView\":60,\"NearPlane\":0.1,\"FarPlane\":1000}}]}]}");
    var importedFrame = WorldViewportRenderer.BuildFrame(importedWorld,
        new UiRect(0, 0, 800, 500), assets: importedRegistry);
    Check(importedFrame.TriangleCount == 2 && importedFrame.Diagnostics.Count == 0,
        "Registered OBJ mesh must render through the normal asset and viewport path");
}
finally { File.Delete(objPath); importRoot.Delete(true); }

Reject("{\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]}]}", "missing schema version");
Reject("{\"schemaVersion\":1,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]}]}", "legacy schema version");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"same\",\"name\":\"same\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"same-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]},{\"entityId\":\"same\",\"name\":\"same\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"same-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]}]}", "duplicate ID");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[\"NaN\",0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]}]}", "NaN transform");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[\"Infinity\",0,0],\"Scale\":[1,1,1]}}]}]}", "infinite transform");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,null,2,3]}}]}]}", "malformed vector");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,0,1]}}]}]}", "zero scale");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"a-camera\",\"typeId\":\"engine.camera\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"FieldOfView\":180,\"NearPlane\":0.1,\"FarPlane\":1000}}]}]}", "invalid FOV");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"a-camera\",\"typeId\":\"engine.camera\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"FieldOfView\":60,\"NearPlane\":5,\"FarPlane\":1}}]}]}", "invalid clip range");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"a-directional-light\",\"typeId\":\"engine.directional-light\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Intensity\":1}}]}]}", "light without color");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"a-sky-light\",\"typeId\":\"engine.sky-light\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Intensity\":1}}]}]}", "light without color");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":\"missing\",\"entities\":[]}", "missing active camera");
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
Check(Marshal.SizeOf<ViewportVertex>() == 7 * sizeof(float), "Native vertex ABI");
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

var intersecting = WorldDocument.Parse("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":\"cam\",\"entities\":[{\"entityId\":\"cube\",\"name\":\"cube\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"cube-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,-0.15],\"Rotation\":[0,0,0],\"Scale\":[0.3,0.3,0.3]}},{\"componentId\":\"cube-mesh-renderer\",\"typeId\":\"engine.mesh-renderer\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Mesh\":\"engine://meshes/cube\",\"Material\":null,\"Color\":[1,1,1,1]}}]},{\"entityId\":\"cam\",\"name\":\"cam\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"cam-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"cam-camera\",\"typeId\":\"engine.camera\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"FieldOfView\":60,\"NearPlane\":0.1,\"FarPlane\":100}}]}]}");
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
var missing = WorldDocument.Parse("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":\"cam\",\"entities\":[{\"entityId\":\"missing\",\"name\":\"missing\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"missing-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,-2],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"missing-mesh-renderer\",\"typeId\":\"engine.mesh-renderer\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Mesh\":\"engine://meshes/missing\",\"Material\":null,\"Color\":[1,1,1,1]}}]},{\"entityId\":\"cam\",\"name\":\"cam\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"cam-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"cam-camera\",\"typeId\":\"engine.camera\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"FieldOfView\":60,\"NearPlane\":0.1,\"FarPlane\":1000}}]}]}");
var missingFrame = WorldViewportRenderer.BuildFrame(missing, bounds, assets: registry);
Check(missingFrame.TriangleCount == 0 && missingFrame.Diagnostics.Count == 1,
    "Missing assets must be reported and skipped, without a silent cube fallback");

var lightingBounds = new UiRect(0, 0, 640, 480);
WorldDocument LightingWorld(string lights, string camera = "{\"entityId\":\"cam\",\"name\":\"cam\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"cam-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,3],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"cam-camera\",\"typeId\":\"engine.camera\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"FieldOfView\":60,\"NearPlane\":0.1,\"FarPlane\":1000}}]}", string extra = "") =>
    WorldDocument.Parse("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"lighting\",\"name\":\"Lighting\",\"activeCameraId\":\"cam\",\"entities\":[" +
        string.Join(",", new[] { extra, "{\"entityId\":\"cube\",\"name\":\"cube\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"cube-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"cube-mesh-renderer\",\"typeId\":\"engine.mesh-renderer\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Mesh\":\"engine://meshes/cube\",\"Material\":null,\"Color\":[1,1,1,1]}}]}", camera, lights }.Where(s => s.Length > 0)) + "]}");
static bool IsBlack(WorldViewportFrame value) => value.Triangles.All(vertex =>
    Math.Abs(vertex.R) < 0.0001f && Math.Abs(vertex.G) < 0.0001f &&
    Math.Abs(vertex.B) < 0.0001f);

var noLightFrame = WorldViewportRenderer.BuildFrame(LightingWorld(string.Empty), lightingBounds);
Check(noLightFrame.TriangleCount == 12 && IsBlack(noLightFrame),
    "A World without lights must render mesh RGB as black");
var zeroLightFrame = WorldViewportRenderer.BuildFrame(LightingWorld(
    "{\"entityId\":\"light\",\"name\":\"light\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"light-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"light-directional-light\",\"typeId\":\"engine.directional-light\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Intensity\":0,\"Color\":[1,1,1,1]}}]}"), lightingBounds);
Check(IsBlack(zeroLightFrame), "Zero-intensity lights must not inject illumination");
string SkyLight(string EntityId, string Color, string Intensity,
    bool Enabled = true, bool Active = true)
{
    return $"{{\"entityId\":\"{EntityId}\",\"name\":\"{EntityId}\",\"parentId\":null," +
        $"\"activeSelf\":{Active.ToString().ToLowerInvariant()},\"components\":[" +
        $"{{\"componentId\":\"{EntityId}-transform\",\"typeId\":\"engine.transform\"," +
        "\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0]," +
        "\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}," +
        $"{{\"componentId\":\"{EntityId}-sky-light\",\"typeId\":\"engine.sky-light\"," +
        $"\"schemaVersion\":1,\"enabled\":{Enabled.ToString().ToLowerInvariant()}," +
        $"\"state\":{{\"Color\":{Color},\"Intensity\":{Intensity}}}}}]}}";
}
var SkyLightFrame = WorldViewportRenderer.BuildFrame(LightingWorld(
    SkyLight("skyA", "[0.25,0.5,0.75,1]", "0.4") + "," +
    SkyLight("skyB", "[0.5,0,0.25,1]", "0.2")), lightingBounds);
Check(SkyLightFrame.Triangles.All(Vertex =>
        Math.Abs(Vertex.R - 0.2f) < 0.0001f &&
        Math.Abs(Vertex.G - 0.2f) < 0.0001f &&
        Math.Abs(Vertex.B - 0.35f) < 0.0001f),
    "Enabled Sky Lights must accumulate full-precision linear RGB and intensity");
var InactiveSkyLightFrame = WorldViewportRenderer.BuildFrame(LightingWorld(
    SkyLight("disabled", "[1,1,1,1]", "1", Enabled: false) + "," +
    SkyLight("inactive", "[1,1,1,1]", "1", Active: false) + "," +
    SkyLight("zero", "[1,1,1,1]", "0")), lightingBounds);
Check(IsBlack(InactiveSkyLightFrame),
    "Disabled, inactive, and zero-intensity Sky Lights must not illuminate");
var coloredLightFrame = WorldViewportRenderer.BuildFrame(LightingWorld("{\"entityId\":\"red\",\"name\":\"red\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"red-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"red-directional-light\",\"typeId\":\"engine.directional-light\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Intensity\":0.5,\"Color\":[1,0,0,1]}}]},{\"entityId\":\"green\",\"name\":\"green\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"green-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"green-directional-light\",\"typeId\":\"engine.directional-light\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Intensity\":0.25,\"Color\":[0,1,0,1]}}]}"), lightingBounds);
Check(Math.Abs(coloredLightFrame.Triangles.Max(vertex => vertex.R) - 0.5f) < 0.0001f &&
    Math.Abs(coloredLightFrame.Triangles.Max(vertex => vertex.G) - 0.25f) < 0.0001f &&
    coloredLightFrame.Triangles.All(vertex => Math.Abs(vertex.B) < 0.0001f),
    "Directional lights must accumulate linear RGB color and intensity");
var clampedLightFrame = WorldViewportRenderer.BuildFrame(LightingWorld("{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"a-directional-light\",\"typeId\":\"engine.directional-light\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Intensity\":2,\"Color\":[1,1,1,1]}}]},{\"entityId\":\"b\",\"name\":\"b\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"b-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"b-directional-light\",\"typeId\":\"engine.directional-light\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Intensity\":3,\"Color\":[1,1,1,1]}}]}"), lightingBounds);
Check(Math.Abs(clampedLightFrame.Triangles.Max(vertex => vertex.R) - 1) < 0.0001f &&
    Math.Abs(clampedLightFrame.Triangles.Max(vertex => vertex.G) - 1) < 0.0001f &&
    Math.Abs(clampedLightFrame.Triangles.Max(vertex => vertex.B) - 1) < 0.0001f,
    "The current LDR viewport must clamp accumulated light to one");
var parentLight = WorldViewportRenderer.BuildFrame(LightingWorld(
    "{\"entityId\":\"light\",\"name\":\"light\",\"parentId\":\"lightParent\",\"activeSelf\":true,\"components\":[{\"componentId\":\"light-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"light-directional-light\",\"typeId\":\"engine.directional-light\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Intensity\":1,\"Color\":[1,1,1,1]}}]}",
    extra: "{\"entityId\":\"lightParent\",\"name\":\"lightParent\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"lightParent-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,180,0],\"Scale\":[1,1,1]}}]}"), lightingBounds);
Check(!parentLight.Triangles.SequenceEqual(WorldViewportRenderer.BuildFrame(LightingWorld(
        "{\"entityId\":\"light\",\"name\":\"light\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"light-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"light-directional-light\",\"typeId\":\"engine.directional-light\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Intensity\":1,\"Color\":[1,1,1,1]}}]}"), lightingBounds).Triangles),
    "Directional light direction must inherit parent rotation");
var scaledParentLight = WorldViewportRenderer.BuildFrame(LightingWorld(
    "{\"entityId\":\"light\",\"name\":\"light\",\"parentId\":\"lightParent\",\"activeSelf\":true,\"components\":[{\"componentId\":\"light-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"light-directional-light\",\"typeId\":\"engine.directional-light\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Intensity\":1,\"Color\":[1,1,1,1]}}]}",
    extra: "{\"entityId\":\"lightParent\",\"name\":\"lightParent\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"lightParent-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[7,2,0.5]}}]}"), lightingBounds);
var rootLight = WorldViewportRenderer.BuildFrame(LightingWorld(
    "{\"entityId\":\"light\",\"name\":\"light\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"light-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"light-directional-light\",\"typeId\":\"engine.directional-light\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Intensity\":1,\"Color\":[1,1,1,1]}}]}"), lightingBounds);
Check(scaledParentLight.Triangles.SequenceEqual(rootLight.Triangles),
    "Parent scale must not alter directional light orientation");
var parentCamera = WorldViewportRenderer.BuildFrame(LightingWorld(string.Empty,
    "{\"entityId\":\"cam\",\"name\":\"cam\",\"parentId\":\"cameraParent\",\"activeSelf\":true,\"components\":[{\"componentId\":\"cam-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[1,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"cam-camera\",\"typeId\":\"engine.camera\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"FieldOfView\":60,\"NearPlane\":0.1,\"FarPlane\":1000}}]}",
    "{\"entityId\":\"cameraParent\",\"name\":\"cameraParent\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"cameraParent-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[2,3,4]}}]}"), lightingBounds);
var rootCamera = WorldViewportRenderer.BuildFrame(LightingWorld(string.Empty,
    "{\"entityId\":\"cam\",\"name\":\"cam\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"cam-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[2,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"cam-camera\",\"typeId\":\"engine.camera\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"FieldOfView\":60,\"NearPlane\":0.1,\"FarPlane\":1000}}]}"), lightingBounds);
Check(parentCamera.Triangles.SequenceEqual(rootCamera.Triangles),
    "Parent scale may move a camera offset but must not alter camera orientation");

Console.WriteLine($"[WorldValidation] PASS: {assertions} assertions; " +
    $"sample={frame.TriangleCount} triangles/{frame.LineCount} lines, " +
    $"near-clipped={clipped.TriangleCount} triangles; vertex ABI=28 bytes.");
