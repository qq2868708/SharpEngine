namespace SharpEngine.Mathematics;

/// <summary>Scalar helpers. Angles are degrees unless a name explicitly says otherwise.</summary>
public static class Mathf
{
    public const float Pi = MathF.PI;
    public const float Tau = MathF.Tau;
    public const float Deg2Rad = MathF.PI / 180;
    public const float Rad2Deg = 180 / MathF.PI;

    public static float Clamp01(float Value) { return Math.Clamp(Value, 0, 1); }
    public static float Lerp(float From, float To, float Amount) { return LerpUnclamped(From, To, Clamp01(Amount)); }
    public static float LerpUnclamped(float From, float To, float Amount) { return (float)((1.0 - Amount) * From + (double)Amount * To); }
    public static float InverseLerp(float From, float To, float Value) { return From == To ? 0 : Clamp01((float)(((double)Value - From) / ((double)To - From))); }

    public static float Repeat(float Value, float Length)
    {
        if (!float.IsFinite(Value) || !float.IsFinite(Length) || Length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Length), "Repeat requires a finite value and positive finite length.");
        }
        var Result = Value % Length;
        return Result < 0 ? Result + Length : Result;
    }

    public static float DeltaAngle(float From, float To)
    {
        var Delta = Repeat(Repeat(To, 360) - Repeat(From, 360), 360);
        return Delta > 180 ? Delta - 360 : Delta;
    }

    public static float LerpAngle(float From, float To, float Amount) { return From + DeltaAngle(From, To) * Clamp01(Amount); }

    public static float MoveTowards(float Current, float Target, float MaxDelta)
    {
        if (!float.IsFinite(MaxDelta) || MaxDelta < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDelta));
        }
        var Delta = (double)Target - Current;
        return Math.Abs(Delta) <= MaxDelta ? Target : (float)(Current + Math.CopySign(MaxDelta, Delta));
    }

    public static float SmoothStep(float From, float To, float Amount)
    {
        var Value = Clamp01(Amount);
        return LerpUnclamped(From, To, Value * Value * (3 - 2 * Value));
    }

    public static bool Approximately(float First, float Second, float AbsoluteTolerance = 1e-6f, float RelativeTolerance = 1e-6f)
    {
        if (AbsoluteTolerance < 0 || RelativeTolerance < 0 || !float.IsFinite(AbsoluteTolerance) || !float.IsFinite(RelativeTolerance))
        {
            throw new ArgumentOutOfRangeException(nameof(AbsoluteTolerance));
        }
        return float.IsFinite(First) && float.IsFinite(Second) &&
            Math.Abs((double)First - Second) <= Math.Max(AbsoluteTolerance, RelativeTolerance * Math.Max(Math.Abs((double)First), Math.Abs((double)Second)));
    }

    /// <summary>Exponential decay, independent of frame subdivision for a stationary target.</summary>
    public static float Damp(float Current, float Target, float Rate, float DeltaSeconds)
    {
        if (!float.IsFinite(Rate) || !float.IsFinite(DeltaSeconds) || Rate < 0 || DeltaSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Rate));
        }
        return LerpUnclamped(Current, Target, (float)(1 - Math.Exp(-(double)Rate * DeltaSeconds)));
    }
}
