using System.Numerics;
using SharpEngine.Assets;
using SharpEngine.Mathematics;

namespace SharpEngine.Animation;

/// <summary>Converts an animation authored for one skeleton into another explicit skeleton mapping.</summary>
public static class SkeletalAnimationRetargeter
{
    /// <summary>Loads registered assets and creates a new target-bound animation.</summary>
    public static SkeletalAnimationAsset Retarget(
        AssetRegistry Registry,
        string AnimationId,
        string SourceSkeletonId,
        string TargetSkeletonId,
        IReadOnlyDictionary<string, string> BoneMap,
        string AssetId,
        string Name)
    {
        ArgumentNullException.ThrowIfNull(Registry);
        return Retarget(
            SkeletalAnimationContent.Load(Registry, AnimationId),
            SkeletonContent.Load(Registry, SourceSkeletonId),
            SkeletonContent.Load(Registry, TargetSkeletonId),
            BoneMap,
            AssetId,
            Name);
    }

    /// <summary>
    /// Retargets the source animation into a new asset bound to the target
    /// skeleton. The source animation and skeleton are never modified.
    /// </summary>
    public static SkeletalAnimationAsset Retarget(
        SkeletalAnimationAsset SourceAnimation,
        SkeletonAsset SourceSkeleton,
        SkeletonAsset TargetSkeleton,
        IReadOnlyDictionary<string, string> BoneMap,
        string AssetId,
        string Name)
    {
        ArgumentNullException.ThrowIfNull(SourceAnimation);
        ArgumentNullException.ThrowIfNull(SourceSkeleton);
        ArgumentNullException.ThrowIfNull(TargetSkeleton);
        ArgumentNullException.ThrowIfNull(BoneMap);
        ValidateSource(SourceAnimation, SourceSkeleton);
        var SourceBones = SourceSkeleton.Bones.ToDictionary(Bone => Bone.BoneId, StringComparer.Ordinal);
        var TargetBones = TargetSkeleton.Bones.ToDictionary(Bone => Bone.BoneId, StringComparer.Ordinal);
        ValidateMap(BoneMap, SourceBones, TargetBones, SourceAnimation);

        var SourceBindWorld = BuildWorldMatrices(SourceSkeleton.Bones, Bone => Bone.BindTransform);
        var TargetBindWorld = BuildWorldMatrices(TargetSkeleton.Bones, Bone => Bone.BindTransform);
        var TargetBoneIndices = TargetSkeleton.Bones
            .Select((Bone, Index) => (Bone.BoneId, Index))
            .ToDictionary(Value => Value.BoneId, Value => Value.Index, StringComparer.Ordinal);
        var SourceTimes = SourceAnimation.Tracks
            .SelectMany(Track => Track.Keyframes.Select(Keyframe => Keyframe.Time))
            .Append(0)
            .Append(SourceAnimation.Duration)
            .Distinct()
            .OrderBy(Time => Time)
            .ToArray();
        var TargetTracks = SourceAnimation.Tracks
            .Select(Track => new { Track, TargetBoneId = BoneMap[Track.BoneId] })
            .Select(Item => new BoneTrack(
                Item.TargetBoneId + "-retarget-track",
                Item.TargetBoneId,
                SourceTimes.Select(Time => BuildKeyframe(
                    Time,
                    SourceAnimation,
                    SourceSkeleton,
                    TargetSkeleton,
                    BoneMap,
                    SourceBindWorld,
                    TargetBindWorld,
                    TargetBoneIndices,
                    Item.Track.BoneId)).ToArray()))
            .ToArray();

        return new SkeletalAnimationAsset(
            AssetId,
            Name,
            SourceAnimation.Duration,
            SourceAnimation.SampleRate,
            TargetSkeleton.Bones,
            TargetTracks,
            TargetSkeleton.AssetId,
            null,
            null,
            SourceAnimation.Notifies);
    }

