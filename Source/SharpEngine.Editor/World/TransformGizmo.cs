using System.Numerics;
using SharpEngine.Rendering;
using SharpEngine.UI;

namespace SharpEngine.World;

public enum TransformGizmoAxis
{
    X,
    Y,
    Z,
}

public enum TransformGizmoMode
{
    Move,
    Rotate,
    Scale,
}

/// <summary>Visual primitive used by one axis handle. Keeping this separate
/// from the transform mode lets the viewport add plane/center handles later
/// without teaching input code about every drawing detail.</summary>
public enum TransformGizmoHandleKind
{
    AxisArrow,
    RotationRing,
    ScaleBox,
}

/// <summary>Shared definition for rendering and hit testing a transform tool.
/// The definition is deliberately data-only so the editor state and MCP can
/// describe the active tool without depending on native UI objects.</summary>
public readonly record struct TransformGizmoDefinition(
    TransformGizmoMode Mode,
    TransformGizmoHandleKind HandleKind,
    float PixelLength,
    float PickTolerance,
    float RingRadius)
{
    public static TransformGizmoDefinition FromTool(string? Value) =>
        Value?.Trim().ToLowerInvariant() switch
        {
            "rotate" or "rotation" => new(TransformGizmoMode.Rotate,
                TransformGizmoHandleKind.RotationRing, 94f, 13f, 0.92f),
            "scale" or "scaling" => new(TransformGizmoMode.Scale,
                TransformGizmoHandleKind.ScaleBox, 82f, 13f, 0.92f),
            _ => new(TransformGizmoMode.Move,
                TransformGizmoHandleKind.AxisArrow, 82f, 12f, 0.92f),
        };

    public string ToolName
    {
        get
        {
            return Mode switch
            {
                TransformGizmoMode.Rotate => "rotate",
                TransformGizmoMode.Scale => "scale",
                _ => "move",
            };
        }
    }
}

public readonly record struct TransformGizmoHandle(
    TransformGizmoAxis Axis, Vector3 WorldDirection, Vector2 Pivot,
    Vector2 End, float WorldLength)
{
    public TransformGizmoMode Mode
    {
        get;
        init;
    } = TransformGizmoMode.Move;
    public TransformGizmoHandleKind Kind
    {
        get;
        init;
    } = TransformGizmoHandleKind.AxisArrow;

    public Vector2 ScreenDirection
    {
        get
        {
            var Direction = End - Pivot;
            return Direction.LengthSquared() < 1e-6f ? Vector2.UnitX :
                Vector2.Normalize(Direction);
        }
    }
}

/// <summary>Shared handles with no entity or document dependency. Screen points are viewport-local pixels.</summary>
public sealed class TransformGizmo
{
    private readonly Matrix4x4 Transform;
    private readonly Matrix4x4 ViewProjection;
    private readonly UiRect Bounds;

    public TransformGizmo(Matrix4x4 Transform, Camera3D Camera, UiRect Bounds, string Tool = "move")
        : this(Transform, Bounds, Bounds.Width > 0 && Bounds.Height > 0
            ? Camera.ViewProjection(Bounds.Width / Bounds.Height) : Matrix4x4.Identity,
            Camera.Position, Camera.Forward, Camera.FieldOfView, Camera.NearPlane, Tool)
    {
    }

    internal TransformGizmo(Matrix4x4 Transform, UiRect Bounds, Matrix4x4 ViewProjection,
        Vector3 CameraPosition, Vector3 CameraForward, float FieldOfView, float NearPlane, string Tool)
    {
        this.Transform = Transform;
        this.Bounds = Bounds;
        this.ViewProjection = ViewProjection;
        Definition = TransformGizmoDefinition.FromTool(Tool);
        Handles = BuildHandles(CameraPosition, CameraForward, FieldOfView, NearPlane);
    }

    public TransformGizmoDefinition Definition
    {
        get;
    }
    public IReadOnlyList<TransformGizmoHandle> Handles
    {
        get;
    }

