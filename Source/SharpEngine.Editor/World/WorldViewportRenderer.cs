using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Text.Json;
using SharpEngine.Assets;
using SharpEngine.UI;

using static SharpEngine.World.SceneRenderer;

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
    public static TransformGizmoDefinition FromTool(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "rotate" or "rotation" => new(TransformGizmoMode.Rotate,
                TransformGizmoHandleKind.RotationRing, 94f, 13f, 0.92f),
            "scale" or "scaling" => new(TransformGizmoMode.Scale,
                TransformGizmoHandleKind.ScaleBox, 82f, 13f, 0.92f),
            _ => new(TransformGizmoMode.Move,
                TransformGizmoHandleKind.AxisArrow, 82f, 12f, 0.92f),
        };

    public string ToolName => Mode switch
    {
        TransformGizmoMode.Rotate => "rotate",
        TransformGizmoMode.Scale => "scale",
        _ => "move",
    };
}

public readonly record struct TransformGizmoHandle(
    TransformGizmoAxis Axis, Vector3 WorldDirection, Vector2 Pivot,
    Vector2 End, float WorldLength)
{
    public TransformGizmoMode Mode { get; init; } = TransformGizmoMode.Move;
    public TransformGizmoHandleKind Kind { get; init; } = TransformGizmoHandleKind.AxisArrow;

    public Vector2 ScreenDirection
    {
        get
        {
            var direction = End - Pivot;
            return direction.LengthSquared() < 1e-6f ? Vector2.UnitX :
                Vector2.Normalize(direction);
        }
    }
}

/// <summary>CPU scene submission stage. It transforms real 3D mesh geometry using
/// the scene's camera, clips it to the camera frustum, and leaves depth testing
/// and rasterization to the native viewport backend.</summary>
public static class WorldViewportRenderer
{
    public static WorldViewportFrame BuildFrame(WorldDocument world, UiRect bounds,
        string? selectedEntityId = null, AssetRegistry? assets = null, string transformTool = "move")
    {
        var frame = SceneRenderer.BuildFrame(world, bounds, assets, selectedEntityId);
        var camera = world.ActiveCamera;
        if (camera is null || bounds.Width <= 0 || bounds.Height <= 0) return frame;
        var lines = frame.Lines.ToList();
        var viewProjection = CameraMatrix(world, camera, bounds.Width / bounds.Height);
        var selected = selectedEntityId is null ? null : world.Entities.FirstOrDefault(e =>
            string.Equals(e.Id, selectedEntityId, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
            AddTransformGizmo(lines, world, selected, camera, bounds, transformTool);

        // Grid and axes also use the scene camera and perspective projection.
        for (var index = -10; index <= 10; index++)
        {
            var grid = new Vector4(0.25f, 0.32f, 0.40f, 1);
            AddLine(lines, new Vector3(index, 0.008f, -10),
                new Vector3(index, 0.008f, 10), viewProjection, bounds,
                index == 0 ? new Vector4(0.27f, 0.46f, 0.92f, 1) : grid);
            AddLine(lines, new Vector3(-10, 0.008f, index),
                new Vector3(10, 0.008f, index), viewProjection, bounds,
                index == 0 ? new Vector4(0.84f, 0.29f, 0.29f, 1) : grid);
        }
        // Orientation gizmo: screen-space and fixed in the top-right corner.
        // Its arms use the camera basis, so they rotate as the viewport orbits
        // while staying readable and independent of scene scale.
        AddCornerGizmo(lines, world, camera, bounds);
        return frame with { Lines = lines.ToArray() };
    }
    public static bool TryPick(WorldDocument world, UiRect bounds, float x, float y,
        out WorldEntity? entity, AssetRegistry? assets = null) =>
        SceneRenderer.TryPick(world, bounds, x, y, out entity, assets);
    public static void InvalidateAssets(AssetRegistry assets) => SceneRenderer.InvalidateAssets(assets);
    public static bool TryPickTransformGizmo(WorldDocument world, UiRect bounds,
        string? selectedEntityId, float x, float y, out TransformGizmoHandle handle,
        string transformTool = "move")
    {
        handle = default;
        if (string.IsNullOrWhiteSpace(selectedEntityId) ||
            !float.IsFinite(x) || !float.IsFinite(y)) return false;
        var entity = world.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, selectedEntityId, StringComparison.OrdinalIgnoreCase));
        var camera = world.ActiveCamera;
        if (entity is null || camera is null || bounds.Width <= 0 || bounds.Height <= 0 ||
            x < bounds.X || y < bounds.Y || x > bounds.Right || y > bounds.Bottom)
            return false;
        var definition = TransformGizmoDefinition.FromTool(transformTool);
        var handles = BuildTransformGizmoHandles(world, entity, camera, bounds, definition);
        var pointer = new Vector2(x - bounds.X, y - bounds.Y);
        var closest = float.MaxValue;
        foreach (var candidate in handles)
        {
            var ring = candidate.Kind == TransformGizmoHandleKind.RotationRing
                ? BuildRotationRing(candidate, world, entity, camera, bounds, definition)
                : null;
            var distance = candidate.Kind == TransformGizmoHandleKind.RotationRing
                ? DistanceToRotationRing(pointer, ring!)
                : candidate.Kind == TransformGizmoHandleKind.ScaleBox
                    ? DistanceToScaleHandle(pointer, candidate)
                    : DistanceToSegment(pointer, candidate.Pivot, candidate.End);
            if (distance <= definition.PickTolerance && distance < closest)
            {
                closest = distance;
                handle = candidate.Kind == TransformGizmoHandleKind.RotationRing
                    ? candidate with { End = ClosestPointOnRotationRing(pointer, ring!) }
                    : candidate;
            }
        }
        return closest < float.MaxValue;
    }

