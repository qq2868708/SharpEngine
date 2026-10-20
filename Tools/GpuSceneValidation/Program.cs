using SharpEngine.Assets;
using SharpEngine.UI;
using SharpEngine.World;

var Root = Path.GetFullPath(args.Length > 0 ? args[0] :
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var Assertions = 0;
void Check(bool Condition, string Message)
{
    if (!Condition)
    {
        throw new InvalidOperationException(Message);
    }
    Assertions++;
}

var Assets = AssetRegistry.Load(Root);
var World = WorldDocument.Load(Path.Combine(Root, "Content", "Worlds", "Main.sworld"));
SceneRenderPreparation.Prepare(World, Assets);
var Bounds = new UiRect(0, 0, 1280, 720);
var Frame = GpuSceneBuilder.BuildFrame(World, Bounds, Assets);
Check(Frame.Instances.Count == 3, "The sample world must submit three GPU mesh instances");
Check(Frame.Lights.Count == 1, "The sample world must submit one directional light");
Check(System.Numerics.Vector3.Distance(Frame.SkyRadiance,
        new System.Numerics.Vector3(0.24f)) < 0.0001f,
    "The sample world must derive sky radiance from its Sky Light component");
Check(Frame.ViewProjection != System.Numerics.Matrix4x4.Identity,
    "The GPU frame must contain the active camera view-projection matrix");

var Skeletal = Frame.Instances.Single(Instance => Instance.Mesh.Skinned);
Check(Skeletal.Mesh.AssetId == "engine://meshes/uefn-mannequin",
    "The mannequin must retain its stable asset identity");
Check(Skeletal.Mesh.Vertices.Count == 27_972 && Skeletal.Mesh.Indices.Count == 27_972,
    "Flat shading must expand the 9,324 mannequin triangles deterministically");
Check(Skeletal.Mesh.Sections.Count > 0 &&
    Skeletal.Mesh.Sections.All(Section =>
        Section.BoneMap.Count <= GpuSceneBuilder.MaximumBonesPerSection),
    "Every skeletal section must fit the portable GPU bone palette");
var SourceMesh = AssetContent.LoadSkeletalMesh(Assets,
    "engine://meshes/uefn-mannequin");
var UsedBones = SourceMesh.Influences.SelectMany(Influence =>
        Influence.BoneIndices.Zip(Influence.Weights)
            .Where(Pair => Pair.Second > 0).Select(Pair => Pair.First))
    .Distinct().Order().ToArray();
var SectionBones = Skeletal.Mesh.Sections.SelectMany(Section => Section.BoneMap)
    .Distinct().Order().ToArray();
Check(SectionBones.SequenceEqual(UsedBones),
    "The mannequin section maps must retain every bone with a non-zero influence");
Check(Skeletal.SkinMatrices.Count == 88,
    "The live skeletal instance must submit all current skin matrices");
Check(Skeletal.Mesh.BvhNodes.Count > 0 && Skeletal.Mesh.BvhTriangles.Count == 9_324,
    "The derived payload must contain a complete deterministic picking BVH");
Check(Skeletal.Mesh.Minimum.X <= Skeletal.Mesh.Maximum.X &&
    Skeletal.Mesh.Minimum.Y <= Skeletal.Mesh.Maximum.Y &&
    Skeletal.Mesh.Minimum.Z <= Skeletal.Mesh.Maximum.Z,
    "The derived payload bounds must be ordered");

var FirstSnapshot = GpuSceneBuilder.DescribeCache(Assets);
var SecondFrame = GpuSceneBuilder.BuildFrame(World, Bounds, Assets);
var SecondSnapshot = GpuSceneBuilder.DescribeCache(Assets);
Check(SecondFrame.Instances.Select(Instance => Instance.Mesh.ContentHash)
        .SequenceEqual(Frame.Instances.Select(Instance => Instance.Mesh.ContentHash)),
    "Unchanged assets must retain stable GPU payload hashes");
Check(SecondSnapshot.Hits >= FirstSnapshot.Hits + 3,
    "A repeated frame must reuse all three resident mesh payloads");
Check(SecondSnapshot.ResidentPayloads == 3 && SecondSnapshot.ResidentBytes > 0,
    "The cache snapshot must expose resident payload count and bytes");

var CacheRoot = Path.Combine(Root, "Intermediate", "RenderCache");
Check(Frame.Instances.Select(Instance => Path.Combine(CacheRoot,
        Instance.Mesh.ContentHash.ToLowerInvariant() + ".sgpumesh")).All(File.Exists),
    "Every referenced payload must be atomically persisted in the render cache");

var ReloadedAssets = AssetRegistry.Load(Root);
var ReloadedWorld = WorldDocument.Load(Path.Combine(Root, "Content", "Worlds", "Main.sworld"));
SceneRenderPreparation.Prepare(ReloadedWorld, ReloadedAssets);
var Reloaded = GpuSceneBuilder.BuildFrame(ReloadedWorld, Bounds, ReloadedAssets);
var ReloadedSnapshot = GpuSceneBuilder.DescribeCache(ReloadedAssets);
Check(Reloaded.Instances.Select(Instance => Instance.Mesh.ContentHash)
        .SequenceEqual(Frame.Instances.Select(Instance => Instance.Mesh.ContentHash)),
    "Disk-cached payloads must preserve deterministic identities across registries");
Check(ReloadedSnapshot.Rebuilds == 0,
    "A second registry must read valid payloads without rebuilding them");

var NoSkyData = World.Data.Clone();
NoSkyData.Entities.RemoveAll(Entity => Entity.Components.Any(Component =>
    Component.TypeId == "engine.sky-light"));
var NoSkyWorld = new WorldDocument(NoSkyData, World.Registry);
SceneRenderPreparation.Prepare(NoSkyWorld, Assets);
var NoSkyFrame = GpuSceneBuilder.BuildFrame(NoSkyWorld, Bounds, Assets);
Check(NoSkyFrame.SkyRadiance == System.Numerics.Vector3.Zero,
    "A GPU scene without an active Sky Light must submit zero sky radiance");

Console.WriteLine($"[GpuSceneValidation] PASS: {Assertions} assertions; " +
    $"instances={Frame.Instances.Count}; sections={Skeletal.Mesh.Sections.Count}; " +
    $"vertices={Skeletal.Mesh.Vertices.Count}; cacheBytes={SecondSnapshot.ResidentBytes}");
