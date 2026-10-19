using System.Numerics;

namespace SharpEngine.Mathematics;

public static class VectorMath
{
    public static bool IsFinite(Vector3 Value) { return float.IsFinite(Value.X) && float.IsFinite(Value.Y) && float.IsFinite(Value.Z); }
    public static double Length(Vector3 Value) { return Math.Sqrt((double)Value.X * Value.X + (double)Value.Y * Value.Y + (double)Value.Z * Value.Z); }

    public static Vector3 NormalizeOrZero(Vector3 Value)
    {
        if (!IsFinite(Value))
        {
            throw new ArgumentException("Vector must be finite.", nameof(Value));
        }
        var Magnitude = Length(Value);
        return Magnitude == 0 ? Vector3.Zero : new((float)(Value.X / Magnitude), (float)(Value.Y / Magnitude), (float)(Value.Z / Magnitude));
    }

    public static Vector3 Project(Vector3 Value, Vector3 Normal)
    {
        var Unit = NormalizeOrZero(Normal);
        var Distance = (double)Value.X * Unit.X + (double)Value.Y * Unit.Y + (double)Value.Z * Unit.Z;
        return new((float)(Distance * Unit.X), (float)(Distance * Unit.Y), (float)(Distance * Unit.Z));
    }

    public static Vector3 ProjectOnPlane(Vector3 Value, Vector3 Normal) { return Value - Project(Value, Normal); }

    public static float Angle(Vector3 From, Vector3 To)
    {
        var First = NormalizeOrZero(From);
        var Second = NormalizeOrZero(To);
        if (First == Vector3.Zero || Second == Vector3.Zero)
        {
            return 0;
        }
        var Dot = ((double)First.X * Second.X + (double)First.Y * Second.Y + (double)First.Z * Second.Z) /
            (Length(First) * Length(Second));
        return (float)(Math.Acos(Math.Clamp(Dot, -1, 1)) * Mathf.Rad2Deg);
    }

    public static Vector3 MoveTowards(Vector3 Current, Vector3 Target, float MaxDelta)
    {
        if (!IsFinite(Current) || !IsFinite(Target) || !float.IsFinite(MaxDelta) || MaxDelta < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDelta));
        }
        var X = (double)Target.X - Current.X;
        var Y = (double)Target.Y - Current.Y;
        var Z = (double)Target.Z - Current.Z;
        var Distance = Math.Sqrt(X * X + Y * Y + Z * Z);
        if (Distance <= MaxDelta)
        {
            return Target;
        }
        var Amount = MaxDelta / Distance;
        return new((float)(Current.X + X * Amount), (float)(Current.Y + Y * Amount), (float)(Current.Z + Z * Amount));
    }
}