    public static IReadOnlyList<TransformGizmoHandle> GetTransformGizmoHandles(
        WorldDocument world, UiRect bounds, string? selectedEntityId,
        string transformTool = "move")
    {
        if (string.IsNullOrWhiteSpace(selectedEntityId)) return Array.Empty<TransformGizmoHandle>();
        var entity = world.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, selectedEntityId, StringComparison.OrdinalIgnoreCase));
        var camera = world.ActiveCamera;
        return entity is null || camera is null ? Array.Empty<TransformGizmoHandle>() :
            BuildTransformGizmoHandles(world, entity, camera, bounds,
                TransformGizmoDefinition.FromTool(transformTool));
    }

    /// <summary>Returns the serializable visual and interaction contract for a
    /// transform tool. Native UI and MCP callers can use this to stay in sync
    /// with the renderer without duplicating mode-specific rules.</summary>
    public static TransformGizmoDefinition GetTransformGizmoDefinition(string? transformTool) =>
        TransformGizmoDefinition.FromTool(transformTool);

    public static float GetRotationDragDegrees(Vector2 radialDirection,
        Vector2 pointerDelta)
    {
        if (!float.IsFinite(radialDirection.X) || !float.IsFinite(radialDirection.Y) ||
            !float.IsFinite(pointerDelta.X) || !float.IsFinite(pointerDelta.Y) ||
            radialDirection.LengthSquared() < 1e-6f) return 0;
        var radial = Vector2.Normalize(radialDirection);
        // Screen Y grows downward, so this is the screen-space
        // counter-clockwise tangent for a positive rotation.
        var tangent = new Vector2(radial.Y, -radial.X);
        return Vector2.Dot(pointerDelta, tangent) * 0.5f;
    }

    private static void AddUiLine(List<WorldViewportVertex> lines, float x0,
        float y0, float x1, float y1, Vector4 color)
    {
        if (!float.IsFinite(x0) || !float.IsFinite(y0) ||
            !float.IsFinite(x1) || !float.IsFinite(y1)) return;
        lines.Add(new WorldViewportVertex(x0, y0, 0.02f,
            color.X, color.Y, color.Z, color.W));
        lines.Add(new WorldViewportVertex(x1, y1, 0.02f,
            color.X, color.Y, color.Z, color.W));
    }

    private static void AddTransformGizmo(List<WorldViewportVertex> lines,
        WorldDocument world, WorldEntity entity, WorldEntity camera, UiRect bounds,
        string transformTool)
    {
        var definition = TransformGizmoDefinition.FromTool(transformTool);
        foreach (var handle in BuildTransformGizmoHandles(world, entity, camera, bounds, definition))
        {
            var color = handle.Axis switch
            {
                TransformGizmoAxis.X => new Vector4(0.95f, 0.25f, 0.25f, 1),
                TransformGizmoAxis.Y => new Vector4(0.28f, 0.90f, 0.46f, 1),
                _ => new Vector4(0.30f, 0.58f, 1.0f, 1),
            };
            if (handle.Kind == TransformGizmoHandleKind.RotationRing)
            {
                AddRotationRing(lines, BuildRotationRing(handle, world, entity,
                    camera, bounds, definition), bounds, color);
                continue;
            }
            var direction = handle.ScreenDirection;
            AddScreenGizmoLine(lines, handle.Pivot, handle.End, bounds, color);
            if (handle.Kind == TransformGizmoHandleKind.ScaleBox)
            {
                AddScaleBox(lines, handle, bounds, color);
            }
            else
            {
                var basePoint = handle.End - direction * 10;
                var perpendicular = new Vector2(-direction.Y, direction.X) * 4;
                AddScreenGizmoLine(lines, handle.End, basePoint + perpendicular, bounds, color);
                AddScreenGizmoLine(lines, handle.End, basePoint - perpendicular, bounds, color);
            }
        }
    }

    private static List<TransformGizmoHandle> BuildTransformGizmoHandles(
        WorldDocument world, WorldEntity entity, WorldEntity camera, UiRect bounds,
        TransformGizmoDefinition definition)
    {
        var result = new List<TransformGizmoHandle>(3);
        var viewProjection = CameraMatrix(world, camera, bounds.Width / bounds.Height);
        var worldMatrix = world.WorldMatrix(entity);
        var pivot = new Vector3(worldMatrix.M41, worldMatrix.M42, worldMatrix.M43);
        var cameraMatrix = world.WorldMatrix(camera);
        var cameraPosition = new Vector3(cameraMatrix.M41, cameraMatrix.M42,
            cameraMatrix.M43);
        var forward = WorldDirection(world, camera, -Vector3.UnitZ);
        var depth = Vector3.Dot(pivot - cameraPosition, forward);
        if (!float.IsFinite(depth) || depth <= camera.NearPlane) return result;
        var pixels = definition.PixelLength;
        var worldLength = MathF.Max(0.15f,
            depth * 2 * MathF.Tan(camera.FieldOfView * MathF.PI / 360) *
            pixels / bounds.Height);
        var axes = new[]
        {
            (TransformGizmoAxis.X, Vector3.UnitX),
            (TransformGizmoAxis.Y, Vector3.UnitY),
            (TransformGizmoAxis.Z, Vector3.UnitZ),
        };
        foreach (var (axis, localAxis) in axes)
        {
            var worldAxis = Vector3.TransformNormal(localAxis, worldMatrix);
            if (!IsFinite(worldAxis) || worldAxis.LengthSquared() < 1e-6f) continue;
            worldAxis = Vector3.Normalize(worldAxis);
            if (!TryProjectPoint(pivot, viewProjection, bounds, out var pivotScreen) ||
                !TryProjectPoint(pivot + worldAxis * worldLength, viewProjection, bounds,
                    out var endScreen)) continue;
            if ((endScreen - pivotScreen).LengthSquared() < 16) continue;
            result.Add(new TransformGizmoHandle(axis, worldAxis, pivotScreen, endScreen,
                worldLength)
            {
                Mode = definition.Mode,
                Kind = definition.HandleKind,
            });
        }
        return result;
    }

    private static List<Vector2> BuildRotationRing(TransformGizmoHandle handle,
        WorldDocument world, WorldEntity entity, WorldEntity camera, UiRect bounds,
        TransformGizmoDefinition definition)
    {
        var matrix = world.WorldMatrix(entity);
        var pivot = new Vector3(matrix.M41, matrix.M42, matrix.M43);
        var viewProjection = CameraMatrix(world, camera, bounds.Width / bounds.Height);
        var axis = handle.WorldDirection;
        var reference = Math.Abs(Vector3.Dot(axis, Vector3.UnitY)) < 0.9f ?
            Vector3.UnitY : Vector3.UnitX;
        var basisX = Vector3.Normalize(Vector3.Cross(axis, reference));
        var basisY = Vector3.Normalize(Vector3.Cross(axis, basisX));
        var points = new List<Vector2>(49);
        for (var index = 0; index <= 48; index++)
        {
            var angle = index / 48f * MathF.PI * 2;
            var point = pivot + (basisX * MathF.Cos(angle) + basisY * MathF.Sin(angle)) *
                handle.WorldLength * definition.RingRadius;
            points.Add(TryProjectPoint(point, viewProjection, bounds, out var screen)
                ? screen : new Vector2(float.NaN, float.NaN));
        }
        return points;
    }

    private static void AddRotationRing(List<WorldViewportVertex> lines,
        IReadOnlyList<Vector2> points, UiRect bounds, Vector4 color)
    {
        for (var index = 1; index < points.Count; index++)
        {
            var start = points[index - 1];
            var end = points[index];
            if (float.IsFinite(start.X) && float.IsFinite(start.Y) &&
                float.IsFinite(end.X) && float.IsFinite(end.Y))
                AddScreenGizmoLine(lines, start, end, bounds, color);
        }
    }

    private static void AddScaleBox(List<WorldViewportVertex> lines,
        TransformGizmoHandle handle, UiRect bounds, Vector4 color)
    {
        var direction = handle.ScreenDirection;
        var perpendicular = new Vector2(-direction.Y, direction.X);
        var half = 6f;
        var back = handle.End - direction * 12f;
        var a = handle.End + perpendicular * half;
        var b = handle.End - perpendicular * half;
        var c = back - perpendicular * half;
        var d = back + perpendicular * half;
        AddScreenGizmoLine(lines, a, d, bounds, color);
        AddScreenGizmoLine(lines, d, c, bounds, color);
        AddScreenGizmoLine(lines, c, b, bounds, color);
        AddScreenGizmoLine(lines, b, a, bounds, color);
        // A short stem makes the scale handle remain recognizable when the
        // projected axis is nearly horizontal or vertical.
        AddScreenGizmoLine(lines, handle.Pivot, handle.End, bounds, color);
    }

    private static float DistanceToRotationRing(Vector2 point,
        IReadOnlyList<Vector2> ring)
    {
        var closest = float.MaxValue;
        for (var index = 1; index < ring.Count; index++)
        {
            if (!float.IsFinite(ring[index - 1].X) || !float.IsFinite(ring[index - 1].Y) ||
                !float.IsFinite(ring[index].X) || !float.IsFinite(ring[index].Y)) continue;
            closest = Math.Min(closest, DistanceToSegment(point, ring[index - 1], ring[index]));
        }
        return closest;
    }

    private static float DistanceToScaleHandle(Vector2 point,
        TransformGizmoHandle handle)
    {
        var direction = handle.ScreenDirection;
        var perpendicular = new Vector2(-direction.Y, direction.X);
        var half = 7f;
        var back = handle.End - direction * 12f;
        var a = handle.End + perpendicular * half;
        var b = handle.End - perpendicular * half;
        var c = back - perpendicular * half;
        var d = back + perpendicular * half;
        return Math.Min(DistanceToSegment(point, a, b),
            Math.Min(DistanceToSegment(point, b, c),
                Math.Min(DistanceToSegment(point, c, d),
                    Math.Min(DistanceToSegment(point, d, a),
                        DistanceToSegment(point, handle.Pivot, handle.End)))));
    }

    private static Vector2 ClosestPointOnRotationRing(Vector2 point,
        IReadOnlyList<Vector2> ring)
    {
        var closest = new Vector2(float.NaN, float.NaN);
        var closestDistance = float.MaxValue;
        for (var index = 1; index < ring.Count; index++)
        {
            var start = ring[index - 1];
            var end = ring[index];
            if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) ||
                !float.IsFinite(end.X) || !float.IsFinite(end.Y)) continue;
            var candidate = ClosestPointOnSegment(point, start, end);
            var distance = Vector2.Distance(point, candidate);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closest = candidate;
            }
        }
        return closest;
    }

    private static Vector2 ClosestPointOnSegment(Vector2 point, Vector2 start,
        Vector2 end)
    {
        var direction = end - start;
        if (direction.LengthSquared() < 1e-6f) return start;
        var t = Math.Clamp(Vector2.Dot(point - start, direction) /
            direction.LengthSquared(), 0, 1);
        return start + direction * t;
    }

    private static bool TryProjectPoint(Vector3 point, Matrix4x4 matrix,
        UiRect bounds, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), matrix);
        if (!IsFinite(clip) || clip.W <= 1e-5f)
        {
            screen = default;
            return false;
        }
        screen = new Vector2((clip.X / clip.W + 1) * 0.5f * bounds.Width,
            (1 - clip.Y / clip.W) * 0.5f * bounds.Height);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var direction = end - start;
        if (direction.LengthSquared() < 1e-6f) return Vector2.Distance(point, start);
        var t = Math.Clamp(Vector2.Dot(point - start, direction) /
            direction.LengthSquared(), 0, 1);
        return Vector2.Distance(point, start + direction * t);
    }

    private static void AddScreenGizmoLine(List<WorldViewportVertex> lines,
        Vector2 start, Vector2 end, UiRect bounds, Vector4 color)
    {
        if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) ||
            !float.IsFinite(end.X) || !float.IsFinite(end.Y)) return;
        start = new Vector2(Math.Clamp(start.X, 0, bounds.Width),
            Math.Clamp(start.Y, 0, bounds.Height));
        end = new Vector2(Math.Clamp(end.X, 0, bounds.Width),
            Math.Clamp(end.Y, 0, bounds.Height));
        lines.Add(new WorldViewportVertex(start.X, start.Y, 0.0f,
            color.X, color.Y, color.Z, color.W));
        lines.Add(new WorldViewportVertex(end.X, end.Y, 0.0f,
            color.X, color.Y, color.Z, color.W));
    }

    private static void AddCornerGizmo(List<WorldViewportVertex> lines,
        WorldDocument world, WorldEntity camera, UiRect bounds)
    {
        var center = new Vector2(Math.Max(44, bounds.Width - 72), 60);
        var screenRight = WorldDirection(world, camera, Vector3.UnitX);
        var screenUp = WorldDirection(world, camera, Vector3.UnitY);
        var screenForward = WorldDirection(world, camera, -Vector3.UnitZ);
        AddProjectedGizmoAxis(lines, center, Vector3.UnitX,
            screenRight, screenUp, screenForward, 44,
            new Vector4(0.95f, 0.30f, 0.30f, 1));
        AddProjectedGizmoAxis(lines, center, Vector3.UnitY,
            screenRight, screenUp, screenForward, 44,
            new Vector4(0.30f, 0.86f, 0.48f, 1));
        AddProjectedGizmoAxis(lines, center, Vector3.UnitZ,
            screenRight, screenUp, screenForward, 44,
            new Vector4(0.35f, 0.60f, 0.95f, 1));
    }

    private static void AddProjectedGizmoAxis(List<WorldViewportVertex> lines,
        Vector2 center, Vector3 axis,
        Vector3 screenRight, Vector3 screenUp, Vector3 screenForward,
        float length, Vector4 color)
    {
        var projected = new Vector2(Vector3.Dot(axis, screenRight),
            -Vector3.Dot(axis, screenUp));
        if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y)) return;
        var projectedLength = projected.Length();
        if (projectedLength < 0.08f)
            projected = new Vector2(0, -1);
        else
            projected /= projectedLength;
        // Axes pointing into the screen are shown in a darker tint, as in
        // mature DCC gizmos, while the projected direction remains visible.
        if (Vector3.Dot(axis, screenForward) < -0.05f)
            color = new Vector4(color.X * 0.55f, color.Y * 0.55f,
                color.Z * 0.55f, color.W);
        AddGizmoAxis(lines, center, projected, length, color);
    }

    private static void AddGizmoAxis(List<WorldViewportVertex> lines,
        Vector2 center, Vector2 direction,
        float length, Vector4 color)
    {
        if (direction.LengthSquared() < 1e-6f) return;
        direction = Vector2.Normalize(direction);
        var end = center + direction * length;
        AddUiLine(lines, center.X, center.Y, end.X, end.Y, color);
        var basePoint = end - direction * 8;
        var perpendicular = new Vector2(-direction.Y, direction.X) * 3.5f;
        AddUiLine(lines, end.X, end.Y, (basePoint + perpendicular).X,
            (basePoint + perpendicular).Y, color);
        AddUiLine(lines, end.X, end.Y, (basePoint - perpendicular).X,
            (basePoint - perpendicular).Y, color);
    }

}
