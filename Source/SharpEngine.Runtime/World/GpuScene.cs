using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.UI;

namespace SharpEngine.World;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct GpuSceneVertex(
    Vector3 Position,
    Vector3 Normal,
    uint BoneIndex0,
    uint BoneIndex1,
    uint BoneIndex2,
    uint BoneIndex3,
    uint BoneIndex4,
    uint BoneIndex5,
    uint BoneIndex6,
    uint BoneIndex7,
    uint BoneIndex8,
    uint BoneIndex9,
    uint BoneIndex10,
    uint BoneIndex11,
    Vector4 BoneWeights,
    Vector4 BoneWeights1,
    Vector4 BoneWeights2)
{
    public uint BoneIndex(int Index)
    {
        return Index switch
        {
            0 => BoneIndex0,
            1 => BoneIndex1,
            2 => BoneIndex2,
            3 => BoneIndex3,
            4 => BoneIndex4,
            5 => BoneIndex5,
            6 => BoneIndex6,
            7 => BoneIndex7,
            8 => BoneIndex8,
            9 => BoneIndex9,
            10 => BoneIndex10,
            11 => BoneIndex11,
            _ => throw new ArgumentOutOfRangeException(nameof(Index)),
        };
    }

    public float BoneWeight(int Index)
    {
        return Index switch
        {
            >= 0 and < 4 => BoneWeights[Index],
            >= 4 and < 8 => BoneWeights1[Index - 4],
            >= 8 and < 12 => BoneWeights2[Index - 8],
            _ => throw new ArgumentOutOfRangeException(nameof(Index)),
        };
    }
}

public sealed record GpuMeshSection(
    int FirstIndex,
    int IndexCount,
    IReadOnlyList<int> BoneMap);

public sealed record GpuBvhNode(
    Vector3 Minimum,
    Vector3 Maximum,
    int Left,
    int Right,
    int FirstTriangle,
    int TriangleCount);

public sealed class GpuMeshPayload
{
    private readonly GpuSceneVertex[] VertexData;
    private readonly uint[] IndexData;
    private readonly GpuMeshSection[] SectionData;
    internal GpuMeshPayload(string AssetId, string ContentHash, bool Skinned,
        string? DefaultMaterial,
        GpuSceneVertex[] Vertices, uint[] Indices, GpuMeshSection[] Sections,
        GpuBvhNode[] BvhNodes, int[] BvhTriangles, Vector3 Minimum, Vector3 Maximum)
    {
        this.AssetId = AssetId;
        this.ContentHash = ContentHash;
        this.Skinned = Skinned;
        this.DefaultMaterial = DefaultMaterial;
        VertexData = Vertices;
        IndexData = Indices;
        SectionData = Sections;
        var HashBytes = Convert.FromHexString(ContentHash);
        ResourceIdHigh = BitConverter.ToUInt64(HashBytes, 0);
        ResourceIdLow = BitConverter.ToUInt64(HashBytes, sizeof(ulong));
        this.BvhNodes = BvhNodes;
        this.BvhTriangles = BvhTriangles;
        this.Minimum = Minimum;
        this.Maximum = Maximum;
    }

    public string AssetId { get; }
    public string ContentHash { get; }
    public string ResourceKey => AssetId + "@" + ContentHash;
    public bool Skinned { get; }
    public string? DefaultMaterial { get; }
    public IReadOnlyList<GpuSceneVertex> Vertices => VertexData;
    public IReadOnlyList<uint> Indices => IndexData;
    public IReadOnlyList<GpuMeshSection> Sections => SectionData;
    public ulong ResourceIdHigh { get; }
    public ulong ResourceIdLow { get; }
    public IReadOnlyList<GpuBvhNode> BvhNodes { get; }
    public IReadOnlyList<int> BvhTriangles { get; }
    public Vector3 Minimum { get; }
    public Vector3 Maximum { get; }

    internal ReadOnlySpan<GpuSceneVertex> VertexSpan => VertexData;
    internal ReadOnlySpan<GpuMeshSection> SectionSpan => SectionData;
}

public readonly record struct GpuDirectionalLight(Vector3 Direction, Vector3 Radiance);

public sealed record GpuSceneInstance(
    string EntityId,
    string ComponentId,
    GpuMeshPayload Mesh,
    Matrix4x4 Model,
    Vector4 Color,
    IReadOnlyList<Matrix4x4> SkinMatrices,
    bool Selected);

