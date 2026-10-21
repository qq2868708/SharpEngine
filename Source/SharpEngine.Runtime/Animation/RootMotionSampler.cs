using System.Numerics;
using SharpEngine.Mathematics;

namespace SharpEngine.Animation;

/// <summary>Pure root-pose removal and continuous loop-delta sampling in mesh space.</summary>
public static class RootMotionSampler
{
    public static SkeletalPose RemoveRootMotion(SkeletalAnimationAsset Asset, SkeletalPose Pose)
    {
        ArgumentNullException.ThrowIfNull(Asset);
        ArgumentNullException.ThrowIfNull(Pose);
        var BoneId = Asset.RootMotion.BoneId;
        if (BoneId is null)
        {
            return Pose;
        }
        var Bind = Asset.Bones.Single(Bone => Bone.BoneId == BoneId).BindTransform;
        var Bones = Pose.Bones.Select(Bone => Bone.BoneId == BoneId
            ? (Bone.BoneId, Bone.Transform with
            {
                Position = Bind.Position,
                Rotation = Bind.Rotation,
            })
            : Bone).ToArray();
        return new(Pose.Time, Array.AsReadOnly(Bones));
    }

    public static Matrix4x4 Delta(SkeletalAnimationAsset Asset, double FromTime,
        double AdvanceSeconds, bool Loop)
    {
        ArgumentNullException.ThrowIfNull(Asset);
        if (!double.IsFinite(FromTime) || !double.IsFinite(AdvanceSeconds) ||
            FromTime < 0 || AdvanceSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AdvanceSeconds));
        }
        if (!Asset.RootMotion.Enabled || AdvanceSeconds == 0)
        {
            return Matrix4x4.Identity;
        }
        var BoneId = Asset.RootMotion.BoneId!;
        var Bind = Asset.Bones.Single(Bone => Bone.BoneId == BoneId).BindTransform;
        var BindMatrix = TransformMath.Trs(Bind.Position, Bind.Rotation, Bind.Scale);
        var BindInverse = Inverse(BindMatrix);
        Matrix4x4 Motion(double Time)
        {
            var Transform = Asset.SampleBone(BoneId, Time);
            return BindInverse * TransformMath.Trs(Transform.Position, Transform.Rotation, Bind.Scale);
        }
        var Start = Motion(0);
        var StartInverse = Inverse(Start);
        var Cycle = Loop && Motion(Asset.Duration) != Start
            ? Motion(Asset.Duration) * StartInverse
            : Matrix4x4.Identity;
        // Unfold the loop into one absolute curve so a frame that crosses a boundary composes the
        // remaining cycles and the new partial cycle in playback order.
        Matrix4x4 Unfolded(double Time)
        {
            if (!Loop)
            {
                var Clamped = Math.Min(Time, Asset.Duration);
                var MotionValue = Motion(Clamped);
                return MotionValue == Start ? Matrix4x4.Identity : MotionValue * StartInverse;
            }
            if (Time < Asset.Duration)
            {
                var MotionValue = Motion(Time);
                return MotionValue == Start ? Matrix4x4.Identity : MotionValue * StartInverse;
            }
            var CyclesValue = Math.Floor(Time / Asset.Duration);
            if (CyclesValue > long.MaxValue)
            {
                throw new InvalidDataException("Root motion advance exceeds the supported cycle count");
            }
            var Remainder = Time - (long)CyclesValue * Asset.Duration;
            var RemainderMotion = Motion(Remainder);
            var RemainderRelative = RemainderMotion == Start ? Matrix4x4.Identity : RemainderMotion * StartInverse;
            return RemainderRelative * Power(Cycle, (long)CyclesValue);
        }
        var Before = Unfolded(FromTime);
        var EndTime = FromTime + AdvanceSeconds;
        if (!double.IsFinite(EndTime))
        {
            throw new InvalidDataException("Root motion time must remain finite");
        }
        var After = Unfolded(EndTime);
        if (Before == After)
        {
            return Matrix4x4.Identity;
        }
        var Result = After * Inverse(Before);
        if (!TransformMath.IsFinite(Result))
        {
            throw new InvalidDataException("Root motion delta must remain finite");
        }
        return Result;
    }

    private static Matrix4x4 Power(Matrix4x4 Matrix, long Count)
    {
        var Result = Matrix4x4.Identity;
        var Factor = Matrix;
        while (Count > 0)
        {
            if ((Count & 1) != 0)
            {
                Result *= Factor;
            }
            Factor *= Factor;
            Count >>= 1;
        }
        return Result;
    }

    private static Matrix4x4 Inverse(Matrix4x4 Matrix)
    {
        var Invertible = TransformMath.TryInverse(Matrix, out var Result);
        if (!Invertible)
        {
            throw new InvalidDataException("Root motion matrices must be invertible");
        }
        return Result;
    }
}
