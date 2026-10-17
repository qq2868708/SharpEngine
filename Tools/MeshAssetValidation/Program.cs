using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.UI;
using SharpEngine.World;

var root = Path.GetFullPath(args.Length == 0 ? Directory.GetCurrentDirectory() : args[0]);
var checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    checks++;
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (Exception error) when (error is InvalidDataException or FileNotFoundException)
    { checks++; return; }
    throw new InvalidOperationException($"Expected rejection: {message}");
}

var registry = AssetRegistry.Load(root);
Check(registry.Diagnostics.All(diagnostic => !diagnostic.IsError), "Mesh manifest has no load errors");
var staticMesh = AssetContent.LoadStaticMesh(registry, "engine://meshes/robot-static");
var skeletalMesh = AssetContent.LoadSkeletalMesh(registry, "engine://meshes/robot-skeletal");
var skeleton = AssetContent.LoadSkeleton(registry, "engine://skeletons/robot");
Check(staticMesh.SourcePath is not null && staticMesh.SourceSha256 is not null, "Static mesh retains FBX provenance");
Check(skeletalMesh.SourcePath is not null && skeletalMesh.SourceSha256 is not null, "Skeletal mesh retains FBX provenance");
Check(skeleton.SourcePath is not null && skeleton.SourceSha256 is not null, "Skeleton retains FBX provenance");
Check(StaticMeshAsset.Parse(staticMesh.Serialize(false)).Serialize(false) == staticMesh.Serialize(false),
    "Static mesh JSON round trip is deterministic");
Check(SkeletalMeshAsset.Parse(skeletalMesh.Serialize(false)).Serialize(false) == skeletalMesh.Serialize(false),
    "Skeletal mesh JSON round trip is deterministic");
Check(SkeletonAsset.Parse(skeleton.Serialize(false)).Serialize(false) == skeleton.Serialize(false),
    "Skeleton JSON round trip is deterministic");

var source = Path.Combine(root, "Content", "Assets", "Sources", "Robot.fbx");
var original = File.ReadAllBytes(source);
var temporary = Path.Combine(Path.GetTempPath(), $"SharpEngine-{Guid.NewGuid():N}.fbx");
try
{
    Check(AssetContent.ExportOriginalFbx(registry, "engine://meshes/robot-static", temporary) == temporary,
        "Static mesh export reports destination");
    Check(File.ReadAllBytes(temporary).AsSpan().SequenceEqual(original),
        "Static mesh export preserves FBX bytes exactly");
    Check(staticMesh.ExportOriginalFbx(registry, temporary) == temporary &&
        File.ReadAllBytes(temporary).AsSpan().SequenceEqual(original),
        "Static mesh instance export uses the same verified source contract");
    Check(AssetContent.ExportOriginalFbx(registry, "engine://sources/robot-fbx", temporary) == temporary &&
        File.ReadAllBytes(temporary).AsSpan().SequenceEqual(original),
        "FBX source descriptor export preserves the authoritative bytes");
    File.AppendAllText(source, "\n# changed after import\n");
    Reject(() => AssetContent.ExportOriginalFbx(registry, "engine://meshes/robot-static", temporary),
        "modified FBX source is rejected by SHA-256 verification");
}
finally
{
    File.WriteAllBytes(source, original);
    File.Delete(temporary);
}

var world = WorldDocument.Parse("""
{"schemaVersion":3,"kind":"world","worldId":"mesh-fixture","name":"Mesh Fixture","activeCameraId":"camera","entities":[
{"entityId":"static","name":"Static","parentId":null,"activeSelf":true,"components":[
{"componentId":"static-transform","typeId":"engine.transform","schemaVersion":1,"enabled":true,"state":{"Position":[-0.8,0,-2],"Rotation":[0,0,0],"Scale":[1,1,1]}},
{"componentId":"static-mesh","typeId":"engine.static-mesh","schemaVersion":1,"enabled":true,"state":{"Mesh":"engine://meshes/robot-static","Material":null,"Color":[1,1,1,1]}}]},
{"entityId":"skeletal","name":"Skeletal","parentId":null,"activeSelf":true,"components":[
{"componentId":"skeletal-transform","typeId":"engine.transform","schemaVersion":1,"enabled":true,"state":{"Position":[0.8,0,-2],"Rotation":[0,0,0],"Scale":[1,1,1]}},
{"componentId":"skeletal-mesh","typeId":"engine.skeletal-mesh","schemaVersion":1,"enabled":true,"state":{"Mesh":"engine://meshes/robot-skeletal","Material":null,"Animation":"engine://animations/robot-walk","AnimationTime":0.25,"LoopAnimation":true,"Color":[1,1,1,1]}}]},
{"entityId":"camera","name":"Camera","parentId":null,"activeSelf":true,"components":[
{"componentId":"camera-transform","typeId":"engine.transform","schemaVersion":1,"enabled":true,"state":{"Position":[0,0,4],"Rotation":[0,0,0],"Scale":[1,1,1]}},
{"componentId":"camera-component","typeId":"engine.camera","schemaVersion":1,"enabled":true,"state":{"FieldOfView":60,"NearPlane":0.1,"FarPlane":1000}}]}]}
""");
Check(world.MeshCount == 2 && world.Entities.Single(e => e.Id == "skeletal").Asset == "engine://meshes/robot-skeletal",
    "World description exposes static and skeletal mesh components");
var roundTrip = WorldDocument.Parse(world.Serialize());
Check(roundTrip.Serialize() == world.Serialize(), "World mesh components round trip deterministically");
using (var runtime = new RuntimeWorld(world.Data, world.Registry))
{
    Check(runtime.FindComponent("static-mesh") is not null && runtime.FindComponent("skeletal-mesh") is not null,
        "RuntimeWorld rebuilds both mesh component types");
}
var frame = SceneRenderer.BuildFrame(world, new UiRect(0, 0, 800, 500), registry);
Check(frame.TriangleCount == 24 && frame.Diagnostics.Count == 0,
    "SceneRenderer draws static and skeletal mesh geometry");
Check(SceneRenderer.TryPick(world, new UiRect(0, 0, 800, 500), 400, 250, out _, registry) == false,
    "Viewport center between fixture meshes is not a false positive pick");

Console.WriteLine($"[MeshAssetValidation] PASS: {checks} assertions; staticVertices={staticMesh.Positions.Count}; skeletalVertices={skeletalMesh.Positions.Count}; triangles={frame.TriangleCount}");