public sealed record GpuSceneFrame(
    UiRect Bounds,
    Matrix4x4 ViewProjection,
    Vector3 SkyRadiance,
    IReadOnlyList<GpuDirectionalLight> Lights,
    IReadOnlyList<GpuSceneInstance> Instances,
    int EntityCount,
    string CameraId,
    IReadOnlyList<string> Diagnostics);

public sealed record GpuPayloadCacheSnapshot(
    ulong Hits,
    ulong Misses,
    ulong Rebuilds,
    int ResidentPayloads,
    long ResidentBytes);

public static class GpuSceneBuilder
{
    public const int PayloadVersion = 3;
    public const int MaximumBonesPerSection = 256;

    private sealed class PayloadStore(AssetRegistry Assets)
    {
        private const uint CacheMagic = 0x55475053;
        private readonly Dictionary<string, GpuMeshPayload> Payloads =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SourceEntry> Sources =
            new(StringComparer.OrdinalIgnoreCase);
        private ulong Hits;
        private ulong Misses;
        private ulong Rebuilds;

        public GpuMeshPayload Static(string AssetId)
        {
            var Cached = Source(AssetId);
            if (Cached is not null)
            {
                Hits++;
                return Cached;
            }
            var Mesh = AssetContent.LoadStaticMesh(Assets, AssetId);
            var Hash = HashStatic(Mesh);
            var Payload = Get(AssetId, Hash, () => BuildStatic(Mesh));
            Remember(AssetId, Payload);
            return Payload;
        }

        public GpuMeshPayload Skeletal(string AssetId)
        {
            var Cached = Source(AssetId);
            if (Cached is not null)
            {
                Hits++;
                return Cached;
            }
            var Mesh = AssetContent.LoadSkeletalMesh(Assets, AssetId);
            var Hash = HashSkeletal(Mesh);
            var Payload = Get(AssetId, Hash, () => BuildSkeletal(Mesh));
            Remember(AssetId, Payload);
            return Payload;
        }

        public GpuPayloadCacheSnapshot Snapshot()
        {
            var Bytes = Payloads.Values.Sum(PayloadBytes);
            return new(Hits, Misses, Rebuilds, Payloads.Count, Bytes);
        }

