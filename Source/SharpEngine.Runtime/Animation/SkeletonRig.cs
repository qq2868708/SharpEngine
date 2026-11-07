using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Mathematics;
using SharpEngine.Playables;

namespace SharpEngine.Animation;

/// <summary>A named attachment frame, local to a real or virtual bone. IDs remain stable on rename.</summary>
public sealed record SkeletonSocket(string SocketId, string Name, string ParentBoneId,
    BoneTransform LocalTransform);

/// <summary>A derived bone parented to SourceBoneId whose model frame matches TargetBoneId.</summary>
public sealed record SkeletonVirtualBone(string BoneId, string Name, string SourceBoneId,
    string TargetBoneId);

public sealed partial class SkeletonAsset
{
    public IReadOnlyList<SkeletonSocket> Sockets
    {
        get;
    }

    public IReadOnlyList<SkeletonVirtualBone> VirtualBones
    {
        get;
    }

    private void ValidateRig()
    {
        var Ids = Bones.Select(Bone => Bone.BoneId).ToHashSet(StringComparer.Ordinal);
        var Names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Bone in VirtualBones)
        {
            ValidateIdentity(Bone.BoneId, Bone.Name, Ids, Names);
        }
        var References = Ids.ToHashSet(StringComparer.Ordinal);
        foreach (var Bone in VirtualBones)
        {
            if (!References.Contains(Bone.SourceBoneId) || !References.Contains(Bone.TargetBoneId))
            {
                throw new InvalidDataException($"SKELETON_RIG_REFERENCE: virtualBones/{Bone.BoneId} requires existing source and target bone IDs");
            }
        }
        var VirtualById = VirtualBones.ToDictionary(Bone => Bone.BoneId, StringComparer.Ordinal);
        var Visiting = new HashSet<string>(StringComparer.Ordinal);
        var Visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string Id)
        {
            if (Visited.Contains(Id) || !VirtualById.ContainsKey(Id))
            {
                return;
            }
            if (!Visiting.Add(Id))
            {
                throw new InvalidDataException($"SKELETON_RIG_CYCLE: virtualBones/{Id} contains a dependency cycle");
            }
            var Bone = VirtualById[Id];
            Visit(Bone.SourceBoneId);
            Visit(Bone.TargetBoneId);
            Visiting.Remove(Id);
            Visited.Add(Id);
        }
        foreach (var Bone in VirtualBones)
        {
            Visit(Bone.BoneId);
        }
        Names.Clear();
        foreach (var Socket in Sockets)
        {
            ValidateIdentity(Socket.SocketId, Socket.Name, Ids, Names);
            if (!References.Contains(Socket.ParentBoneId))
            {
                throw new InvalidDataException($"SKELETON_RIG_REFERENCE: sockets/{Socket.SocketId}/parentBoneId requires an existing bone ID");
            }
            ValidateTransform(Socket.LocalTransform, $"sockets/{Socket.SocketId}/localTransform");
        }
    }

    private static void ValidateIdentity(string Id, string Name, HashSet<string> Ids,
        HashSet<string> Names)
    {
        if (string.IsNullOrWhiteSpace(Id) || Id != Id.Trim() || !Ids.Add(Id) ||
            string.IsNullOrWhiteSpace(Name) || Name != Name.Trim() || !Names.Add(Name))
        {
            throw new InvalidDataException("SKELETON_RIG_IDENTITY: IDs must be unique across bones and sockets; names must be unique within each authored kind and non-empty");
        }
    }

    private static JsonObject SocketJson(SkeletonSocket Socket)
    {
        return new JsonObject
        {
            ["socketId"] = Socket.SocketId,
            ["name"] = Socket.Name,
            ["parentBoneId"] = Socket.ParentBoneId,
            ["position"] = PlayableJson.Vector3(Socket.LocalTransform.Position),
            ["rotation"] = PlayableJson.Quaternion(Socket.LocalTransform.Rotation),
            ["scale"] = PlayableJson.Vector3(Socket.LocalTransform.Scale),
        };
    }

    private static JsonObject VirtualBoneJson(SkeletonVirtualBone Bone)
    {
        return new JsonObject
        {
            ["boneId"] = Bone.BoneId,
            ["name"] = Bone.Name,
            ["sourceBoneId"] = Bone.SourceBoneId,
            ["targetBoneId"] = Bone.TargetBoneId,
        };
    }

    private static SkeletonSocket ParseSocket(JsonNode? Node)
    {
        var Value = Node?.AsObject() ?? throw new InvalidDataException("Socket must be an object");
        Exact(Value, "socketId", "name", "parentBoneId", "position", "rotation", "scale");
        return new SkeletonSocket(RequiredString(Value, "socketId"), RequiredString(Value, "name"),
            RequiredString(Value, "parentBoneId"), new BoneTransform(
                ParseVector3(Value["position"], "position"), ParseQuaternion(Value["rotation"], "rotation"),
                ParseVector3(Value["scale"], "scale")));
    }

    private static SkeletonVirtualBone ParseVirtualBone(JsonNode? Node)
    {
        var Value = Node?.AsObject() ?? throw new InvalidDataException("Virtual bone must be an object");
        Exact(Value, "boneId", "name", "sourceBoneId", "targetBoneId");
        return new SkeletonVirtualBone(RequiredString(Value, "boneId"), RequiredString(Value, "name"),
            RequiredString(Value, "sourceBoneId"), RequiredString(Value, "targetBoneId"));
    }
}

