using System.Numerics;

namespace SharpEngine.Rendering;

/// <summary>An immutable observation camera, independent of any World camera entity.</summary>
public sealed record Camera3D(Vector3 Position, Quaternion Rotation,
    float FieldOfView = 35, float NearPlane = 0.01f, float FarPlane = 10000)
{
    public Vector3 Right
    {
        get
        {
            return Vector3.Transform(Vector3.UnitX, Rotation);
        }
    }
    public Vector3 Up
    {
        get
        {
            return Vector3.Transform(Vector3.UnitY, Rotation);
        }
    }
    public Vector3 Forward
    {
        get
        {
            return Vector3.Transform(-Vector3.UnitZ, Rotation);
        }
    }

    public Matrix4x4 ViewProjection(float Aspect)
    {
        if (!float.IsFinite(Aspect) || Aspect <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Aspect));
        }
        var RotationLength = Rotation.LengthSquared();
        if (!float.IsFinite(Position.X) || !float.IsFinite(Position.Y) || !float.IsFinite(Position.Z) ||
            !float.IsFinite(RotationLength) || MathF.Abs(RotationLength - 1) > 0.001f ||
            !float.IsFinite(FieldOfView) || FieldOfView <= 0 || FieldOfView >= 180 ||
            !float.IsFinite(NearPlane) || !float.IsFinite(FarPlane) || NearPlane <= 0 || FarPlane <= NearPlane)
        {
            throw new InvalidOperationException("Observation camera requires a finite pose, unit rotation and valid projection planes");
        }
        return Matrix4x4.CreateLookAt(Position, Position + Forward, Up) *
            Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView * MathF.PI / 180,
                Aspect, NearPlane, FarPlane);
    }
}