        private GpuMeshPayload Get(string AssetId, string Hash,
            Func<GpuMeshPayload> Build)
        {
            var Key = AssetId + "@" + Hash;
            if (Payloads.TryGetValue(Key, out var Existing))
            {
                Hits++;
                return Existing;
            }
            Misses++;
            var Path = CachePath(Hash);
            GpuMeshPayload Payload;
            var WriteRequired = !File.Exists(Path);
            try
            {
                Payload = WriteRequired ? Build() : Read(Path, AssetId, Hash);
            }
            catch (Exception Exception) when (Exception is IOException or
                InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
            {
                Payload = Build();
                WriteRequired = true;
            }
            if (WriteRequired)
            {
                Rebuilds++;
                Write(Path, Payload);
            }
            Payloads[Key] = Payload;
            return Payload;
        }

        private GpuMeshPayload? Source(string AssetId)
        {
            if (!Sources.TryGetValue(AssetId, out var Entry))
            {
                return null;
            }
            var Descriptor = Assets.Find(AssetId);
            var PathValue = Descriptor is null ? null : Assets.ResolvePath(Descriptor);
            if (PathValue is null || !File.Exists(PathValue))
            {
                Sources.Remove(AssetId);
                return null;
            }
            var Info = new FileInfo(PathValue);
            if (!string.Equals(Entry.Path, PathValue, StringComparison.OrdinalIgnoreCase) ||
                Entry.Length != Info.Length || Entry.LastWriteUtc != Info.LastWriteTimeUtc)
            {
                Sources.Remove(AssetId);
                return null;
            }
            return Entry.Payload;
        }

        private void Remember(string AssetId, GpuMeshPayload Payload)
        {
            var Descriptor = Assets.Find(AssetId);
            var PathValue = Descriptor is null ? null : Assets.ResolvePath(Descriptor);
            if (PathValue is null || !File.Exists(PathValue))
            {
                return;
            }
            var Info = new FileInfo(PathValue);
            Sources[AssetId] = new(PathValue, Info.Length, Info.LastWriteTimeUtc,
                Payload);
        }

        private string CachePath(string Hash)
        {
            var Root = Assets.ProjectRoot ?? Assets.EngineRoot;
            return Path.Combine(Root, "Intermediate", "RenderCache",
                Hash.ToLowerInvariant() + ".sgpumesh");
        }

        private static void Write(string PathValue, GpuMeshPayload Payload)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathValue)!);
            var Temporary = PathValue + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var Stream = new FileStream(Temporary, FileMode.CreateNew,
                           FileAccess.Write, FileShare.None))
                using (var Writer = new BinaryWriter(Stream, Encoding.UTF8, leaveOpen: false))
                {
                    Writer.Write(CacheMagic);
                    Writer.Write(PayloadVersion);
                    Writer.Write(Payload.AssetId);
                    Writer.Write(Payload.ContentHash);
                    Writer.Write(Payload.Skinned);
                    Writer.Write(Payload.DefaultMaterial ?? string.Empty);
                    WriteVector(Writer, Payload.Minimum);
                    WriteVector(Writer, Payload.Maximum);
                    Writer.Write(Payload.Vertices.Count);
                    foreach (var Vertex in Payload.Vertices)
                    {
                        WriteVector(Writer, Vertex.Position);
                        WriteVector(Writer, Vertex.Normal);
                        for (var Item = 0; Item < SkeletalMeshAsset.MaximumInfluences; Item++)
                        {
                            Writer.Write(Vertex.BoneIndex(Item));
                        }
                        for (var Item = 0; Item < SkeletalMeshAsset.MaximumInfluences; Item++)
                        {
                            Writer.Write(Vertex.BoneWeight(Item));
                        }
                    }
                    Writer.Write(Payload.Indices.Count);
                    foreach (var Index in Payload.Indices) Writer.Write(Index);
                    Writer.Write(Payload.Sections.Count);
                    foreach (var Section in Payload.Sections)
                    {
                        Writer.Write(Section.FirstIndex);
                        Writer.Write(Section.IndexCount);
                        Writer.Write(Section.BoneMap.Count);
                        foreach (var Bone in Section.BoneMap) Writer.Write(Bone);
                    }
                    Writer.Write(Payload.BvhNodes.Count);
                    foreach (var Node in Payload.BvhNodes)
                    {
                        WriteVector(Writer, Node.Minimum);
                        WriteVector(Writer, Node.Maximum);
                        Writer.Write(Node.Left);
                        Writer.Write(Node.Right);
                        Writer.Write(Node.FirstTriangle);
                        Writer.Write(Node.TriangleCount);
                    }
                    Writer.Write(Payload.BvhTriangles.Count);
                    foreach (var Triangle in Payload.BvhTriangles) Writer.Write(Triangle);
                }
                File.Move(Temporary, PathValue, true);
            }
            finally
            {
                if (File.Exists(Temporary)) File.Delete(Temporary);
            }
        }

        private static GpuMeshPayload Read(string PathValue, string AssetId,
            string Hash)
        {
            using var Stream = File.OpenRead(PathValue);
            using var Reader = new BinaryReader(Stream, Encoding.UTF8, leaveOpen: false);
            if (Reader.ReadUInt32() != CacheMagic || Reader.ReadInt32() != PayloadVersion ||
                Reader.ReadString() != AssetId || Reader.ReadString() != Hash)
            {
                throw new InvalidDataException("GPU payload cache identity mismatch");
            }
            var Skinned = Reader.ReadBoolean();
            var DefaultMaterialValue = Reader.ReadString();
            var DefaultMaterial = DefaultMaterialValue.Length == 0 ? null :
                DefaultMaterialValue;
            var Minimum = ReadVector(Reader);
            var Maximum = ReadVector(Reader);
            var Vertices = new GpuSceneVertex[Count(Reader, "vertices")];
            for (var Index = 0; Index < Vertices.Length; Index++)
            {
                var Position = ReadVector(Reader);
                var Normal = ReadVector(Reader);
                var Bone0 = Reader.ReadUInt32();
                var Bone1 = Reader.ReadUInt32();
                var Bone2 = Reader.ReadUInt32();
                var Bone3 = Reader.ReadUInt32();
                var Bone4 = Reader.ReadUInt32();
                var Bone5 = Reader.ReadUInt32();
                var Bone6 = Reader.ReadUInt32();
                var Bone7 = Reader.ReadUInt32();
                var Bone8 = Reader.ReadUInt32();
                var Bone9 = Reader.ReadUInt32();
                var Bone10 = Reader.ReadUInt32();
                var Bone11 = Reader.ReadUInt32();
                var Weights = new Vector4(Reader.ReadSingle(), Reader.ReadSingle(),
                    Reader.ReadSingle(), Reader.ReadSingle());
                var Weights1 = new Vector4(Reader.ReadSingle(), Reader.ReadSingle(),
                    Reader.ReadSingle(), Reader.ReadSingle());
                var Weights2 = new Vector4(Reader.ReadSingle(), Reader.ReadSingle(),
                    Reader.ReadSingle(), Reader.ReadSingle());
                Vertices[Index] = new(Position, Normal, Bone0, Bone1, Bone2, Bone3,
                    Bone4, Bone5, Bone6, Bone7, Bone8, Bone9, Bone10, Bone11,
                    Weights, Weights1, Weights2);
            }
            var Indices = new uint[Count(Reader, "indices")];
            for (var Index = 0; Index < Indices.Length; Index++) Indices[Index] = Reader.ReadUInt32();
            var Sections = new GpuMeshSection[Count(Reader, "sections")];
            for (var Index = 0; Index < Sections.Length; Index++)
            {
                var FirstIndex = Reader.ReadInt32();
                var IndexCount = Reader.ReadInt32();
                var BoneMap = new int[Count(Reader, "bone map")];
                for (var Bone = 0; Bone < BoneMap.Length; Bone++) BoneMap[Bone] = Reader.ReadInt32();
                Sections[Index] = new(FirstIndex, IndexCount, BoneMap);
            }
            var Nodes = new GpuBvhNode[Count(Reader, "BVH nodes")];
            for (var Index = 0; Index < Nodes.Length; Index++)
            {
                Nodes[Index] = new(ReadVector(Reader), ReadVector(Reader),
                    Reader.ReadInt32(), Reader.ReadInt32(), Reader.ReadInt32(),
                    Reader.ReadInt32());
            }
            var Triangles = new int[Count(Reader, "BVH triangles")];
            for (var Index = 0; Index < Triangles.Length; Index++) Triangles[Index] = Reader.ReadInt32();
            if (Stream.Position != Stream.Length)
            {
                throw new InvalidDataException("GPU payload cache has trailing data");
            }
            return new(AssetId, Hash, Skinned, DefaultMaterial, Vertices, Indices, Sections,
                Nodes, Triangles, Minimum, Maximum);
        }

        private static int Count(BinaryReader Reader, string Name)
        {
            var Value = Reader.ReadInt32();
            if (Value < 0 || Value > 100_000_000)
            {
                throw new InvalidDataException($"Invalid GPU payload {Name} count");
            }
            return Value;
        }

        private sealed record SourceEntry(string Path, long Length,
            DateTime LastWriteUtc, GpuMeshPayload Payload);
    }

    private static readonly ConditionalWeakTable<AssetRegistry, PayloadStore> Stores = new();

    public static GpuSceneFrame BuildFrame(WorldDocument World, UiRect Bounds,
        AssetRegistry Assets, string? SelectedEntityId = null)
    {
        ArgumentNullException.ThrowIfNull(World);
        ArgumentNullException.ThrowIfNull(Assets);
        var Camera = World.ActiveCamera;
        if (Camera is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return new(Bounds, Matrix4x4.Identity, Vector3.Zero, [], [], World.Entities.Count,
                Camera?.Id ?? string.Empty, []);
        }
        var Store = Stores.GetValue(Assets, Registry => new PayloadStore(Registry));
        var Diagnostics = new List<string>();
        var Instances = new List<GpuSceneInstance>();
        foreach (var Entity in World.Entities)
        {
            foreach (var Component in Entity.Components.Where(Component =>
                         WorldEntity.IsMeshComponentType(Component.TypeId) &&
                         Component.Enabled && Entity.ActiveInHierarchy)
                     .OrderBy(Component => Component.ComponentId, StringComparer.Ordinal))
            {
                var AssetId = Component.State["Mesh"]?.GetValue<string>() ?? string.Empty;
                if (AssetId.Length == 0)
                {
                    continue;
                }
                try
                {
                    var Skinned = Component.TypeId == "engine.skeletal-mesh";
                    var Payload = Skinned ? Store.Skeletal(AssetId) : Store.Static(AssetId);
                    var MaterialId = Component.State["Material"]?.GetValue<string>();
                    var Color = ResolveColor(Assets, Entity, MaterialId,
                        Payload.DefaultMaterial);
                    var SkinMatrices = Array.Empty<Matrix4x4>();
                    if (Skinned && World.RenderStates.GetValueOrDefault(Component.ComponentId) is
                            SkeletonFrameState Skeleton)
                    {
                        SkinMatrices = Skeleton.SkinMatrices.ToArray();
                    }
                    Instances.Add(new(Entity.Id, Component.ComponentId, Payload,
                        World.WorldMatrix(Entity), Color, SkinMatrices,
                        string.Equals(Entity.Id, SelectedEntityId,
                            StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception Exception) when (Exception is IOException or
                    InvalidDataException or KeyNotFoundException or ArgumentException or
                    InvalidOperationException or System.Text.Json.JsonException or
                    UnauthorizedAccessException)
                {
                    Diagnostics.Add($"{Entity.Id}: {Exception.Message}");
                }
            }
        }
        var Lights = World.Entities.SelectMany(Entity =>
                Entity.RenderViews("engine.directional-light"))
            .Where(Light => Light.HasEnabledComponent("engine.directional-light") &&
                Light.Intensity > 0)
            .Select(Light => new GpuDirectionalLight(
                SceneRenderer.WorldDirection(World, Light, -Vector3.UnitZ),
                Light.LinearColor("engine.directional-light") * Light.Intensity))
            .ToArray();
        return new(Bounds,
            SceneRenderer.CameraMatrix(World, Camera, Bounds.Width / Bounds.Height),
            SceneRenderer.ResolveSkyRadiance(World), Lights, Instances,
            World.Entities.Count, Camera.Id, Diagnostics);
    }

    public static GpuPayloadCacheSnapshot DescribeCache(AssetRegistry Assets) =>
        Stores.GetValue(Assets, Registry => new PayloadStore(Registry)).Snapshot();

    internal static GpuMeshPayload ResolvePayload(AssetRegistry Assets,
        string AssetId, bool Skinned)
    {
        var Store = Stores.GetValue(Assets,
            Registry => new PayloadStore(Registry));
        return Skinned ? Store.Skeletal(AssetId) : Store.Static(AssetId);
    }

    private static Vector4 ResolveColor(AssetRegistry Assets, WorldEntity Entity,
        string? MaterialId, string? DefaultMaterial)
    {
        var Resolved = string.IsNullOrWhiteSpace(MaterialId) ? DefaultMaterial : MaterialId;
        var Material = string.IsNullOrWhiteSpace(Resolved) ? null :
            AssetContent.LoadMaterial(Assets, Resolved);
        var Base = Material?.BaseColor ?? new AssetColor(1, 1, 1, 1);
        return new(Entity.Color.R / 255f * Base.R,
            Entity.Color.G / 255f * Base.G,
            Entity.Color.B / 255f * Base.B, Base.A);
    }

    private static GpuMeshPayload BuildStatic(StaticMeshAsset Mesh)
    {
        var Positions = Mesh.Positions.Select(Position =>
            new Vector3(Position.X, Position.Y, Position.Z)).ToArray();
        var Result = BuildExpanded(Mesh.AssetId, HashStatic(Mesh), false,
            Mesh.DefaultMaterial, Positions, Mesh.Indices, null);
        return Result;
    }

    private static GpuMeshPayload BuildSkeletal(SkeletalMeshAsset Mesh)
    {
        var Positions = Mesh.Positions.Select(Position =>
            new Vector3(Position.X, Position.Y, Position.Z)).ToArray();
        return BuildExpanded(Mesh.AssetId, HashSkeletal(Mesh), true,
            Mesh.DefaultMaterial, Positions, Mesh.Indices, Mesh.Influences);
    }

    private static GpuMeshPayload BuildExpanded(string AssetId, string Hash,
        bool Skinned, string? DefaultMaterial, IReadOnlyList<Vector3> Positions,
        IReadOnlyList<int> SourceIndices,
        IReadOnlyList<BoneInfluences>? Influences)
    {
        var Vertices = new List<GpuSceneVertex>(SourceIndices.Count);
        var Indices = new List<uint>(SourceIndices.Count);
        var Sections = new List<GpuMeshSection>();
        var CurrentBones = new SortedSet<int>();
        var SectionTriangles = new List<int>();
        void FlushSection()
        {
            if (SectionTriangles.Count == 0) return;
            var BoneMap = CurrentBones.ToArray();
            var LocalBones = BoneMap.Select((Bone, Index) => (Bone, Index))
                .ToDictionary(Pair => Pair.Bone, Pair => Pair.Index);
            var FirstIndex = Indices.Count;
            foreach (var Triangle in SectionTriangles)
            {
                var SourceOffset = Triangle * 3;
                var Position0 = Positions[SourceIndices[SourceOffset]];
                var Position1 = Positions[SourceIndices[SourceOffset + 1]];
                var Position2 = Positions[SourceIndices[SourceOffset + 2]];
                var Normal = Vector3.Normalize(Vector3.Cross(Position1 - Position0,
                    Position2 - Position0));
                if (!IsFinite(Normal)) Normal = Vector3.UnitY;
                for (var Corner = 0; Corner < 3; Corner++)
                {
                    var SourceIndex = SourceIndices[SourceOffset + Corner];
                    BoneInfluences? Influence = Influences is null ? null :
                        Influences[SourceIndex];
                    var BoneIndices = new uint[SkeletalMeshAsset.MaximumInfluences];
                    var Weights = new float[SkeletalMeshAsset.MaximumInfluences];
                    if (Influence is not null)
                    {
                        if (Influence.Value.BoneIndices.Count > SkeletalMeshAsset.MaximumInfluences)
                        {
                            throw new InvalidDataException("GPU skin influence limit exceeded");
                        }
                        for (var Item = 0; Item < Influence.Value.BoneIndices.Count;
                             Item++)
                        {
                            var Weight = Influence.Value.Weights[Item];
                            if (Weight == 0)
                            {
                                continue;
                            }
                            var GlobalBone = Influence.Value.BoneIndices[Item];
                            BoneIndices[Item] = (uint)LocalBones[GlobalBone];
                            Weights[Item] = Weight;
                        }
                    }
                    var Vertex = new GpuSceneVertex(Positions[SourceIndex], Normal,
                        BoneIndices[0], BoneIndices[1], BoneIndices[2], BoneIndices[3],
                        BoneIndices[4], BoneIndices[5], BoneIndices[6], BoneIndices[7],
                        BoneIndices[8], BoneIndices[9], BoneIndices[10], BoneIndices[11],
                        new Vector4(Weights[0], Weights[1], Weights[2], Weights[3]),
                        new Vector4(Weights[4], Weights[5], Weights[6], Weights[7]),
                        new Vector4(Weights[8], Weights[9], Weights[10], Weights[11]));
                    Indices.Add((uint)Vertices.Count);
                    Vertices.Add(Vertex);
                }
            }
            Sections.Add(new(FirstIndex, Indices.Count - FirstIndex, BoneMap));
            SectionTriangles.Clear();
            CurrentBones.Clear();
        }

        for (var Triangle = 0; Triangle < SourceIndices.Count / 3; Triangle++)
        {
            var TriangleBones = new SortedSet<int>();
            if (Influences is not null)
            {
                for (var Corner = 0; Corner < 3; Corner++)
                {
                    var Influence = Influences[SourceIndices[Triangle * 3 + Corner]];
                    for (var Item = 0; Item < Influence.BoneIndices.Count; Item++)
                    {
                        if (Influence.Weights[Item] > 0) TriangleBones.Add(Influence.BoneIndices[Item]);
                    }
                }
            }
            if (CurrentBones.Union(TriangleBones).Count() > MaximumBonesPerSection)
            {
                FlushSection();
            }
            CurrentBones.UnionWith(TriangleBones);
            SectionTriangles.Add(Triangle);
        }
        FlushSection();
        if (Sections.Count == 0)
        {
            Sections.Add(new(0, 0, []));
        }
        var (Minimum, Maximum) = Bounds(Positions);
        var (Nodes, BvhTriangles) = BuildBvh(Positions, SourceIndices);
        return new(AssetId, Hash, Skinned, DefaultMaterial, Vertices.ToArray(), Indices.ToArray(),
            Sections.ToArray(), Nodes, BvhTriangles, Minimum, Maximum);
    }

    private static (GpuBvhNode[] Nodes, int[] Triangles) BuildBvh(
        IReadOnlyList<Vector3> Positions, IReadOnlyList<int> Indices)
    {
        var Ordered = Enumerable.Range(0, Indices.Count / 3).ToList();
        var Nodes = new List<GpuBvhNode>();
        int Build(int Start, int Count)
        {
            var Minimum = new Vector3(float.MaxValue);
            var Maximum = new Vector3(float.MinValue);
            var CentroidMinimum = Minimum;
            var CentroidMaximum = Maximum;
            for (var Item = Start; Item < Start + Count; Item++)
            {
                var Triangle = Ordered[Item];
                var First = Positions[Indices[Triangle * 3]];
                var Second = Positions[Indices[Triangle * 3 + 1]];
                var Third = Positions[Indices[Triangle * 3 + 2]];
                Minimum = Vector3.Min(Minimum, Vector3.Min(First,
                    Vector3.Min(Second, Third)));
                Maximum = Vector3.Max(Maximum, Vector3.Max(First,
                    Vector3.Max(Second, Third)));
                var Centroid = (First + Second + Third) / 3;
                CentroidMinimum = Vector3.Min(CentroidMinimum, Centroid);
                CentroidMaximum = Vector3.Max(CentroidMaximum, Centroid);
            }
            var NodeIndex = Nodes.Count;
            Nodes.Add(null!);
            if (Count <= 8)
            {
                Nodes[NodeIndex] = new(Minimum, Maximum, -1, -1, Start, Count);
                return NodeIndex;
            }
            var Extent = CentroidMaximum - CentroidMinimum;
            var Axis = Extent.X >= Extent.Y && Extent.X >= Extent.Z ? 0 :
                Extent.Y >= Extent.Z ? 1 : 2;
            Ordered.Sort(Start, Count, Comparer<int>.Create((Left, Right) =>
            {
                var LeftValue = TriangleCentroid(Positions, Indices, Left, Axis);
                var RightValue = TriangleCentroid(Positions, Indices, Right, Axis);
                var Comparison = LeftValue.CompareTo(RightValue);
                return Comparison != 0 ? Comparison : Left.CompareTo(Right);
            }));
            var LeftCount = Count / 2;
            var Left = Build(Start, LeftCount);
            var Right = Build(Start + LeftCount, Count - LeftCount);
            Nodes[NodeIndex] = new(Minimum, Maximum, Left, Right, 0, 0);
            return NodeIndex;
        }
        if (Ordered.Count > 0) Build(0, Ordered.Count);
        return (Nodes.ToArray(), Ordered.ToArray());
    }

    private static float TriangleCentroid(IReadOnlyList<Vector3> Positions,
        IReadOnlyList<int> Indices, int Triangle, int Axis)
    {
        var Value = (Positions[Indices[Triangle * 3]] +
            Positions[Indices[Triangle * 3 + 1]] +
            Positions[Indices[Triangle * 3 + 2]]) / 3;
        return Axis == 0 ? Value.X : Axis == 1 ? Value.Y : Value.Z;
    }

    private static (Vector3 Minimum, Vector3 Maximum) Bounds(
        IReadOnlyList<Vector3> Positions)
    {
        var Minimum = new Vector3(float.MaxValue);
        var Maximum = new Vector3(float.MinValue);
        foreach (var Position in Positions)
        {
            Minimum = Vector3.Min(Minimum, Position);
            Maximum = Vector3.Max(Maximum, Position);
        }
        return (Minimum, Maximum);
    }

    private static string HashStatic(StaticMeshAsset Mesh) => Hash(
        "static", Mesh.Serialize(false));

    private static string HashSkeletal(SkeletalMeshAsset Mesh) => Hash(
        "skeletal", Mesh.Serialize(false));

    private static string Hash(string Kind, string Content) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"SharpEngine.GpuMeshPayload|{PayloadVersion}|{Kind}|{Content}")));

    private static long PayloadBytes(GpuMeshPayload Payload) =>
        (long)Payload.Vertices.Count * Marshal.SizeOf<GpuSceneVertex>() +
        (long)Payload.Indices.Count * sizeof(uint) +
        Payload.Sections.Sum(Section => (long)Section.BoneMap.Count * sizeof(int));

    private static bool IsFinite(Vector3 Value) => float.IsFinite(Value.X) &&
        float.IsFinite(Value.Y) && float.IsFinite(Value.Z);

    private static void WriteVector(BinaryWriter Writer, Vector3 Value)
    {
        Writer.Write(Value.X);
        Writer.Write(Value.Y);
        Writer.Write(Value.Z);
    }

    private static Vector3 ReadVector(BinaryReader Reader) =>
        new(Reader.ReadSingle(), Reader.ReadSingle(), Reader.ReadSingle());
}