/// <summary>Per-evaluation, immutable model-space frames. Virtual bones never alter skinning indices.</summary>
public sealed class SkeletonRigPose
{
    public IReadOnlyDictionary<string, Matrix4x4> BoneModelMatrices
    {
        get;
    }

    public IReadOnlyDictionary<string, Matrix4x4> VirtualBoneLocalMatrices
    {
        get;
    }

    public IReadOnlyDictionary<string, Matrix4x4> SocketModelMatrices
    {
        get;
    }

    public SkeletonRigPose(SkeletonAsset Skeleton, SkeletalPose? Pose = null)
    {
        var Poses = Pose?.Bones.ToDictionary(Bone => Bone.BoneId, Bone => Bone.Transform, StringComparer.Ordinal);
        var Models = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        var Locals = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        var Bones = Skeleton.Bones.ToDictionary(Bone => Bone.BoneId, StringComparer.Ordinal);
        var Virtual = Skeleton.VirtualBones.ToDictionary(Bone => Bone.BoneId, StringComparer.Ordinal);
        Matrix4x4 Resolve(string Id)
        {
            var Found = Models.TryGetValue(Id, out var Existing);
            if (Found)
            {
                return Existing;
            }
            Matrix4x4 Model;
            if (Bones.ContainsKey(Id))
            {
                var Bone = Bones[Id];
                var Local = Poses?.GetValueOrDefault(Id, Bone.BindTransform) ?? Bone.BindTransform;
                var Parent = Bone.ParentBoneId is null ? Matrix4x4.Identity : Resolve(Bone.ParentBoneId);
                Model = TransformMath.Trs(Local.Position, Local.Rotation, Local.Scale) * Parent;
            }
            else
            {
                var Bone = Virtual[Id];
                var Source = Resolve(Bone.SourceBoneId);
                var Target = Resolve(Bone.TargetBoneId);
                var Invertible = TransformMath.TryInverse(Source, out var Inverse);
                if (!Invertible)
                {
                    throw new InvalidDataException($"SKELETON_RIG_TRANSFORM: virtual bone '{Id}' source is not invertible");
                }
                var Local = Target * Inverse;
                if (!TransformMath.IsFinite(Local))
                {
                    throw new InvalidDataException($"SKELETON_RIG_TRANSFORM: virtual bone '{Id}' local matrix is not finite");
                }
                Locals.Add(Id, Local);
                Model = Target;
            }
            if (!TransformMath.IsFinite(Model))
            {
                throw new InvalidDataException($"SKELETON_RIG_TRANSFORM: bone '{Id}' model matrix is not finite");
            }
            Models.Add(Id, Model);
            return Model;
        }
        foreach (var Id in Bones.Keys.Concat(Virtual.Keys))
        {
            Resolve(Id);
        }
        var Sockets = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        foreach (var Socket in Skeleton.Sockets)
        {
            var Transform = Socket.LocalTransform;
            var Model = TransformMath.Trs(Transform.Position, Transform.Rotation, Transform.Scale) * Models[Socket.ParentBoneId];
            if (!TransformMath.IsFinite(Model))
            {
                throw new InvalidDataException($"SKELETON_RIG_TRANSFORM: socket '{Socket.SocketId}' model matrix is not finite");
            }
            Sockets.Add(Socket.SocketId, Model);
        }
        BoneModelMatrices = new ReadOnlyDictionary<string, Matrix4x4>(Models);
        VirtualBoneLocalMatrices = new ReadOnlyDictionary<string, Matrix4x4>(Locals);
        SocketModelMatrices = new ReadOnlyDictionary<string, Matrix4x4>(Sockets);
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["boneModelMatrices"] = MatricesJson(BoneModelMatrices),
            ["virtualBoneLocalMatrices"] = MatricesJson(VirtualBoneLocalMatrices),
            ["socketModelMatrices"] = MatricesJson(SocketModelMatrices),
        };
    }

    private static JsonObject MatricesJson(IReadOnlyDictionary<string, Matrix4x4> Matrices)
    {
        var Result = new JsonObject();
        foreach (var Entry in Matrices.OrderBy(Entry => Entry.Key, StringComparer.Ordinal))
        {
            var Matrix = Entry.Value;
            Result[Entry.Key] = new JsonArray(Matrix.M11, Matrix.M12, Matrix.M13, Matrix.M14,
                Matrix.M21, Matrix.M22, Matrix.M23, Matrix.M24, Matrix.M31, Matrix.M32, Matrix.M33, Matrix.M34,
                Matrix.M41, Matrix.M42, Matrix.M43, Matrix.M44);
        }
        return Result;
    }
}
