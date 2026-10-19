using System.Numerics;

namespace SharpEngine.Mathematics;

public static class QuaternionMath
{
    public static Quaternion Normalize(Quaternion Value)
    {
        var Length = Math.Sqrt((double)Value.X * Value.X + (double)Value.Y * Value.Y + (double)Value.Z * Value.Z + (double)Value.W * Value.W);
        if (!double.IsFinite(Length) || Length == 0)
        {
            throw new ArgumentException("Rotation must be finite and nonzero.", nameof(Value));
        }
        return new((float)(Value.X / Length), (float)(Value.Y / Length), (float)(Value.Z / Length), (float)(Value.W / Length));
    }

    /// <summary>X pitch, Y yaw, Z roll, in degrees; matches CreateFromYawPitchRoll.</summary>
    public static Quaternion Euler(Vector3 Degrees)
    {
        if (!VectorMath.IsFinite(Degrees))
        {
            throw new ArgumentException("Euler angles must be finite.", nameof(Degrees));
        }
        return Quaternion.CreateFromYawPitchRoll(Degrees.Y * Mathf.Deg2Rad, Degrees.X * Mathf.Deg2Rad, Degrees.Z * Mathf.Deg2Rad);
    }

    /// <summary>Returns one equivalent Euler representation; roll is zero at a pitch pole.</summary>
    public static Vector3 ToEuler(Quaternion Rotation)
    {
        var Unit = Normalize(Rotation);
        var X = (double)Unit.X;
        var Y = (double)Unit.Y;
        var Z = (double)Unit.Z;
        var W = (double)Unit.W;
        var PitchSine = Math.Clamp(2 * (W * X - Y * Z), -1, 1);
        if (Math.Abs(PitchSine) > 1 - 1e-6)
        {
            return new((float)Math.CopySign(90, PitchSine), (float)(Math.Atan2(2 * (W * Y - X * Z), 1 - 2 * (Y * Y + Z * Z)) * Mathf.Rad2Deg), 0);
        }
        return new((float)(Math.Asin(PitchSine) * Mathf.Rad2Deg),
            (float)(Math.Atan2(2 * (W * Y + X * Z), 1 - 2 * (X * X + Y * Y)) * Mathf.Rad2Deg),
            (float)(Math.Atan2(2 * (W * Z + X * Y), 1 - 2 * (X * X + Z * Z)) * Mathf.Rad2Deg));
    }

    public static float Angle(Quaternion From, Quaternion To)
    {
        var First = Normalize(From);
        var Second = Normalize(To);
        var FirstLength = (double)First.X * First.X + (double)First.Y * First.Y + (double)First.Z * First.Z + (double)First.W * First.W;
        var SecondLength = (double)Second.X * Second.X + (double)Second.Y * Second.Y + (double)Second.Z * Second.Z + (double)Second.W * Second.W;
        var Dot = Math.Abs((double)First.X * Second.X + (double)First.Y * Second.Y + (double)First.Z * Second.Z + (double)First.W * Second.W) /
            Math.Sqrt(FirstLength * SecondLength);
        return (float)(2 * Math.Acos(Math.Clamp(Dot, 0, 1)) * Mathf.Rad2Deg);
    }

    public static Quaternion RotateTowards(Quaternion From, Quaternion To, float MaxDegrees)
    {
        if (!float.IsFinite(MaxDegrees) || MaxDegrees < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDegrees));
        }
        var AngleValue = Angle(From, To);
        return Quaternion.Slerp(Normalize(From), Normalize(To), AngleValue == 0 ? 1 : Math.Min(1, MaxDegrees / AngleValue));
    }
}
