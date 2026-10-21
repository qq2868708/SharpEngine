using System.Numerics;
using SharpEngine.Rendering;

namespace SharpEngine.UI;

/// <summary>Instance-local orbit math. No editor, asset, UI or World dependencies.</summary>
public sealed class OrbitCameraController
{
    private Vector3 Minimum = new(-0.5f);
    private Vector3 Maximum = new(0.5f);

    public Vector3 Pivot
    {
        get;
        private set;
    }
    public float Distance
    {
        get;
        private set;
    } = 3;
    public float Yaw
    {
        get;
        private set;
    } = 37;
    public float Pitch
    {
        get;
        private set;
    } = -20;
    public ulong StateRevision
    {
        get;
        private set;
    } = 1;
    public float Aspect
    {
        get;
        internal set;
    } = 1;
    public Vector3 Position
    {
        get
        {
            return Pivot + Vector3.Transform(Vector3.UnitZ, Rotation) * Distance;
        }
    }
    public Camera3D Camera
    {
        get
        {
            return new Camera3D(Position, Rotation);
        }
    }
    private Quaternion Rotation
    {
        get
        {
            return Quaternion.CreateFromYawPitchRoll(Yaw * MathF.PI / 180, Pitch * MathF.PI / 180, 0);
        }
    }

    public void Frame(IEnumerable<Vector3> Positions)
    {
        var Points = Positions.ToArray();
        Minimum = Points.Length == 0 ? new Vector3(-0.5f) : Points.Aggregate(Vector3.Min);
        Maximum = Points.Length == 0 ? new Vector3(0.5f) : Points.Aggregate(Vector3.Max);
        Execute(new(Viewport3DActionKind.Focus, 0, 0));
    }

    public void Orbit(float DeltaX, float DeltaY) { Execute(new(Viewport3DActionKind.Orbit, DeltaX, DeltaY)); }
    public void Pan(float DeltaX, float DeltaY) { Execute(new(Viewport3DActionKind.Pan, DeltaX, DeltaY)); }
    public void Zoom(float Delta) { Execute(new(Viewport3DActionKind.Zoom, 0, Delta)); }
    public void Focus() { Execute(new(Viewport3DActionKind.Focus, 0, 0)); }

    internal void Execute(Viewport3DAction Action)
    {
        if (!float.IsFinite(Action.X) || !float.IsFinite(Action.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(Action), "Camera input must be finite");
        }
        switch (Action.Kind)
        {
            case Viewport3DActionKind.Focus:
                Pivot = (Minimum + Maximum) * 0.5f;
                var Extents = Vector3.Max(Maximum - Minimum, new Vector3(0.01f));
                var Radius = Extents.Length() * 0.5f;
                var HalfFov = 35 * MathF.PI / 360;
                Distance = MathF.Max(0.1f, Radius / MathF.Sin(HalfFov) * MathF.Max(1, 1 / Aspect) * 1.1f);
                break;
            case Viewport3DActionKind.Orbit:
                Yaw += Action.X * 0.35f;
                // Quaternion pitch and orbit elevation use opposite signs.
                Pitch = Math.Clamp(Pitch - Action.Y * 0.35f, -89, 89);
                break;
            case Viewport3DActionKind.Pan:
                var Right = Vector3.Transform(Vector3.UnitX, Rotation);
                var Up = Vector3.Transform(Vector3.UnitY, Rotation);
                Pivot += (-Right * Action.X + Up * Action.Y) * Distance * 0.0015f;
                break;
            case Viewport3DActionKind.Zoom:
                Distance = Math.Clamp(Distance * MathF.Exp(-Action.Y * 0.16f), 0.01f, 10000);
                break;
        }
        StateRevision++;
    }
}
