using System.Numerics;

namespace SharpEngine.Mathematics;

/// <summary>System.Numerics row vectors: local = Scale * Rotation * Translation; world = local * parent.</summary>
public static class TransformMath
{
    public static Matrix4x4 Trs(Vector3 Position, Quaternion Rotation, Vector3 Scale)
    {
        if (!VectorMath.IsFinite(Position) || !VectorMath.IsFinite(Scale))
        {
            throw new ArgumentException("TRS values must be finite.");
        }
        return Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(QuaternionMath.Normalize(Rotation)) * Matrix4x4.CreateTranslation(Position);
    }

    public static Matrix4x4 Trs(Vector3 Position, Vector3 EulerDegrees, Vector3 Scale) { return Trs(Position, QuaternionMath.Euler(EulerDegrees), Scale); }

    public static bool IsFinite(Matrix4x4 Matrix)
    {
        return float.IsFinite(Matrix.M11) && float.IsFinite(Matrix.M12) && float.IsFinite(Matrix.M13) && float.IsFinite(Matrix.M14) &&
            float.IsFinite(Matrix.M21) && float.IsFinite(Matrix.M22) && float.IsFinite(Matrix.M23) && float.IsFinite(Matrix.M24) &&
            float.IsFinite(Matrix.M31) && float.IsFinite(Matrix.M32) && float.IsFinite(Matrix.M33) && float.IsFinite(Matrix.M34) &&
            float.IsFinite(Matrix.M41) && float.IsFinite(Matrix.M42) && float.IsFinite(Matrix.M43) && float.IsFinite(Matrix.M44);
    }

    public static bool IsAffine(Matrix4x4 Matrix) { return IsFinite(Matrix) && Matrix.M14 == 0 && Matrix.M24 == 0 && Matrix.M34 == 0 && Matrix.M44 == 1; }

    public static bool TryInverse(Matrix4x4 Matrix, out Matrix4x4 Inverse)
    {
        Inverse = default;
        var Success = IsFinite(Matrix) && Matrix4x4.Invert(Matrix, out Inverse) && IsFinite(Inverse);
        if (!Success)
        {
            Inverse = default;
        }
        return Success;
    }

    /// <summary>Rejects perspective, singular transforms, and shear that cannot be represented as TRS.</summary>
    public static bool TryDecompose(Matrix4x4 Matrix, out Vector3 Position, out Quaternion Rotation, out Vector3 Scale)
    {
        Position = default;
        Rotation = Quaternion.Identity;
        Scale = default;
        var Success = IsAffine(Matrix) && Matrix4x4.Decompose(Matrix, out Scale, out Rotation, out Position);
        if (!Success || !VectorMath.IsFinite(Scale) || Scale.X == 0 || Scale.Y == 0 || Scale.Z == 0)
        {
            return false;
        }
        var Rebuilt = Trs(Position, Rotation, Scale);
        // Compare each basis independently so a large translation cannot hide shear.
        return Near(new(Matrix.M11, Matrix.M12, Matrix.M13), new(Rebuilt.M11, Rebuilt.M12, Rebuilt.M13)) &&
            Near(new(Matrix.M21, Matrix.M22, Matrix.M23), new(Rebuilt.M21, Rebuilt.M22, Rebuilt.M23)) &&
            Near(new(Matrix.M31, Matrix.M32, Matrix.M33), new(Rebuilt.M31, Rebuilt.M32, Rebuilt.M33));
    }

    private static bool Near(Vector3 First, Vector3 Second) { return VectorMath.Length(First - Second) <= 1e-5 * VectorMath.Length(First); }

    public static Vector3 TransformNormal(Vector3 Normal, Matrix4x4 Matrix)
    {
        var Success = TryInverse(Matrix, out var Inverse) && IsAffine(Matrix);
        if (!Success)
        {
            throw new ArgumentException("Normal transformation requires an invertible affine matrix.", nameof(Matrix));
        }
        return VectorMath.NormalizeOrZero(Vector3.TransformNormal(Normal, Matrix4x4.Transpose(Inverse)));
    }
}