    private static BoneKeyframe BuildKeyframe(
        double Time,
        SkeletalAnimationAsset SourceAnimation,
        SkeletonAsset SourceSkeleton,
        SkeletonAsset TargetSkeleton,
        IReadOnlyDictionary<string, string> BoneMap,
        IReadOnlyDictionary<string, Matrix4x4> SourceBindWorld,
        IReadOnlyDictionary<string, Matrix4x4> TargetBindWorld,
        IReadOnlyDictionary<string, int> TargetBoneIndices,
        string SourceBoneId)
    {
        var SourcePose = SourceAnimation.Sample(Time).Bones
            .ToDictionary(Bone => Bone.BoneId, Bone => Bone.Transform, StringComparer.Ordinal);
        var SourceAnimatedWorld = BuildWorldMatrices(SourceSkeleton.Bones,
            Bone => SourcePose.GetValueOrDefault(Bone.BoneId, Bone.BindTransform));
        var TargetAnimatedWorld = TargetSkeleton.Bones.ToDictionary(
            Bone => Bone.BoneId,
            Bone => TargetBindWorld[Bone.BoneId]);
        foreach (var Pair in BoneMap)
        {
            var SourceDelta = Inverse(SourceBindWorld[Pair.Key], $"source bone '{Pair.Key}'") *
                SourceAnimatedWorld[Pair.Key];
            TargetAnimatedWorld[Pair.Value] = TargetBindWorld[Pair.Value] * SourceDelta;
        }

        var TargetBone = TargetSkeleton.Bones[TargetBoneIndices[BoneMap[SourceBoneId]]];
        var TargetParentWorld = TargetBone.ParentBoneId is null
            ? Matrix4x4.Identity
            : TargetAnimatedWorld[TargetBone.ParentBoneId];
        var TargetLocal = TargetAnimatedWorld[TargetBone.BoneId] *
            Inverse(TargetParentWorld, $"target bone '{TargetBone.BoneId}' parent");
        if (!TryDecompose(TargetLocal, out var Position, out var Rotation, out var Scale))
        {
            throw new InvalidDataException(
                $"Retargeted bone '{TargetBone.BoneId}' produced a transform that cannot be represented as TRS");
        }
        return new BoneKeyframe(Time, new BoneTransform(Position, Rotation, Scale));
    }

    private static IReadOnlyDictionary<string, Matrix4x4> BuildWorldMatrices(
        IReadOnlyList<SkeletonBone> Bones,
        Func<SkeletonBone, BoneTransform> LocalTransform)
    {
        var ById = Bones.ToDictionary(Bone => Bone.BoneId, StringComparer.Ordinal);
        var Result = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        Matrix4x4 Build(SkeletonBone Bone)
        {
            if (Result.TryGetValue(Bone.BoneId, out var Existing))
            {
                return Existing;
            }
            var Local = LocalTransform(Bone);
            var Parent = Bone.ParentBoneId is null
                ? Matrix4x4.Identity
                : Build(ById[Bone.ParentBoneId]);
            var World = TransformMath.Trs(Local.Position, Local.Rotation, Local.Scale) * Parent;
            if (!TransformMath.IsFinite(World))
            {
                throw new InvalidDataException($"Bone '{Bone.BoneId}' produced a non-finite retarget transform");
            }
            Result.Add(Bone.BoneId, World);
            return World;
        }

        foreach (var Bone in Bones)
        {
            Build(Bone);
        }
        return Result;
    }

    private static Matrix4x4 Inverse(Matrix4x4 Matrix, string Description)
    {
        if (!TransformMath.TryInverse(Matrix, out var Result))
        {
            throw new InvalidDataException($"Retarget transform '{Description}' is not invertible");
        }
        return Result;
    }

    private static bool TryDecompose(
        Matrix4x4 Matrix,
        out Vector3 Position,
        out Quaternion Rotation,
        out Vector3 Scale)
    {
        Position = default;
        Rotation = Quaternion.Identity;
        Scale = default;
        if (!TransformMath.IsFinite(Matrix) ||
            MathF.Abs(Matrix.M14) > 1e-4f || MathF.Abs(Matrix.M24) > 1e-4f ||
            MathF.Abs(Matrix.M34) > 1e-4f || MathF.Abs(Matrix.M44 - 1) > 1e-4f ||
            !Matrix4x4.Decompose(Matrix, out Scale, out Rotation, out Position) ||
            !VectorMath.IsFinite(Position) || !VectorMath.IsFinite(Scale) ||
            Scale.X == 0 || Scale.Y == 0 || Scale.Z == 0 ||
            !VectorMath.IsFinite(new Vector3(Rotation.X, Rotation.Y, Rotation.Z)) ||
            !float.IsFinite(Rotation.W))
        {
            return false;
        }
        var Rebuilt = TransformMath.Trs(Position, Rotation, Scale);
        return NearMatrix(Matrix, Rebuilt);
    }

