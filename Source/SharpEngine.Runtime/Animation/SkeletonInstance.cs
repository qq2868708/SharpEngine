using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Mathematics;

namespace SharpEngine.Animation;

/// <summary>Per-model runtime skeleton. Source mesh and skeleton assets are never modified.</summary>
public sealed class SkeletonInstance
{
    private readonly SkeletalMeshAsset Mesh;
    private readonly Matrix4x4[] Bind;
    private readonly int[] Parents;

    public SkeletonInstance(SkeletalMeshAsset Mesh)
    {
        this.Mesh = Mesh;
        Bind = new Matrix4x4[Mesh.Bones.Count];
        Parents = Mesh.Bones.Select(Bone => Bone.ParentBoneId is null ? -1 :
            Mesh.Bones.ToList().FindIndex(Parent => Parent.BoneId == Bone.ParentBoneId)).ToArray();
        BuildMatrices(Mesh.Bones.Select(Bone => Bone.BindTransform).ToArray(), Bind);
    }

    private void BuildMatrices(IReadOnlyList<BoneTransform> Local, Matrix4x4[] Matrices)
    {
        var Ready = new bool[Local.Count];
        void Build(int Index)
        {
            if (Ready[Index])
            {
                return;
            }
            var Parent = Parents[Index];
            if (Parent >= 0)
            {
                Build(Parent);
            }
            var Transform = Local[Index];
            Matrices[Index] = TransformMath.Trs(Transform.Position, Transform.Rotation, Transform.Scale) * (Parent < 0 ? Matrix4x4.Identity : Matrices[Parent]);
            if (!TransformMath.IsFinite(Matrices[Index]))
            {
                throw new InvalidDataException("Skeleton model matrices must remain finite.");
            }
            Ready[Index] = true;
        }
        for (var Index = 0; Index < Local.Count; Index++)
        {
            Build(Index);
        }
    }

    public SkeletonFrameState Prepare(string EntityId, string ComponentId, SkeletalPose? Pose, string Code, string? Message = null)
    {
        var Poses = Pose?.Bones.ToDictionary(Bone => Bone.BoneId, Bone => Bone.Transform, StringComparer.Ordinal);
        var Local = Mesh.Bones.Select(Bone => Poses?.GetValueOrDefault(Bone.BoneId, Bone.BindTransform) ?? Bone.BindTransform).ToArray();
        var Model = new Matrix4x4[Local.Length];
        BuildMatrices(Local, Model);
        var Skin = new Matrix4x4[Local.Length];
        for (var Index = 0; Index < Skin.Length; Index++)
        {
            var Invertible = TransformMath.TryInverse(Bind[Index], out var Inverse);
            if (!Invertible)
            {
                throw new InvalidDataException("Skeleton bind matrices must be invertible.");
            }
            Skin[Index] = Inverse * Model[Index];
        }
        var LocalPose = new SkeletalPose(Pose?.Time ?? 0, Array.AsReadOnly(Mesh.Bones.Select((Bone, Index) => (Bone.BoneId, Local[Index])).ToArray()));
        return new(EntityId, ComponentId, Mesh.AssetId, Code, Message, LocalPose,
            Model, Skin, () => SkinPositions(Mesh, Skin));
    }

    private static IReadOnlyList<Vector3> SkinPositions(SkeletalMeshAsset Mesh,
        IReadOnlyList<Matrix4x4> Skin)
    {
        var Positions = new Vector3[Mesh.Positions.Count];
        for (var Index = 0; Index < Positions.Length; Index++)
        {
            var Position = Mesh.Positions[Index];
            var Original = new Vector3(Position.X, Position.Y, Position.Z);
            var Influence = Mesh.Influences[Index];
            var Total = 0f;
            var Skinned = Vector3.Zero;
            for (var Item = 0; Item < Influence.BoneIndices.Count; Item++)
            {
                var Weight = Influence.Weights[Item];
                Skinned += Vector3.Transform(Original,
                    Skin[Influence.BoneIndices[Item]]) * Weight;
                Total += Weight;
            }
            Positions[Index] = Total > 0 ? Skinned / Total : Original;
            if (!VectorMath.IsFinite(Positions[Index]))
            {
                throw new InvalidDataException(
                    "Skinned positions must remain finite.");
            }
        }
        return Array.AsReadOnly(Positions);
    }
}

/// <summary>Frozen render/inspection output for one committed component revision.</summary>
public sealed class SkeletonFrameState : ComponentFrameState
{
    private readonly JsonObject State;
    private readonly Lazy<IReadOnlyList<Vector3>> PositionData;
    public string? MeshId
    {
        get;
    }
    public IReadOnlyList<Vector3> Positions
    {
        get
        {
            return PositionData.Value;
        }
    }
    public IReadOnlyList<Matrix4x4> SkinMatrices
    {
        get;
    }
    public string Code
    {
        get;
    }
    public string? Message
    {
        get;
    }

    internal SkeletonFrameState(string EntityId, string ComponentId, string? MeshId, string Code, string? Message,
        SkeletalPose? LocalPose = null, Matrix4x4[]? Model = null,
        Matrix4x4[]? Skin = null,
        Func<IReadOnlyList<Vector3>>? PositionFactory = null)
    {
        this.MeshId = MeshId;
        this.Code = Code;
        this.Message = Message;
        PositionData = new Lazy<IReadOnlyList<Vector3>>(
            PositionFactory ?? (() => Array.Empty<Vector3>()));
        SkinMatrices = Array.AsReadOnly(Skin?.ToArray() ?? Array.Empty<Matrix4x4>());
        State = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["entityId"] = EntityId,
            ["componentId"] = ComponentId,
            ["meshId"] = MeshId,
            ["code"] = Code,
            ["message"] = Message,
            ["localPose"] = LocalPose?.ToJson(),
            ["modelMatrices"] = new JsonArray((Model ?? Array.Empty<Matrix4x4>()).Select(MatrixJson).ToArray()),
            ["skinMatrices"] = new JsonArray((Skin ?? Array.Empty<Matrix4x4>()).Select(MatrixJson).ToArray()),
        };
    }

    private static JsonNode MatrixJson(Matrix4x4 Matrix)
    {
        return new JsonArray(Matrix.M11, Matrix.M12, Matrix.M13, Matrix.M14,
            Matrix.M21, Matrix.M22, Matrix.M23, Matrix.M24, Matrix.M31, Matrix.M32, Matrix.M33, Matrix.M34,
            Matrix.M41, Matrix.M42, Matrix.M43, Matrix.M44);
    }
    public override JsonObject ToJson() { return (JsonObject)State.DeepClone(); }
}
