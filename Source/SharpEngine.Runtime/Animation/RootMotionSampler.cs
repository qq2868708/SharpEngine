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
        var StartTime = Loop ? FromTime % Asset.Duration : Math.Min(FromTime, Asset.Duration);
        var EndTime = StartTime + AdvanceSeconds;
        if (!double.IsFinite(EndTime))
        {
            throw new InvalidDataException("Root motion time must remain finite");
        }
        var BeforeMotion = Motion(StartTime);
        var Before = BeforeMotion * StartInverse;
        Matrix4x4 After;
        if (Loop)
        {
            var CyclesValue = Math.Floor(EndTime / Asset.Duration);
            if (CyclesValue > long.MaxValue)
            {
                throw new InvalidDataException("Root motion advance exceeds the supported cycle count");
            }
            var EndMotion = Motion(EndTime % Asset.Duration);
            var CycleMotion = Motion(Asset.Duration);
            if (BeforeMotion == EndMotion && (CyclesValue == 0 || CycleMotion == Start))
            {
                return Matrix4x4.Identity;
            }
            var Cycle = CycleMotion == Start ? Matrix4x4.Identity : CycleMotion * StartInverse;
            After = EndMotion * StartInverse *
                Power(Cycle, (long)CyclesValue);
        }
        else
        {
            var EndMotion = Motion(Math.Min(EndTime, Asset.Duration));
            if (BeforeMotion == EndMotion)
            {
                return Matrix4x4.Identity;
            }
            After = EndMotion * StartInverse;
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