    public bool TryPick(float X, float Y, out TransformGizmoHandle Handle)
    {
        Handle = default;
        var Bounds = this.Bounds;
        var Definition = this.Definition;
        if (!float.IsFinite(X) || !float.IsFinite(Y) || X < Bounds.X || Y < Bounds.Y || X > Bounds.Right || Y > Bounds.Bottom)
        {
            return false;
        }
        var Pointer = new Vector2(X - Bounds.X, Y - Bounds.Y);
        var Closest = float.MaxValue;
        foreach (var Candidate in Handles)
        {
            var Ring = Candidate.Kind == TransformGizmoHandleKind.RotationRing
                ? GetHandlePath(Candidate)
                : null;
            var Distance = Candidate.Kind == TransformGizmoHandleKind.RotationRing
                ? DistanceToRotationRing(Pointer, Ring!)
                : Candidate.Kind == TransformGizmoHandleKind.ScaleBox
                    ? DistanceToScaleHandle(Pointer, Candidate)
                    : DistanceToSegment(Pointer, Candidate.Pivot, Candidate.End);
            if (Distance <= Definition.PickTolerance && Distance < Closest)
            {
                Closest = Distance;
                Handle = Candidate.Kind == TransformGizmoHandleKind.RotationRing
                    ? Candidate with { End = ClosestPointOnRotationRing(Pointer, Ring!) }
                    : Candidate;
            }
        }
        return Closest < float.MaxValue;
    }

    public static float GetRotationDragDegrees(Vector2 RadialDirection,
        Vector2 PointerDelta)
    {
        if (!float.IsFinite(RadialDirection.X) || !float.IsFinite(RadialDirection.Y) ||
            !float.IsFinite(PointerDelta.X) || !float.IsFinite(PointerDelta.Y) ||
            RadialDirection.LengthSquared() < 1e-6f)
        {
            return 0;
        }
        var Radial = Vector2.Normalize(RadialDirection);
        // Screen Y grows downward, so this is the screen-space
        // counter-clockwise tangent for a positive rotation.
        var Tangent = new Vector2(Radial.Y, -Radial.X);
        return Vector2.Dot(PointerDelta, Tangent) * 0.5f;
    }

    public IReadOnlyList<ViewportVertex> BuildLines()
    {
        var Lines = new List<ViewportVertex>();
        var Bounds = this.Bounds;
        foreach (var Handle in Handles)
        {
            var Color = Handle.Axis switch
            {
                TransformGizmoAxis.X => new Vector4(0.95f, 0.25f, 0.25f, 1),
                TransformGizmoAxis.Y => new Vector4(0.28f, 0.90f, 0.46f, 1),
                _ => new Vector4(0.30f, 0.58f, 1.0f, 1),
            };
            if (Handle.Kind == TransformGizmoHandleKind.RotationRing)
            {
                AddRotationRing(Lines, GetHandlePath(Handle), Bounds, Color);
                continue;
            }
            var Direction = Handle.ScreenDirection;
            AddScreenGizmoLine(Lines, Handle.Pivot, Handle.End, Bounds, Color);
            if (Handle.Kind == TransformGizmoHandleKind.ScaleBox)
            {
                AddScaleBox(Lines, Handle, Bounds, Color);
            }
            else
            {
                var BasePoint = Handle.End - Direction * 10;
                var Perpendicular = new Vector2(-Direction.Y, Direction.X) * 4;
                AddScreenGizmoLine(Lines, Handle.End, BasePoint + Perpendicular, Bounds, Color);
                AddScreenGizmoLine(Lines, Handle.End, BasePoint - Perpendicular, Bounds, Color);
            }
        }
        return Lines;
    }

    private List<TransformGizmoHandle> BuildHandles(Vector3 CameraPosition,
        Vector3 Forward, float FieldOfView, float NearPlane)
    {
        var Result = new List<TransformGizmoHandle>(3);
        var Bounds = this.Bounds;
        if (!float.IsFinite(Bounds.Width) || !float.IsFinite(Bounds.Height) || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return Result;
        }
        var ViewProjection = this.ViewProjection;
        var WorldMatrix = Transform;
        var Pivot = WorldMatrix.Translation;
        var Definition = this.Definition;
        var Depth = Vector3.Dot(Pivot - CameraPosition, Forward);
        if (!float.IsFinite(Depth) || Depth <= NearPlane)
        {
            return Result;
        }
        var Pixels = Definition.PixelLength;
        var WorldLength = MathF.Max(0.15f,
            Depth * 2 * MathF.Tan(FieldOfView * MathF.PI / 360) *
            Pixels / Bounds.Height);
        var Axes = new[]
        {
            (TransformGizmoAxis.X, Vector3.UnitX),
            (TransformGizmoAxis.Y, Vector3.UnitY),
            (TransformGizmoAxis.Z, Vector3.UnitZ),
        };
        foreach (var (Axis, LocalAxis) in Axes)
        {
            var WorldAxis = Vector3.TransformNormal(LocalAxis, WorldMatrix);
            if (!IsFinite(WorldAxis) || WorldAxis.LengthSquared() < 1e-6f)
            {
                continue;
            }
            WorldAxis = Vector3.Normalize(WorldAxis);
            var PivotProjected = TryProjectPoint(Pivot, ViewProjection, Bounds, out var PivotScreen);
            var EndProjected = TryProjectPoint(Pivot + WorldAxis * WorldLength, ViewProjection, Bounds, out var EndScreen);
            if (!PivotProjected || !EndProjected)
            {
                continue;
            }
            if ((EndScreen - PivotScreen).LengthSquared() < 16)
            {
                continue;
            }
            Result.Add(new TransformGizmoHandle(Axis, WorldAxis, PivotScreen, EndScreen,
                WorldLength)
            {
                Mode = Definition.Mode,
                Kind = Definition.HandleKind,
            });
        }
        return Result;
    }

