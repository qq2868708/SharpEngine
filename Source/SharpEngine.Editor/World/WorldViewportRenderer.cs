using SharpEngine.Rendering;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Text.Json;
using SharpEngine.Assets;
using SharpEngine.UI;

using static SharpEngine.World.SceneRenderer;

namespace SharpEngine.World;

/// <summary>CPU scene submission stage. It transforms real 3D mesh geometry using
/// the scene's camera, clips it to the camera frustum, and leaves depth testing
/// and rasterization to the native viewport backend.</summary>
public static class WorldViewportRenderer
{
    public static WorldViewportFrame BuildFrame(WorldDocument world, UiRect bounds,
        string? selectedEntityId = null, AssetRegistry? assets = null, string transformTool = "move")
    {
        var frame = SceneRenderer.BuildFrame(world, bounds, assets, selectedEntityId);
        var Overlay = BuildOverlayFrame(world, bounds, selectedEntityId,
            transformTool);
        return frame with { Lines = frame.Lines.Concat(Overlay.Lines).ToArray() };
    }

    public static WorldViewportFrame BuildPreviewFrame(WorldDocument World, UiRect Bounds,
        Camera3D Camera, AssetRegistry? Assets = null)
    {
        var Frame = SceneRenderer.BuildFrame(World, Bounds, Assets, ViewCamera: Camera);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return Frame;
        }
        var Lines = Frame.Lines.ToList();
        AddNavigationOverlay(Lines, Camera.ViewProjection(Bounds.Width / Bounds.Height),
            Camera.Right, Camera.Up, Camera.Forward, Bounds);
        return Frame with
        {
            Lines = Lines.ToArray(),
        };
    }

    private static void AddNavigationOverlay(List<ViewportVertex> Lines, Matrix4x4 ViewProjection,
        Vector3 Right, Vector3 Up, Vector3 Forward, UiRect Bounds)
    {
        for (var Index = -10; Index <= 10; Index++)
        {
            var Grid = new Vector4(0.25f, 0.32f, 0.40f, 1);
            AddLine(Lines, new Vector3(Index, 0.008f, -10),
                new Vector3(Index, 0.008f, 10), ViewProjection, Bounds,
                Index == 0 ? new Vector4(0.27f, 0.46f, 0.92f, 1) : Grid);
            AddLine(Lines, new Vector3(-10, 0.008f, Index),
                new Vector3(10, 0.008f, Index), ViewProjection, Bounds,
                Index == 0 ? new Vector4(0.84f, 0.29f, 0.29f, 1) : Grid);
        }
        AddCornerGizmo(Lines, Right, Up, Forward, Bounds);
    }

    public static WorldViewportFrame BuildOverlayFrame(WorldDocument world,
        UiRect bounds, string? selectedEntityId = null, string transformTool = "move")
    {
        var camera = world.ActiveCamera;
        if (camera is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return new(bounds, [], [], world.Entities.Count, world.MeshCount,
                camera?.Id ?? string.Empty);
        }
        var lines = new List<ViewportVertex>();
        var viewProjection = CameraMatrix(world, camera, bounds.Width / bounds.Height);
        var selected = selectedEntityId is null ? null : world.Entities.FirstOrDefault(e =>
            string.Equals(e.Id, selectedEntityId, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            lines.AddRange(CreateTransformGizmo(world, bounds, selected.Id, transformTool)!.BuildLines());
        }

        AddNavigationOverlay(lines, viewProjection, WorldDirection(world, camera, Vector3.UnitX),
            WorldDirection(world, camera, Vector3.UnitY), WorldDirection(world, camera, -Vector3.UnitZ), bounds);
        return new(bounds, [], lines.ToArray(), world.Entities.Count,
            world.MeshCount, camera.Id);
    }
    public static bool TryPick(WorldDocument world, UiRect bounds, float x, float y,
        out WorldEntity? entity, AssetRegistry? assets = null) =>
        SceneRenderer.TryPick(world, bounds, x, y, out entity, assets);
    public static void InvalidateAssets(AssetRegistry assets) => SceneRenderer.InvalidateAssets(assets);
    public static bool TryPickTransformGizmo(WorldDocument World, UiRect Bounds,
        string? SelectedEntityId, float X, float Y, out TransformGizmoHandle Handle,
        string TransformTool = "move")
    {
        Handle = default;
        var Gizmo = CreateTransformGizmo(World, Bounds, SelectedEntityId, TransformTool);
        return Gizmo is not null && Gizmo.TryPick(X, Y, out Handle);
    }

    public static IReadOnlyList<TransformGizmoHandle> GetTransformGizmoHandles(
        WorldDocument World, UiRect Bounds, string? SelectedEntityId, string TransformTool = "move")
    {
        return CreateTransformGizmo(World, Bounds, SelectedEntityId, TransformTool)?.Handles ?? [];
    }

    public static TransformGizmoDefinition GetTransformGizmoDefinition(string? TransformTool)
    {
        return TransformGizmoDefinition.FromTool(TransformTool);
    }

    public static float GetRotationDragDegrees(Vector2 RadialDirection, Vector2 PointerDelta)
    {
        return TransformGizmo.GetRotationDragDegrees(RadialDirection, PointerDelta);
    }

    private static TransformGizmo? CreateTransformGizmo(WorldDocument World, UiRect Bounds,
        string? SelectedEntityId, string TransformTool)
    {
        var Entity = World.Entities.FirstOrDefault(Candidate =>
            string.Equals(Candidate.Id, SelectedEntityId, StringComparison.OrdinalIgnoreCase));
        var Camera = World.ActiveCamera;
        if (Entity is null || Camera is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return null;
        }
        return new TransformGizmo(World.WorldMatrix(Entity), Bounds,
            CameraMatrix(World, Camera, Bounds.Width / Bounds.Height), World.WorldMatrix(Camera).Translation,
            WorldDirection(World, Camera, -Vector3.UnitZ), Camera.FieldOfView, Camera.NearPlane, TransformTool);
    }

    private static void AddUiLine(List<ViewportVertex> lines, float x0,
        float y0, float x1, float y1, Vector4 color)
    {
        if (!float.IsFinite(x0) || !float.IsFinite(y0) ||
            !float.IsFinite(x1) || !float.IsFinite(y1)) return;
        lines.Add(new ViewportVertex(x0, y0, 0.02f,
            color.X, color.Y, color.Z, color.W));
        lines.Add(new ViewportVertex(x1, y1, 0.02f,
            color.X, color.Y, color.Z, color.W));
    }

    private static void AddCornerGizmo(List<ViewportVertex> Lines,
        Vector3 ScreenRight, Vector3 ScreenUp, Vector3 ScreenForward, UiRect Bounds)
    {
        var Center = new Vector2(Math.Max(44, Bounds.Width - 72), 60);
        AddProjectedGizmoAxis(Lines, Center, Vector3.UnitX,
            ScreenRight, ScreenUp, ScreenForward, 44,
            new Vector4(0.95f, 0.30f, 0.30f, 1));
        AddProjectedGizmoAxis(Lines, Center, Vector3.UnitY,
            ScreenRight, ScreenUp, ScreenForward, 44,
            new Vector4(0.30f, 0.86f, 0.48f, 1));
        AddProjectedGizmoAxis(Lines, Center, Vector3.UnitZ,
            ScreenRight, ScreenUp, ScreenForward, 44,
            new Vector4(0.35f, 0.60f, 0.95f, 1));
    }

    private static void AddProjectedGizmoAxis(List<ViewportVertex> lines,
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

    private static void AddGizmoAxis(List<ViewportVertex> lines,
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
