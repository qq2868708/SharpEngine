using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Mathematics;
using SharpEngine.UI;
using SharpEngine.World;

var Root = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
var Evidence = Path.Combine(Root, "Saved", "Validation", "SkeletalReimport-20261020");
var Stage = Path.Combine(Evidence, "Stage");
var Assets = AssetRegistry.Load(Stage);
var Assertions = 0;
void Check(bool Condition, string Message)
{
    if (!Condition)
    {
        throw new InvalidOperationException(Message);
    }
    Assertions++;
}
void Reject(Action Action, string Message)
{
    try
    {
        Action();
    }
    catch (InvalidDataException)
    {
        Assertions++;
        return;
    }
    throw new InvalidOperationException("Expected rejection: " + Message);
}
Vector3 ReadVector(BinaryReader Reader) { return new(Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle()); }
Matrix4x4 ReadMatrix(BinaryReader Reader)
{
    return new(Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle(),
        Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle(),
        Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle(),
        Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle());
}
Check(!Assets.Diagnostics.Any(Diagnostic => Diagnostic.IsError),
    string.Join("; ", Assets.Diagnostics.Select(Diagnostic => Diagnostic.Message)));
var Mesh = SkeletalMeshContent.Load(Assets, "engine://meshes/uefn-mannequin");
var Skeleton = SkeletonContent.Load(Assets, Mesh.SkeletonId);
Check(Mesh.Bones.Count == 88 && Mesh.Positions.Count == 6009 && Mesh.Indices.Count == 9324 * 3,
    "Reimport preserves bone and geometry counts");
Check(Mesh.Influences.Max(Influence => Influence.Weights.Count) == 9,
    "The source's ninth influence survives asset loading");
Check(Marshal.SizeOf<GpuSceneVertex>() == 120, "The GPU vertex ABI is 120 bytes");
Check(SkeletalMeshAsset.Parse(Mesh.Serialize(false)).Serialize(false) == Mesh.Serialize(false),
    "Schema 2 has a deterministic round trip");
var Legacy = SkeletalMeshAsset.Parse(File.ReadAllText(Path.Combine(Root,
    "Content", "Assets", "Meshes", "Robot.skeletalmesh.json")));
Check(JsonNode.Parse(Legacy.Serialize())!["schemaVersion"]!.GetValue<int>() == 2,
    "Legacy schema 1 migrates to schema 2 without losing data");
var InvalidLegacy = JsonNode.Parse(Mesh.Serialize())!.AsObject();
InvalidLegacy["schemaVersion"] = 1;
Reject(() => SkeletalMeshAsset.Parse(InvalidLegacy.ToJsonString()),
    "Schema 1 must reject weights that it cannot represent");
var InvalidFuture = (JsonObject)InvalidLegacy.DeepClone();
InvalidFuture["schemaVersion"] = 3;
Reject(() => SkeletalMeshAsset.Parse(InvalidFuture.ToJsonString()), "Unknown future schema");
var Twelve = JsonNode.Parse(Mesh.Serialize())!.AsObject();
Twelve["influences"]![0]!["boneIndices"] = new JsonArray(Enumerable.Range(0, 12)
    .Select(Index => (JsonNode?)JsonValue.Create(Index)).ToArray());
Twelve["influences"]![0]!["weights"] = new JsonArray(Enumerable.Range(0, 12)
    .Select(Index => (JsonNode?)JsonValue.Create(1f / 12)).ToArray());
var TwelveMesh = SkeletalMeshAsset.Parse(Twelve.ToJsonString());
var TwelveBind = new SkeletonInstance(TwelveMesh).Prepare("twelve", "twelve-mesh", null, "READY");
Check(TwelveMesh.Influences[0].BoneIndices.Count == 12 &&
    Vector3.Distance(TwelveBind.Positions[0], new Vector3(Mesh.Positions[0].X,
        Mesh.Positions[0].Y, Mesh.Positions[0].Z)) < 1e-5f,
    "All twelve nonzero influences are accepted and preserve the bind-pose vertex");
var Duplicate = (JsonObject)Twelve.DeepClone();
Duplicate["influences"]![0]!["boneIndices"]![1] = 0;
Reject(() => SkeletalMeshAsset.Parse(Duplicate.ToJsonString()), "Repeated nonzero bone influences");
var TooMany = JsonNode.Parse(Mesh.Serialize())!.AsObject();
TooMany["influences"]![0]!["boneIndices"] = new JsonArray(Enumerable.Range(0, 13)
    .Select(Index => (JsonNode?)JsonValue.Create(Index)).ToArray());
