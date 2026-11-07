using System.Numerics;

namespace SharpEngine.World;

internal readonly record struct TransformGizmoPose(Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>One gesture evaluated from its initial local TRS and parent frame.</summary>
internal sealed class TransformGizmoDrag(TransformGizmoPose Pose, Matrix4x4 Parent,
    TransformGizmoHandle Handle, Vector2 StartPointer)
{
    public TransformGizmoPose Evaluate(Vector2 Pointer)
    {
        var Start = Pose;
        var Delta = Pointer - StartPointer;
        if (Delta == Vector2.Zero)
        {
            return Start;
        }
        var Axis = Handle.Axis switch
        {
            TransformGizmoAxis.X => Vector3.UnitX,
            TransformGizmoAxis.Y => Vector3.UnitY,
            _ => Vector3.UnitZ,
        };
        var ProjectedDelta = Vector2.Dot(Delta, Handle.ScreenDirection);
        if (Handle.Mode == TransformGizmoMode.Move)
        {
            var Invertible = Matrix4x4.Invert(Parent, out var Inverse);
            if (!Invertible)
            {
                throw new InvalidDataException("Socket parent matrix is not invertible");
            }
            var ScreenLength = MathF.Max(4, Vector2.Distance(Handle.Pivot, Handle.End));
            var ModelDelta = Handle.WorldDirection * ProjectedDelta * Handle.WorldLength / ScreenLength;
            return Start with
            {
                Position = Start.Position + Vector3.TransformNormal(ModelDelta, Inverse),
            };
        }
        if (Handle.Mode == TransformGizmoMode.Rotate)
        {
            var Degrees = TransformGizmo.GetRotationDragDegrees(Handle.ScreenDirection, Delta);
            if (Degrees == 0)
            {
                return Start;
            }
            return Start with
            {
                Rotation = Quaternion.Normalize(Start.Rotation * Quaternion.CreateFromAxisAngle(Axis, Degrees * MathF.PI / 180)),
            };
        }
        var Factor = Math.Clamp(MathF.Exp(ProjectedDelta * 0.01f), 0.05f, 20);
        return Start with
        {
            Scale = Start.Scale * (Vector3.One + Axis * (Factor - 1)),
        };
    }
}
