using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Text.Json;
using SharpEngine.Assets;
using SharpEngine.UI;

namespace SharpEngine.World;

/// <summary>One native-friendly vertex: relative viewport pixels, 0..1 depth,
/// and RGBA. The GL and future Vulkan backends consume the same data.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct WorldViewportVertex(float X, float Y, float Z,
    float R, float G, float B, float A);

public sealed record WorldViewportFrame(UiRect Bounds,
    WorldViewportVertex[] Triangles, WorldViewportVertex[] Lines,
    int EntityCount, int MeshCount, string CameraId)
{
    public int TriangleCount => Triangles.Length / 3;
    public int LineCount => Lines.Length / 2;
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

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
    private sealed record MeshGeometry(Vector3[] Positions, int[] Indices,
        Vector4 BaseColor, string? Error = null);
    private static readonly ConditionalWeakTable<AssetRegistry,
        ConcurrentDictionary<string, MeshGeometry>> AssetCaches = new();
    private static readonly Vector3[] CubeVertices =
    {
        new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f),
        new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f),
        new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f),
        new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f),
    };
    private static readonly int[] CubeTriangles =
    {
        0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
        0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5,
        3, 7, 6, 3, 6, 2, 0, 1, 5, 0, 5, 4,
    };
    private static readonly Vector3[] PlaneVertices =
    {
        new(-0.5f, 0, -0.5f), new(-0.5f, 0, 0.5f),
        new(0.5f, 0, 0.5f), new(0.5f, 0, -0.5f),
    };
    private static readonly int[] PlaneTriangles = { 0, 1, 2, 0, 2, 3 };

    public static WorldViewportFrame BuildFrame(WorldDocument world,
        UiRect bounds, string? selectedEntityId = null, AssetRegistry? assets = null,
        string transformTool = "move")
    {
        ArgumentNullException.ThrowIfNull(world);
        var triangles = new List<WorldViewportVertex>();
        var lines = new List<WorldViewportVertex>();
        var diagnostics = new List<string>();
        var camera = world.ActiveCamera;
        if (camera is null || bounds.Width <= 0 || bounds.Height <= 0 ||
            !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height))
            return new WorldViewportFrame(bounds, [], [], world.Entities.Count,
                world.MeshCount, camera?.Id ?? string.Empty);

        var viewProjection = CameraMatrix(world, camera, bounds.Width / bounds.Height);
        var lights = world.Entities
            .Where(entity => entity.Kind == WorldEntityKind.Light && entity.Intensity > 0)
            .Select(light =>
            {
                var color = new Vector3(light.Color.R, light.Color.G, light.Color.B) / 255f;
                return (Direction: WorldDirection(world, light, -Vector3.UnitZ),
                    Radiance: color * light.Intensity);
            })
            .ToArray();

        foreach (var entity in world.Entities.Where(e => e.Kind == WorldEntityKind.Mesh))
        {
            var geometry = ResolveGeometry(entity, assets);
            if (geometry.Error is not null)
            {
                diagnostics.Add($"{entity.Id}: {geometry.Error}");
                continue;
            }
            var vertices = geometry.Positions;
            var indices = geometry.Indices;
            var model = world.WorldMatrix(entity);
            var mvp = model * viewProjection;
            for (var index = 0; index < indices.Length; index += 3)
            {
                var p0 = vertices[indices[index]];
                var p1 = vertices[indices[index + 1]];
                var p2 = vertices[indices[index + 2]];
                var world0 = Vector3.Transform(p0, model);
                var world1 = Vector3.Transform(p1, model);
                var world2 = Vector3.Transform(p2, model);
                var normal = Vector3.Normalize(Vector3.Cross(world1 - world0,
                    world2 - world0));
                if (!IsFinite(normal)) continue;
                var lighting = Vector3.Zero;
                foreach (var light in lights)
                {
                    var diffuse = MathF.Max(0, Vector3.Dot(normal, -light.Direction));
                    lighting += light.Radiance * diffuse;
                }
                var color = new Vector4(
                    Math.Clamp(entity.Color.R / 255f * geometry.BaseColor.X * lighting.X, 0, 1),
                    Math.Clamp(entity.Color.G / 255f * geometry.BaseColor.Y * lighting.Y, 0, 1),
                    Math.Clamp(entity.Color.B / 255f * geometry.BaseColor.Z * lighting.Z, 0, 1),
                    geometry.BaseColor.W);
                var polygon = ClipPolygon(new[]
                {
                    Vector4.Transform(new Vector4(p0, 1), mvp),
                    Vector4.Transform(new Vector4(p1, 1), mvp),
                    Vector4.Transform(new Vector4(p2, 1), mvp),
                });
                for (var fan = 1; fan + 1 < polygon.Count; fan++)
                {
                    triangles.Add(ToVertex(polygon[0], bounds, color));
                    triangles.Add(ToVertex(polygon[fan], bounds, color));
                    triangles.Add(ToVertex(polygon[fan + 1], bounds, color));
                }
            }

            if (string.Equals(entity.Id, selectedEntityId,
                StringComparison.OrdinalIgnoreCase))
            {
                for (var index = 0; index < indices.Length; index += 3)
                    for (var edge = 0; edge < 3; edge++)
                        AddLine(lines, vertices[indices[index + edge]],
                            vertices[indices[index + (edge + 1) % 3]], mvp,
                            bounds, new Vector4(1, 0.68f, 0.18f, 1));
            }
        }

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
        return new WorldViewportFrame(bounds, triangles.ToArray(), lines.ToArray(),
            world.Entities.Count, world.MeshCount, camera.Id)
        { Diagnostics = diagnostics.AsReadOnly() };
    }

    public static bool TryPick(WorldDocument world, UiRect bounds, float x,
        float y, out WorldEntity? entity, AssetRegistry? assets = null)
    {
        entity = null;
        var camera = world.ActiveCamera;
        if (camera is null || bounds.Width <= 0 || bounds.Height <= 0 ||
            x < bounds.X || y < bounds.Y || x > bounds.Right || y > bounds.Bottom)
            return false;
        if (!Matrix4x4.Invert(CameraMatrix(world, camera, bounds.Width / bounds.Height),
            out var inverse)) return false;
        var ndc = new Vector2((x - bounds.X) / bounds.Width * 2 - 1,
            1 - (y - bounds.Y) / bounds.Height * 2);
        var origin4 = Vector4.Transform(new Vector4(ndc, 0, 1), inverse);
        var end4 = Vector4.Transform(new Vector4(ndc, 1, 1), inverse);
        var origin = new Vector3(origin4.X, origin4.Y, origin4.Z) / origin4.W;
        var end = new Vector3(end4.X, end4.Y, end4.Z) / end4.W;
        var direction = Vector3.Normalize(end - origin);
        var nearest = float.MaxValue;
        var farDistance = Vector3.Distance(origin, end);
        foreach (var candidate in world.Entities.Where(e => e.Kind == WorldEntityKind.Mesh))
        {
            var geometry = ResolveGeometry(candidate, assets);
            if (geometry.Error is not null) continue;
            if (!Matrix4x4.Invert(world.WorldMatrix(candidate), out var modelInverse)) continue;
            var localOrigin = Vector3.Transform(origin, modelInverse);
            var localDirection = Vector3.TransformNormal(direction, modelInverse);
            for (var index = 0; index < geometry.Indices.Length; index += 3)
            {
                if (RayTriangle(localOrigin, localDirection,
                    geometry.Positions[geometry.Indices[index]],
                    geometry.Positions[geometry.Indices[index + 1]],
                    geometry.Positions[geometry.Indices[index + 2]],
                    out var distance) && distance < nearest && distance <= farDistance)
                {
                    nearest = distance;
                    entity = candidate;
                }
            }
        }
        return entity is not null;
    }

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

    private static bool RayTriangle(Vector3 origin, Vector3 direction,
        Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(direction, e2);
        var determinant = Vector3.Dot(e1, p);
        if (Math.Abs(determinant) < 1e-7f) return false;
        var inverse = 1 / determinant;
        var offset = origin - a;
        var u = Vector3.Dot(offset, p) * inverse;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(offset, e1);
        var v = Vector3.Dot(direction, q) * inverse;
        if (v < 0 || u + v > 1) return false;
        distance = Vector3.Dot(e2, q) * inverse;
        return distance >= 0;
    }

    private static Matrix4x4 CameraMatrix(WorldDocument world, WorldEntity camera,
        float aspect)
    {
        var matrix = world.WorldMatrix(camera);
        var position = new Vector3(matrix.M41, matrix.M42, matrix.M43);
        var forward = WorldDirection(world, camera, -Vector3.UnitZ);
        var up = WorldDirection(world, camera, Vector3.UnitY);
        return Matrix4x4.CreateLookAt(position, position + forward, up) *
            Matrix4x4.CreatePerspectiveFieldOfView(camera.FieldOfView * MathF.PI / 180,
                aspect, camera.NearPlane, camera.FarPlane);
    }

    private static Vector3 WorldDirection(WorldDocument world, WorldEntity entity,
        Vector3 localDirection)
    {
        var direction = localDirection;
        var current = entity;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            if (!visited.Add(current.Id))
                throw new InvalidDataException($"World parent cycle includes '{current.Id}'");
            direction = Vector3.Transform(direction, Rotation(current.Rotation));
            if (current.ParentId.Length == 0) break;
            current = world.Entities.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, current.ParentId,
                    StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException(
                $"World entity '{current.Id}' references missing parent '{current.ParentId}'");
        }
        return Vector3.Normalize(direction);
    }

    private static Quaternion Rotation(WorldVector3 euler) =>
        Quaternion.CreateFromYawPitchRoll(euler.Y * MathF.PI / 180,
            euler.X * MathF.PI / 180, euler.Z * MathF.PI / 180);

    private static Vector3 ToVector(WorldVector3 vector) =>
        new(vector.X, vector.Y, vector.Z);

    private static MeshGeometry ResolveGeometry(WorldEntity entity, AssetRegistry? assets)
    {
        if (assets is null)
        {
            if (string.Equals(entity.Asset, "engine://meshes/cube",
                StringComparison.OrdinalIgnoreCase))
                return new MeshGeometry(CubeVertices, CubeTriangles, Vector4.One);
            if (string.Equals(entity.Asset, "engine://meshes/plane",
                StringComparison.OrdinalIgnoreCase))
                return new MeshGeometry(PlaneVertices, PlaneTriangles, Vector4.One);
            return new MeshGeometry([], [], Vector4.One,
                $"Mesh '{entity.Asset}' is unavailable without an asset registry");
        }

        var cache = AssetCaches.GetOrCreateValue(assets);
        return cache.GetOrAdd(entity.Asset + "|" + entity.Material, _ =>
        {
            try
            {
                var mesh = AssetContent.LoadMesh(assets, entity.Asset);
                var materialId = entity.Material.Length > 0 ? entity.Material :
                    mesh.DefaultMaterial;
                var baseColor = Vector4.One;
                if (!string.IsNullOrWhiteSpace(materialId))
                {
                    var material = AssetContent.LoadMaterial(assets, materialId);
                    baseColor = new Vector4(material.BaseColor.R, material.BaseColor.G,
                        material.BaseColor.B, material.BaseColor.A);
                }
                return new MeshGeometry(mesh.Positions.Select(p =>
                    new Vector3(p.X, p.Y, p.Z)).ToArray(), mesh.Indices.ToArray(), baseColor);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or
                IOException or InvalidDataException or JsonException or
                UnauthorizedAccessException or InvalidOperationException)
            {
                return new MeshGeometry([], [], Vector4.One, exception.Message);
            }
        });
    }

    public static void InvalidateAssets(AssetRegistry assets) => AssetCaches.Remove(assets);

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsFinite(Vector4 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) &&
            float.IsFinite(v.W);

    private static float ClipDistance(Vector4 v, int plane) => plane switch
    {
        0 => v.X + v.W,
        1 => v.W - v.X,
        2 => v.Y + v.W,
        3 => v.W - v.Y,
        4 => v.Z,
        _ => v.W - v.Z,
    };

    private static List<Vector4> ClipPolygon(IEnumerable<Vector4> vertices)
    {
        var result = vertices.ToList();
        if (result.Any(v => !IsFinite(v))) return new List<Vector4>();
        for (var plane = 0; plane < 6 && result.Count > 0; plane++)
        {
            var input = result;
            result = new List<Vector4>();
            var previous = input[^1];
            var previousDistance = ClipDistance(previous, plane);
            foreach (var vertex in input)
            {
                var distance = ClipDistance(vertex, plane);
                if ((distance >= 0) != (previousDistance >= 0))
                {
                    var t = previousDistance / (previousDistance - distance);
                    result.Add(Vector4.Lerp(previous, vertex, t));
                }
                if (distance >= 0) result.Add(vertex);
                previous = vertex;
                previousDistance = distance;
            }
        }
        result.RemoveAll(v => !IsFinite(v) || v.W < 1e-6f);
        return result;
    }

    private static void AddLine(List<WorldViewportVertex> lines, Vector3 a,
        Vector3 b, Matrix4x4 matrix, UiRect bounds, Vector4 color)
    {
        var start = Vector4.Transform(new Vector4(a, 1), matrix);
        var end = Vector4.Transform(new Vector4(b, 1), matrix);
        if (!IsFinite(start) || !IsFinite(end)) return;
        for (var plane = 0; plane < 6; plane++)
        {
            var d0 = ClipDistance(start, plane);
            var d1 = ClipDistance(end, plane);
            if (d0 < 0 && d1 < 0) return;
            if ((d0 >= 0) == (d1 >= 0)) continue;
            var point = Vector4.Lerp(start, end, d0 / (d0 - d1));
            if (d0 < 0) start = point; else end = point;
        }
        if (start.W < 1e-6f || end.W < 1e-6f) return;
        var first = ToVertex(start, bounds, color);
        var second = ToVertex(end, bounds, color);
        lines.Add(first with { Z = Math.Max(0, first.Z - 0.00005f) });
        lines.Add(second with { Z = Math.Max(0, second.Z - 0.00005f) });
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

    private static WorldViewportVertex ToVertex(Vector4 clip, UiRect bounds,
        Vector4 color) => new((clip.X / clip.W + 1) * 0.5f * bounds.Width,
            (1 - clip.Y / clip.W) * 0.5f * bounds.Height,
            Math.Clamp(clip.Z / clip.W, 0, 1), color.X, color.Y, color.Z, color.W);
}