    public IReadOnlyList<Vector2> GetHandlePath(TransformGizmoHandle Handle)
    {
        if (Handle.Kind != TransformGizmoHandleKind.RotationRing)
        {
            return [Handle.Pivot, Handle.End];
        }
        var Pivot = Transform.Translation;
        var ViewProjection = this.ViewProjection;
        var Bounds = this.Bounds;
        var Definition = this.Definition;
        var Axis = Handle.WorldDirection;
        var Reference = Math.Abs(Vector3.Dot(Axis, Vector3.UnitY)) < 0.9f ?
            Vector3.UnitY : Vector3.UnitX;
        var BasisX = Vector3.Normalize(Vector3.Cross(Axis, Reference));
        var BasisY = Vector3.Normalize(Vector3.Cross(Axis, BasisX));
        var Points = new List<Vector2>(49);
        for (var Index = 0; Index <= 48; Index++)
        {
            var Angle = Index / 48f * MathF.PI * 2;
            var Point = Pivot + (BasisX * MathF.Cos(Angle) + BasisY * MathF.Sin(Angle)) *
                Handle.WorldLength * Definition.RingRadius;
            Points.Add(TryProjectPoint(Point, ViewProjection, Bounds, out var Screen)
                ? Screen : new Vector2(float.NaN, float.NaN));
        }
        return Points;
    }

    private static void AddRotationRing(List<ViewportVertex> Lines,
        IReadOnlyList<Vector2> Points, UiRect Bounds, Vector4 Color)
    {
        for (var Index = 1; Index < Points.Count; Index++)
        {
            var Start = Points[Index - 1];
            var End = Points[Index];
            if (float.IsFinite(Start.X) && float.IsFinite(Start.Y) &&
                float.IsFinite(End.X) && float.IsFinite(End.Y))
            {
                AddScreenGizmoLine(Lines, Start, End, Bounds, Color);
            }
        }
    }

    private static void AddScaleBox(List<ViewportVertex> Lines,
        TransformGizmoHandle Handle, UiRect Bounds, Vector4 Color)
    {
        var Direction = Handle.ScreenDirection;
        var Perpendicular = new Vector2(-Direction.Y, Direction.X);
        var Half = 6f;
        var Back = Handle.End - Direction * 12f;
        var A = Handle.End + Perpendicular * Half;
        var B = Handle.End - Perpendicular * Half;
        var C = Back - Perpendicular * Half;
        var D = Back + Perpendicular * Half;
        AddScreenGizmoLine(Lines, A, D, Bounds, Color);
        AddScreenGizmoLine(Lines, D, C, Bounds, Color);
        AddScreenGizmoLine(Lines, C, B, Bounds, Color);
        AddScreenGizmoLine(Lines, B, A, Bounds, Color);
        // A short stem makes the scale handle remain recognizable when the
        // projected axis is nearly horizontal or vertical.
        AddScreenGizmoLine(Lines, Handle.Pivot, Handle.End, Bounds, Color);
    }

    private static float DistanceToRotationRing(Vector2 Point,
        IReadOnlyList<Vector2> Ring)
    {
        var Closest = float.MaxValue;
        for (var Index = 1; Index < Ring.Count; Index++)
        {
            if (!float.IsFinite(Ring[Index - 1].X) || !float.IsFinite(Ring[Index - 1].Y) ||
                !float.IsFinite(Ring[Index].X) || !float.IsFinite(Ring[Index].Y))
            {
                continue;
            }
            Closest = Math.Min(Closest, DistanceToSegment(Point, Ring[Index - 1], Ring[Index]));
        }
        return Closest;
    }