TooMany["influences"]![0]!["weights"] = new JsonArray(Enumerable.Range(0, 13)
    .Select(Index => (JsonNode?)JsonValue.Create(1f / 13)).ToArray());
Reject(() => SkeletalMeshAsset.Parse(TooMany.ToJsonString()), "Thirteen influences must never be truncated");

var Registry = ComponentRegistry.CreateBuiltin();
var Data = new SceneData();
var Entity = Data.CreateEntity(Registry, "Reimported Mannequin");
var MeshComponent = Data.AddComponent(Registry, Entity.EntityId, "engine.skeletal-mesh")
    .Single(Component => Component.TypeId == "engine.skeletal-mesh");
MeshComponent.State["Mesh"] = Mesh.AssetId;
var Animator = Data.AddComponent(Registry, Entity.EntityId, "engine.animator").Single();
Animator.State["Target"] = MeshComponent.ComponentId;
Animator.State["Clip"] = "engine://animations/uefn-mannequin-idle";
Animator.State["Playing"] = false;
Animator.State["Loop"] = false;
var Camera = Data.CreateEntity(Registry, "Camera");
Data.AddComponent(Registry, Camera.EntityId, "engine.camera");
Camera.Components.Single(Component => Component.TypeId == "engine.transform").State["Position"] = new JsonArray(0f, 1f, 4f);
Data.ActiveCameraId = Camera.EntityId;
using var Runtime = new RuntimeWorld(Data, Registry, Assets);
var World = new WorldDocument(Runtime.Frame, Registry);
var GpuFrame = GpuSceneBuilder.BuildFrame(World, new UiRect(0, 0, 800, 600), Assets);
Check(GpuFrame.Diagnostics.Count == 0, "GPU scene preparation must succeed");
var Payload = GpuFrame.Instances.Single().Mesh;
foreach (var Section in Payload.Sections)
{
    for (var Index = Section.FirstIndex; Index < Section.FirstIndex + Section.IndexCount; Index++)
    {
        var SourceVertex = Mesh.Indices[Index];
        var Influence = Mesh.Influences[SourceVertex];
        var Vertex = Payload.Vertices[Index];
        for (var Item = 0; Item < Influence.Weights.Count; Item++)
        {
            Check(Section.BoneMap[(int)Vertex.BoneIndex(Item)] == Influence.BoneIndices[Item] &&
                Vertex.BoneWeight(Item) == Influence.Weights[Item], "GPU packing preserves every source influence");
        }
    }
}
var ReloadedAssets = AssetRegistry.Load(Stage);
var ReloadedData = Data.Clone();
using var ReloadedRuntime = new RuntimeWorld(ReloadedData, Registry, ReloadedAssets);
var ReloadedFrame = GpuSceneBuilder.BuildFrame(new WorldDocument(ReloadedRuntime.Frame, Registry), new UiRect(0, 0, 800, 600), ReloadedAssets);
Check(ReloadedFrame.Instances.Single().Mesh.ContentHash == Payload.ContentHash &&
    GpuSceneBuilder.DescribeCache(ReloadedAssets).Rebuilds == 0, "GPU cache v3 survives a new registry");

var Bind = new Matrix4x4[Mesh.Bones.Count];
var Ready = new bool[Bind.Length];
void BuildBind(int Index)
{
    if (Ready[Index])
    {
        return;
    }
    var Bone = Mesh.Bones[Index];
    var Parent = Bone.ParentBoneId is null ? -1 : Mesh.Bones.ToList().FindIndex(Item => Item.BoneId == Bone.ParentBoneId);
    if (Parent >= 0)
    {
        BuildBind(Parent);
    }
    var Trs = Bone.BindTransform;
    Bind[Index] = TransformMath.Trs(Trs.Position, Trs.Rotation, Trs.Scale) * (Parent < 0 ? Matrix4x4.Identity : Bind[Parent]);
    Ready[Index] = true;
}
for (var Index = 0; Index < Bind.Length; Index++)
{
    BuildBind(Index);
}
var Instance = new SkeletonInstance(Mesh);
var Reports = new JsonArray();
using var Stream = File.OpenRead(Path.Combine(Evidence, "blender-reference.bin"));
using var Reader = new BinaryReader(Stream, Encoding.UTF8);
Check(Encoding.ASCII.GetString(Reader.ReadBytes(4)) == "SEBG" && Reader.ReadInt32() == 1,
    "Independent Blender reference header");
Check(Reader.ReadInt32() == Bind.Length && Reader.ReadInt32() == Mesh.Positions.Count,
    "Reference topology matches imported assets");