    private static bool NearMatrix(Matrix4x4 First, Matrix4x4 Second)
    {
        var FirstValues = new[]
        {
            First.M11, First.M12, First.M13, First.M21, First.M22, First.M23,
            First.M31, First.M32, First.M33, First.M41, First.M42, First.M43,
        };
        var SecondValues = new[]
        {
            Second.M11, Second.M12, Second.M13, Second.M21, Second.M22, Second.M23,
            Second.M31, Second.M32, Second.M33, Second.M41, Second.M42, Second.M43,
        };
        return FirstValues.Zip(SecondValues, (Left, Right) =>
            MathF.Abs(Left - Right) <= 1e-4f + 1e-4f * MathF.Abs(Left)).All(Equal => Equal);
    }

    private static void ValidateSource(SkeletalAnimationAsset Animation, SkeletonAsset Skeleton)
    {
        if (Animation.SkeletonId is not null &&
            !string.Equals(Animation.SkeletonId, Skeleton.AssetId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Animation '{Animation.AssetId}' does not belong to source skeleton '{Skeleton.AssetId}'");
        }
        var SkeletonBones = Skeleton.Bones.ToDictionary(Bone => Bone.BoneId, StringComparer.Ordinal);
        var AnimationBones = Animation.Bones.ToDictionary(Bone => Bone.BoneId, StringComparer.Ordinal);
        if (SkeletonBones.Count != AnimationBones.Count ||
            SkeletonBones.Keys.Any(BoneId => !AnimationBones.ContainsKey(BoneId)))
        {
            throw new InvalidDataException("Source animation and skeleton must contain the same bone IDs");
        }
        foreach (var Bone in Skeleton.Bones)
        {
            var AnimationBone = AnimationBones[Bone.BoneId];
            if (!string.Equals(Bone.ParentBoneId, AnimationBone.ParentBoneId, StringComparison.Ordinal) ||
                !Near(Bone.BindTransform, AnimationBone.BindTransform))
            {
                throw new InvalidDataException(
                    $"Source animation bone '{Bone.BoneId}' does not match the source skeleton binding");
            }
        }
    }

    private static void ValidateMap(
        IReadOnlyDictionary<string, string> BoneMap,
        IReadOnlyDictionary<string, SkeletonBone> SourceBones,
        IReadOnlyDictionary<string, SkeletonBone> TargetBones,
        SkeletalAnimationAsset SourceAnimation)
    {
        if (BoneMap.Count == 0)
        {
            throw new InvalidDataException("A retarget map must contain at least one bone");
        }
        var TargetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Pair in BoneMap)
        {
            if (!SourceBones.ContainsKey(Pair.Key))
            {
                throw new InvalidDataException($"Retarget map references missing source bone '{Pair.Key}'");
            }
            if (!TargetBones.ContainsKey(Pair.Value))
            {
                throw new InvalidDataException($"Retarget map references missing target bone '{Pair.Value}'");
            }
            if (!TargetIds.Add(Pair.Value))
            {
                throw new InvalidDataException($"Retarget map maps multiple source bones to target bone '{Pair.Value}'");
            }
        }
        foreach (var Track in SourceAnimation.Tracks)
        {
            if (!BoneMap.ContainsKey(Track.BoneId))
            {
                throw new InvalidDataException(
                    $"Retarget map is missing animated source bone '{Track.BoneId}'");
            }
        }
    }

    private static bool Near(BoneTransform First, BoneTransform Second) =>
        Near(First.Position, Second.Position) && Near(First.Scale, Second.Scale) &&
        NearRotation(First.Rotation, Second.Rotation);

    private static bool Near(Vector3 First, Vector3 Second) =>
        Vector3.Distance(First, Second) <= 1e-5f;

    private static bool NearRotation(Quaternion First, Quaternion Second)
    {
        var Left = Quaternion.Normalize(First);
        var Right = Quaternion.Normalize(Second);
        var Dot = MathF.Abs(Quaternion.Dot(Left, Right));
        return float.IsFinite(Dot) && Dot >= 1 - 1e-5f;
    }
}