    private static float DistanceToScaleHandle(Vector2 Point,
        TransformGizmoHandle Handle)
    {
        var Direction = Handle.ScreenDirection;
        var Perpendicular = new Vector2(-Direction.Y, Direction.X);
        var Half = 7f;
        var Back = Handle.End - Direction * 12f;
        var A = Handle.End + Perpendicular * Half;
        var B = Handle.End - Perpendicular * Half;
        var C = Back - Perpendicular * Half;
        var D = Back + Perpendicular * Half;
        return Math.Min(DistanceToSegment(Point, A, B),
            Math.Min(DistanceToSegment(Point, B, C),
                Math.Min(DistanceToSegment(Point, C, D),
                    Math.Min(DistanceToSegment(Point, D, A),
                        DistanceToSegment(Point, Handle.Pivot, Handle.End)))));
    }

    private static Vector2 ClosestPointOnRotationRing(Vector2 Point,
        IReadOnlyList<Vector2> Ring)
    {
        var Closest = new Vector2(float.NaN, float.NaN);
        var ClosestDistance = float.MaxValue;
        for (var Index = 1; Index < Ring.Count; Index++)
        {
            var Start = Ring[Index - 1];
            var End = Ring[Index];
            if (!float.IsFinite(Start.X) || !float.IsFinite(Start.Y) ||
                !float.IsFinite(End.X) || !float.IsFinite(End.Y))
            {
                continue;
            }
            var Candidate = ClosestPointOnSegment(Point, Start, End);
            var Distance = Vector2.Distance(Point, Candidate);
            if (Distance < ClosestDistance)
            {
                ClosestDistance = Distance;
                Closest = Candidate;
            }
        }
        return Closest;
    }

    private static Vector2 ClosestPointOnSegment(Vector2 Point, Vector2 Start,
        Vector2 End)
    {
        var Direction = End - Start;
        if (Direction.LengthSquared() < 1e-6f)
        {
            return Start;
        }
        var T = Math.Clamp(Vector2.Dot(Point - Start, Direction) /
            Direction.LengthSquared(), 0, 1);
        return Start + Direction * T;
    }

    private static bool TryProjectPoint(Vector3 Point, Matrix4x4 Matrix,
        UiRect Bounds, out Vector2 Screen)
    {
        var Clip = Vector4.Transform(new Vector4(Point, 1), Matrix);
        if (!IsFinite(Clip) || Clip.W <= 1e-5f)
        {
            Screen = default;
            return false;
        }
        Screen = new Vector2((Clip.X / Clip.W + 1) * 0.5f * Bounds.Width,
            (1 - Clip.Y / Clip.W) * 0.5f * Bounds.Height);
        return float.IsFinite(Screen.X) && float.IsFinite(Screen.Y);
    }

    private static float DistanceToSegment(Vector2 Point, Vector2 Start, Vector2 End)
    {
        var Direction = End - Start;
        if (Direction.LengthSquared() < 1e-6f)
        {
            return Vector2.Distance(Point, Start);
        }
        var T = Math.Clamp(Vector2.Dot(Point - Start, Direction) /
            Direction.LengthSquared(), 0, 1);
        return Vector2.Distance(Point, Start + Direction * T);
    }

    private static void AddScreenGizmoLine(List<ViewportVertex> Lines,
        Vector2 Start, Vector2 End, UiRect Bounds, Vector4 Color)
    {
        if (!float.IsFinite(Start.X) || !float.IsFinite(Start.Y) ||
            !float.IsFinite(End.X) || !float.IsFinite(End.Y))
        {
            return;
        }
        Start = new Vector2(Math.Clamp(Start.X, 0, Bounds.Width),
            Math.Clamp(Start.Y, 0, Bounds.Height));
        End = new Vector2(Math.Clamp(End.X, 0, Bounds.Width),
            Math.Clamp(End.Y, 0, Bounds.Height));
        Lines.Add(new ViewportVertex(Start.X, Start.Y, 0.0f,
            Color.X, Color.Y, Color.Z, Color.W));
        Lines.Add(new ViewportVertex(End.X, End.Y, 0.0f,
            Color.X, Color.Y, Color.Z, Color.W));
    }

    private static bool IsFinite(Vector3 Value)
    {
        return float.IsFinite(Value.X) && float.IsFinite(Value.Y) && float.IsFinite(Value.Z);
    }

    private static bool IsFinite(Vector4 Value)
    {
        return float.IsFinite(Value.X) && float.IsFinite(Value.Y) && float.IsFinite(Value.Z) && float.IsFinite(Value.W);
    }
}
