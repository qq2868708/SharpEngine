using System.Numerics;

namespace SharpEngine.Animation;

// Shared checks for embedded animation/mesh bindings against their canonical skeleton.
internal static class SkeletonBinding
{
    internal static void Validate(
        IReadOnlyList<SkeletonBone> Expected,
        IReadOnlyList<SkeletonBone> Actual,
        string Context,
        bool RequireAll = false)
    {
        if (RequireAll && Expected.Count != Actual.Count)
        {
            throw new InvalidDataException($"{Context} must contain the same bone IDs");
        }
        var ById = Expected.ToDictionary(Bone => Bone.BoneId, StringComparer.Ordinal);
        foreach (var Bone in Actual)
        {
            var Found = ById.TryGetValue(Bone.BoneId, out var Reference);
            if (!Found || Reference!.ParentBoneId != Bone.ParentBoneId ||
                !Near(Reference.BindTransform, Bone.BindTransform))
            {
                throw new InvalidDataException(
                    $"{Context}: bone '{Bone.BoneId}' has an incompatible hierarchy or bind transform");
            }
        }
    }

    private static bool Near(BoneTransform First, BoneTransform Second)
    {
        var Dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(First.Rotation), Quaternion.Normalize(Second.Rotation)));
        return Near(First.Position, Second.Position) && Near(First.Scale, Second.Scale) &&
            float.IsFinite(Dot) && Dot >= 1 - 1e-7f;
    }

    private static bool Near(Vector3 First, Vector3 Second)
    {
        return Near(First.X, Second.X) && Near(First.Y, Second.Y) && Near(First.Z, Second.Z);
    }

    private static bool Near(float First, float Second)
    {
        return MathF.Abs(First - Second) <= 1e-5f + 1e-5f * MathF.Max(MathF.Abs(First), MathF.Abs(Second));
    }
}
