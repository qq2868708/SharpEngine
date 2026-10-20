using System.Numerics;
using System.Runtime.InteropServices;
using SharpEngine.World;

namespace SharpEngine.Interop;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeGpuResourceId(
    ulong High,
    ulong Low,
    uint Section);

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeGpuMeshUpload
{
    public NativeGpuResourceId Resource;
    public GpuSceneVertex* Vertices;
    public uint VertexCount;
    public byte Skinned;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeGpuLight(Vector3 Direction, Vector3 Radiance);

[StructLayout(LayoutKind.Sequential)]
internal struct NativeGpuDraw
{
    public NativeGpuResourceId Resource;
    public Matrix4x4 Model;
    public Vector4 Color;
    public uint SkinMatrixOffset;
    public uint SkinMatrixCount;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeGpuSceneFrame
{
    public float ViewportX;
    public float ViewportY;
    public float ViewportWidth;
    public float ViewportHeight;
    public float ClientHeight;
    public Vector3 SkyRadiance;
    public Matrix4x4 ViewProjection;
    public NativeGpuLight* Lights;
    public uint LightCount;
    public NativeGpuDraw* Draws;
    public uint DrawCount;
    public Matrix4x4* SkinMatrices;
    public uint SkinMatrixCount;
}

public sealed unsafe class NativeGpuSceneBridge
{
    private readonly delegate* unmanaged[Cdecl]<NativeGpuMeshUpload*, byte>
        EnsureGpuMeshFunction;
    private readonly delegate* unmanaged[Cdecl]<NativeGpuSceneFrame*, byte>
        SubmitGpuSceneFunction;

    public NativeGpuSceneBridge(nint NativeApi)
    {
        var Table = (NativeEngineApiTable*)NativeApi;
        if (Table is not null && Table->Version >= 16)
        {
            EnsureGpuMeshFunction = Table->EnsureGpuMesh;
            SubmitGpuSceneFunction = Table->SubmitGpuScene;
        }
    }

    public bool Available => EnsureGpuMeshFunction != null &&
        SubmitGpuSceneFunction != null;

    public bool Submit(GpuSceneFrame Frame, float ClientHeight)
    {
        if (!Available)
        {
            return false;
        }
        var Resources = new HashSet<NativeGpuResourceId>();
        foreach (var Instance in Frame.Instances)
        {
            var Sections = Instance.Mesh.SectionSpan;
            for (var SectionIndex = 0; SectionIndex < Sections.Length; SectionIndex++)
            {
                var Resource = ResourceId(Instance.Mesh, SectionIndex);
                if (!Resources.Add(Resource))
                {
                    continue;
                }
                var Section = Sections[SectionIndex];
                var Vertices = Instance.Mesh.VertexSpan.Slice(Section.FirstIndex,
                    Section.IndexCount);
                fixed (GpuSceneVertex* VertexPointer = Vertices)
                {
                    var Upload = new NativeGpuMeshUpload
                    {
                        Resource = Resource,
                        Vertices = VertexPointer,
                        VertexCount = checked((uint)Vertices.Length),
                        Skinned = Instance.Mesh.Skinned ? (byte)1 : (byte)0,
                    };
                    if (EnsureGpuMeshFunction(&Upload) == 0)
                    {
                        return false;
                    }
                }
            }
        }

        var Draws = new List<NativeGpuDraw>();
        var SkinMatrices = new List<Matrix4x4>();
        foreach (var Instance in Frame.Instances)
        {
            var Sections = Instance.Mesh.SectionSpan;
            for (var SectionIndex = 0; SectionIndex < Sections.Length; SectionIndex++)
            {
                var Section = Sections[SectionIndex];
                var SkinOffset = SkinMatrices.Count;
                if (Instance.Mesh.Skinned)
                {
                    foreach (var Bone in Section.BoneMap)
                    {
                        if (Bone < 0 || Bone >= Instance.SkinMatrices.Count)
                        {
                            return false;
                        }
                        SkinMatrices.Add(Instance.SkinMatrices[Bone]);
                    }
                }
                Draws.Add(new NativeGpuDraw
                {
                    Resource = ResourceId(Instance.Mesh, SectionIndex),
                    Model = Instance.Model,
                    Color = Instance.Color,
                    SkinMatrixOffset = checked((uint)SkinOffset),
                    SkinMatrixCount = checked((uint)(SkinMatrices.Count - SkinOffset)),
                    Flags = Instance.Selected ? 1u : 0u,
                });
            }
        }
        var Lights = Frame.Lights.Select(Light =>
            new NativeGpuLight(Light.Direction, Light.Radiance)).ToArray();
        var DrawArray = Draws.ToArray();
        var SkinArray = SkinMatrices.ToArray();
        fixed (NativeGpuLight* LightPointer = Lights)
        fixed (NativeGpuDraw* DrawPointer = DrawArray)
        fixed (Matrix4x4* SkinPointer = SkinArray)
        {
            var NativeFrame = new NativeGpuSceneFrame
            {
                ViewportX = Frame.Bounds.X,
                ViewportY = Frame.Bounds.Y,
                ViewportWidth = Frame.Bounds.Width,
                ViewportHeight = Frame.Bounds.Height,
                ClientHeight = ClientHeight,
                SkyRadiance = Frame.SkyRadiance,
                ViewProjection = Frame.ViewProjection,
                Lights = LightPointer,
                LightCount = checked((uint)Lights.Length),
                Draws = DrawPointer,
                DrawCount = checked((uint)DrawArray.Length),
                SkinMatrices = SkinPointer,
                SkinMatrixCount = checked((uint)SkinArray.Length),
            };
            return SubmitGpuSceneFunction(&NativeFrame) != 0;
        }
    }

    private static NativeGpuResourceId ResourceId(GpuMeshPayload Payload,
        int Section) => new(Payload.ResourceIdHigh, Payload.ResourceIdLow,
        checked((uint)Section));
}