var ClipCount = Reader.ReadInt32();
var MaximumVertexError = 0f;
var MaximumJointError = 0f;
var TotalSamples = 0;
for (var ClipIndex = 0; ClipIndex < ClipCount; ClipIndex++)
{
    var NameLength = Reader.ReadInt32();
    var Name = Encoding.UTF8.GetString(Reader.ReadBytes(NameLength));
    var SampleCount = Reader.ReadInt32();
    var Clip = SkeletalAnimationContent.Load(Assets, "engine://animations/uefn-mannequin-" + Name.ToLowerInvariant());
    SkeletalAnimationCompatibility.Validate(Clip, Mesh);
    var ClipVertexError = 0f;
    var ClipJointError = 0f;
    var MaximumGpuError = 0f;
    for (var SampleIndex = 0; SampleIndex < SampleCount; SampleIndex++)
    {
        var Time = Reader.ReadDouble();
        var Frame = Instance.Prepare("reference", "mesh", Clip.Sample(Time), "VALIDATION");
        for (var BoneIndex = 0; BoneIndex < Bind.Length; BoneIndex++)
        {
            var Expected = ReadMatrix(Reader);
            var Actual = Bind[BoneIndex] * Frame.SkinMatrices[BoneIndex];
            var Error = Vector3.Distance(Expected.Translation, Actual.Translation);
            ClipJointError = Math.Max(ClipJointError, Error);
            Check(Error <= 0.00005f, $"{Name} time={Time}: joint {BoneIndex} differs from Blender by {Error}");
        }
        var Positions = Frame.Positions;
        for (var VertexIndex = 0; VertexIndex < Mesh.Positions.Count; VertexIndex++)
        {
            var Expected = ReadVector(Reader);
            var Actual = Positions[VertexIndex];
            var Error = Vector3.Distance(Expected, Actual);
            ClipVertexError = Math.Max(ClipVertexError, Error);
            Check(Error <= 0.00005f, $"{Name} time={Time}: vertex {VertexIndex} differs from Blender by {Error}");
        }
        // Simulate the two backends' exact twelve-lane vertex/palette contract.
        if (SampleIndex % 8 == 0 || SampleIndex == SampleCount - 1)
        {
            foreach (var Section in Payload.Sections)
            {
                for (var Index = Section.FirstIndex; Index < Section.FirstIndex + Section.IndexCount; Index++)
                {
                    var Vertex = Payload.Vertices[Index];
                    var Position = Vector3.Zero;
                    var Total = 0f;
                    for (var Item = 0; Item < SkeletalMeshAsset.MaximumInfluences; Item++)
                    {
                        var Weight = Vertex.BoneWeight(Item);
                        Position += Vector3.Transform(Vertex.Position,
                            Frame.SkinMatrices[Section.BoneMap[(int)Vertex.BoneIndex(Item)]]) * Weight;
                        Total += Weight;
                    }
                    Position /= Total;
                    var Error = Vector3.Distance(Position, Positions[Mesh.Indices[Index]]);
                    MaximumGpuError = Math.Max(MaximumGpuError, Error);
                    Check(Error < 1e-5f, "GPU palette and twelve-lane weights must match CPU skinning");
                }
            }
        }
        TotalSamples++;
    }
    MaximumVertexError = Math.Max(MaximumVertexError, ClipVertexError);
    MaximumJointError = Math.Max(MaximumJointError, ClipJointError);
    Reports.Add(new JsonObject
    {
        ["clip"] = Name,
        ["samples"] = SampleCount,
        ["maximumVertexErrorMetres"] = ClipVertexError,
        ["maximumJointErrorMetres"] = ClipJointError,
        ["maximumPackedGpuErrorMetres"] = MaximumGpuError,
    });
    Console.WriteLine($"[SkeletalReimport] {Name}: samples={SampleCount} vertexError={ClipVertexError:E3} jointError={ClipJointError:E3}");
}
Check(Stream.Position == Stream.Length, "The complete Blender reference was consumed");
var Report = new JsonObject
{
    ["schemaVersion"] = 1,
    ["assertions"] = Assertions,
    ["samples"] = TotalSamples,
    ["vertexToleranceMetres"] = 0.00005,
    ["maximumVertexErrorMetres"] = MaximumVertexError,
    ["maximumJointErrorMetres"] = MaximumJointError,
    ["maximumInfluences"] = Mesh.Influences.Max(Influence => Influence.Weights.Count),
    ["clips"] = Reports,
};
File.WriteAllText(Path.Combine(Evidence, "engine-comparison.json"), Report.ToJsonString(new JsonSerializerOptions
{
    WriteIndented = true,
}));
Console.WriteLine($"[SkeletalReimport] PASS: assertions={Assertions} samples={TotalSamples}");
