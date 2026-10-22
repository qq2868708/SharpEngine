using SharpEngine.Rendering;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Animation;
using SharpEngine.UI;

namespace SharpEngine.World;

public sealed record WorldViewportFrame(UiRect Bounds,
    ViewportVertex[] Triangles, ViewportVertex[] Lines,
    int EntityCount, int MeshCount, string CameraId) : ViewportFrame(Bounds, Triangles, Lines);

public static class SceneRenderer
{
    private sealed record MeshGeometry(Vector3[] Positions, int[] Indices,
        Vector4 BaseColor, string? Error = null);
    private enum TriangleClipState
    {
        Outside,
        Inside,
        Intersecting,
    }
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
        UiRect bounds, AssetRegistry? assets = null, string? selectedEntityId = null,
        Camera3D? ViewCamera = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        var Triangles = new List<ViewportVertex>();
        var Lines = new List<ViewportVertex>();
        var Diagnostics = new List<string>();
        var Camera = world.ActiveCamera;
        if ((Camera is null && ViewCamera is null) || bounds.Width <= 0 || bounds.Height <= 0 ||
            !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height))
            return new WorldViewportFrame(bounds, [], [], world.Entities.Count,
                world.MeshCount, Camera?.Id ?? string.Empty);

        var ViewProjection = ViewCamera?.ViewProjection(bounds.Width / bounds.Height) ??
            CameraMatrix(world, Camera!, bounds.Width / bounds.Height);
        var Lights = world.Entities.SelectMany(Entity => Entity.RenderViews("engine.directional-light"))
            .Where(Entity => Entity.HasEnabledComponent("engine.directional-light") && Entity.Intensity > 0)
            .Select(Light =>
            {
                return (Direction: WorldDirection(world, Light, -Vector3.UnitZ),
                    Radiance: Light.LinearColor("engine.directional-light") *
                        Light.Intensity);
            })
            .ToArray();
        var SkyRadiance = ResolveSkyRadiance(world);
        Span<Vector4> ClippedPolygon = stackalloc Vector4[12];
        Span<Vector4> ClipScratch = stackalloc Vector4[12];

        foreach (var (Entity, ComponentType) in Renderables(world))
        {
            var ComponentId = Entity.Record.Components.First(Component => Component.TypeId == ComponentType).ComponentId;
            var SkeletonState = world.RenderStates.GetValueOrDefault(ComponentId) as SkeletonFrameState;
            if (SkeletonState is not null && SkeletonState.Code != "SKELETON_READY")
            {
                Diagnostics.Add($"{Entity.Id}: {SkeletonState.Code}: {SkeletonState.Message}");
            }
            var Geometry = ResolveGeometry(Entity, ComponentType, assets);
            if (Geometry.Error is not null)
            {
                Diagnostics.Add($"{Entity.Id}: {Geometry.Error}");
                continue;
            }
            var Vertices = PreparedPositions(world, Geometry, Entity, ComponentType);
            var Indices = Geometry.Indices;
            var Model = world.WorldMatrix(Entity);
            var ModelViewProjection = Model * ViewProjection;
            var WorldPositions = new Vector3[Vertices.Count];
            var ClipPositions = new Vector4[Vertices.Count];
            for (var Index = 0; Index < Vertices.Count; Index++)
            {
                var Position = Vertices[Index];
                WorldPositions[Index] = Vector3.Transform(Position, Model);
                ClipPositions[Index] = Vector4.Transform(new Vector4(Position, 1),
                    ModelViewProjection);
            }
            Triangles.EnsureCapacity(Triangles.Count + Indices.Length);
            var SurfaceColor = new Vector3(Entity.Color.R / 255f * Geometry.BaseColor.X,
                Entity.Color.G / 255f * Geometry.BaseColor.Y,
                Entity.Color.B / 255f * Geometry.BaseColor.Z);
            for (var Index = 0; Index < Indices.Length; Index += 3)
            {
                var FirstIndex = Indices[Index];
                var SecondIndex = Indices[Index + 1];
                var ThirdIndex = Indices[Index + 2];
                var WorldFirst = WorldPositions[FirstIndex];
                var WorldSecond = WorldPositions[SecondIndex];
                var WorldThird = WorldPositions[ThirdIndex];
                var Normal = Vector3.Normalize(Vector3.Cross(WorldSecond - WorldFirst,
                    WorldThird - WorldFirst));
                if (!IsFinite(Normal)) continue;
                var Lighting = SkyRadiance;
                foreach (var Light in Lights)
                {
                    var Diffuse = MathF.Max(0, Vector3.Dot(Normal, -Light.Direction));
                    Lighting += Light.Radiance * Diffuse;
                }
                var Color = new Vector4(
                    Math.Clamp(SurfaceColor.X * Lighting.X, 0, 1),
                    Math.Clamp(SurfaceColor.Y * Lighting.Y, 0, 1),
                    Math.Clamp(SurfaceColor.Z * Lighting.Z, 0, 1),
                    Geometry.BaseColor.W);
                var ClipFirst = ClipPositions[FirstIndex];
                var ClipSecond = ClipPositions[SecondIndex];
                var ClipThird = ClipPositions[ThirdIndex];
                var ClipState = ClassifyTriangle(ClipFirst, ClipSecond, ClipThird);
                if (ClipState == TriangleClipState.Outside)
                {
                    continue;
                }
                if (ClipState == TriangleClipState.Inside)
                {
                    Triangles.Add(ToVertex(ClipFirst, bounds, Color));
                    Triangles.Add(ToVertex(ClipSecond, bounds, Color));
                    Triangles.Add(ToVertex(ClipThird, bounds, Color));
                    continue;
                }
                var ClippedCount = ClipTriangle(ClipFirst, ClipSecond, ClipThird,
                    ClippedPolygon, ClipScratch);
                for (var Fan = 1; Fan + 1 < ClippedCount; Fan++)
                {
                    Triangles.Add(ToVertex(ClippedPolygon[0], bounds, Color));
                    Triangles.Add(ToVertex(ClippedPolygon[Fan], bounds, Color));
                    Triangles.Add(ToVertex(ClippedPolygon[Fan + 1], bounds, Color));
                }
            }

            if (string.Equals(Entity.Id, selectedEntityId,
                StringComparison.OrdinalIgnoreCase))
            {
                for (var Index = 0; Index < Indices.Length; Index += 3)
                    for (var Edge = 0; Edge < 3; Edge++)
                        AddLine(Lines, Vertices[Indices[Index + Edge]],
                            Vertices[Indices[Index + (Edge + 1) % 3]], ModelViewProjection,
                            bounds, new Vector4(1, 0.68f, 0.18f, 1));
            }
        }

        return new WorldViewportFrame(bounds, Triangles.ToArray(), Lines.ToArray(),
            world.Entities.Count, world.MeshCount, ViewCamera is null ? Camera!.Id : "viewport-observer")
        { Diagnostics = Diagnostics.AsReadOnly() };
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
        foreach (var (candidate, componentType) in Renderables(world))
        {
            var geometry = ResolveGeometry(candidate, componentType, assets);
            if (geometry.Error is not null) continue;
            if (!Matrix4x4.Invert(world.WorldMatrix(candidate), out var modelInverse)) continue;
            var localOrigin = Vector3.Transform(origin, modelInverse);
            var localDirection = Vector3.TransformNormal(direction, modelInverse);
            if (assets is not null && componentType == "engine.static-mesh")
            {
                var AssetId = StateString(ComponentState(candidate, componentType),
                    "Mesh");
                if (AssetId.Length == 0)
                {
                    AssetId = candidate.Asset;
                }
                GpuMeshPayload? Payload = null;
                try
                {
                    Payload = GpuSceneBuilder.ResolvePayload(assets, AssetId,
                        false);
                }
                catch (Exception Exception) when (Exception is IOException or
                    InvalidDataException or KeyNotFoundException or
                    UnauthorizedAccessException)
                {
                }
                if (Payload is not null)
                {
                    if (RayBvh(localOrigin, localDirection, Payload, nearest,
                        farDistance, out var BvhDistance))
                    {
                        nearest = BvhDistance;
                        entity = candidate;
                    }
                    continue;
                }
            }
            var vertices = PreparedPositions(world, geometry, candidate, componentType);
            for (var index = 0; index < geometry.Indices.Length; index += 3)
            {
                if (RayTriangle(localOrigin, localDirection,
                    vertices[geometry.Indices[index]],
                    vertices[geometry.Indices[index + 1]],
                    vertices[geometry.Indices[index + 2]],
                    out var distance) && distance < nearest && distance <= farDistance)
                {
                    nearest = distance;
                    entity = candidate;
                }
            }
        }
        return entity is not null;
    }

    internal static Vector3 ResolveSkyRadiance(WorldDocument World)
    {
        var Radiance = Vector3.Zero;
        foreach (var Light in World.Entities.SelectMany(Entity =>
                     Entity.RenderViews("engine.sky-light")))
        {
            if (!Light.HasEnabledComponent("engine.sky-light") || Light.Intensity <= 0)
            {
                continue;
            }
            Radiance += Light.LinearColor("engine.sky-light") * Light.Intensity;
        }
        return Radiance;
    }

    private static bool RayBvh(Vector3 Origin, Vector3 Direction,
        GpuMeshPayload Payload, float Nearest, float FarDistance,
        out float Distance)
    {
        Distance = Nearest;
        if (Payload.BvhNodes.Count == 0)
        {
            return false;
        }
        var Hit = false;
        var LocalStack = new int[64];
        var OverflowStack = new Stack<int>();
        var StackCount = 1;
        LocalStack[0] = 0;
        while (StackCount > 0 || OverflowStack.Count > 0)
        {
            int NodeIndex;
            if (StackCount > 0)
            {
                NodeIndex = LocalStack[--StackCount];
            }
            else
            {
                NodeIndex = OverflowStack.Pop();
            }
            var Node = Payload.BvhNodes[NodeIndex];
            if (!RayBounds(Origin, Direction, Node.Minimum, Node.Maximum,
                Math.Min(Distance, FarDistance)))
            {
                continue;
            }
            if (Node.TriangleCount > 0)
            {
                for (var Item = Node.FirstTriangle;
                     Item < Node.FirstTriangle + Node.TriangleCount; Item++)
                {
                    var Triangle = Payload.BvhTriangles[Item];
                    var First = Payload.Vertices[Triangle * 3].Position;
                    var Second = Payload.Vertices[Triangle * 3 + 1].Position;
                    var Third = Payload.Vertices[Triangle * 3 + 2].Position;
                    if (RayTriangle(Origin, Direction, First, Second, Third,
                        out var CandidateDistance) &&
                        CandidateDistance < Distance &&
                        CandidateDistance <= FarDistance)
                    {
                        Distance = CandidateDistance;
                        Hit = true;
                    }
                }
                continue;
            }
            PushNode(Node.Left);
            PushNode(Node.Right);
        }
        return Hit;

        void PushNode(int NodeIndex)
        {
            if (NodeIndex < 0)
            {
                return;
            }
            if (StackCount < LocalStack.Length)
            {
                LocalStack[StackCount++] = NodeIndex;
            }
            else
            {
                OverflowStack.Push(NodeIndex);
            }
        }
    }

    private static bool RayBounds(Vector3 Origin, Vector3 Direction,
        Vector3 Minimum, Vector3 Maximum, float MaximumDistance)
    {
        var Near = 0f;
        var Far = MaximumDistance;
        for (var Axis = 0; Axis < 3; Axis++)
        {
            var AxisOrigin = Axis == 0 ? Origin.X : Axis == 1 ? Origin.Y :
                Origin.Z;
            var AxisDirection = Axis == 0 ? Direction.X : Axis == 1 ?
                Direction.Y : Direction.Z;
            var AxisMinimum = Axis == 0 ? Minimum.X : Axis == 1 ? Minimum.Y :
                Minimum.Z;
            var AxisMaximum = Axis == 0 ? Maximum.X : Axis == 1 ? Maximum.Y :
                Maximum.Z;
            if (Math.Abs(AxisDirection) < 1e-8f)
            {
                if (AxisOrigin < AxisMinimum || AxisOrigin > AxisMaximum)
                {
                    return false;
                }
                continue;
            }
            var Inverse = 1f / AxisDirection;
            var First = (AxisMinimum - AxisOrigin) * Inverse;
            var Second = (AxisMaximum - AxisOrigin) * Inverse;
            if (First > Second)
            {
                (First, Second) = (Second, First);
            }
            Near = Math.Max(Near, First);
            Far = Math.Min(Far, Second);
            if (Near > Far)
            {
                return false;
            }
        }
        return Far >= 0;
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

    internal static Matrix4x4 CameraMatrix(WorldDocument world, WorldEntity camera,
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

    internal static Vector3 WorldDirection(WorldDocument world, WorldEntity entity,
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

    internal static Quaternion Rotation(WorldVector3 euler) =>
        Quaternion.CreateFromYawPitchRoll(euler.Y * MathF.PI / 180,
            euler.X * MathF.PI / 180, euler.Z * MathF.PI / 180);

    internal static Vector3 ToVector(WorldVector3 vector) =>
        new(vector.X, vector.Y, vector.Z);

    private static IEnumerable<(WorldEntity Entity, string Type)> Renderables(WorldDocument world)
    {
        foreach (var entity in world.Entities)
            foreach (var type in new[] { "engine.mesh-renderer", "engine.static-mesh", "engine.skeletal-mesh" })
                foreach (var view in entity.RenderViews(type))
                    yield return (view, type);
    }

    private static MeshGeometry ResolveGeometry(WorldEntity entity, string componentType,
        AssetRegistry? assets)
    {
        var state = ComponentState(entity, componentType);
        var assetId = StateString(state, "Mesh");
        if (assetId.Length == 0) assetId = entity.Asset;
        var materialId = StateString(state, "Material");
        if (materialId.Length == 0) materialId = entity.Material;
        if (assets is null)
        {
            if (string.Equals(assetId, "engine://meshes/cube",
                StringComparison.OrdinalIgnoreCase))
                return new MeshGeometry(CubeVertices, CubeTriangles, Vector4.One);
            if (string.Equals(assetId, "engine://meshes/plane",
                StringComparison.OrdinalIgnoreCase))
                return new MeshGeometry(PlaneVertices, PlaneTriangles, Vector4.One);
            return new MeshGeometry([], [], Vector4.One,
                Error: $"Mesh '{assetId}' is unavailable without an asset registry");
        }

        var cache = AssetCaches.GetOrCreateValue(assets);
        return cache.GetOrAdd(componentType + "|" + assetId + "|" + materialId, _ =>
        {
            try
            {
                object mesh;
                if (componentType == "engine.static-mesh")
                    mesh = AssetContent.LoadStaticMesh(assets, assetId);
                else if (componentType == "engine.skeletal-mesh")
                    mesh = AssetContent.LoadSkeletalMesh(assets, assetId);
                else
                    mesh = AssetContent.LoadMesh(assets, assetId);
                var meshMaterial = GetString(mesh, "DefaultMaterial");
                var resolvedMaterial = materialId.Length > 0 ? materialId : meshMaterial;
                var baseColor = Vector4.One;
                if (!string.IsNullOrWhiteSpace(resolvedMaterial))
                {
                    var material = AssetContent.LoadMaterial(assets, resolvedMaterial);
                    baseColor = new Vector4(material.BaseColor.R, material.BaseColor.G,
                        material.BaseColor.B, material.BaseColor.A);
                }
                var positions = GetEnumerable(mesh, "Positions").Select(ReadVector3).ToArray();
                var indices = GetEnumerable(mesh, "Indices").Select(ReadInt).ToArray();
                return new MeshGeometry(positions, indices, baseColor);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or
                IOException or InvalidDataException or JsonException or
                UnauthorizedAccessException or InvalidOperationException)
            {
                return new MeshGeometry([], [], Vector4.One, Error: exception.Message);
            }
        });
    }

    private static JsonObject? ComponentState(WorldEntity entity, string type) =>
        entity.Record.Components.FirstOrDefault(c => c.TypeId == type)?.State;

    private static string StateString(JsonObject? state, string name)
    {
        var node = state?[name];
        return node is null || node.GetValueKind() == JsonValueKind.Null
            ? string.Empty : node.GetValue<string>();
    }

    private static IEnumerable<object> GetEnumerable(object value, string name) =>
        (GetProperty(value, name) as IEnumerable)?.Cast<object>() ?? [];

    private static object? GetProperty(object value, string name) =>
        value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);

    private static string GetString(object value, string name) =>
        GetProperty(value, name) as string ?? string.Empty;

    private static int ReadInt(object value) => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);

    private static Vector3 ReadVector3(object Value)
    {
        if (Value is Vector3)
        {
            return (Vector3)Value;
        }
        if (Value is AssetVector3)
        {
            var Vector = (AssetVector3)Value;
            return new(Vector.X, Vector.Y, Vector.Z);
        }
        return new(Convert.ToSingle(GetProperty(Value, "X")), Convert.ToSingle(GetProperty(Value, "Y")),
            Convert.ToSingle(GetProperty(Value, "Z")));
    }

    private static IReadOnlyList<Vector3> PreparedPositions(WorldDocument World, MeshGeometry Geometry,
        WorldEntity Entity, string ComponentType)
    {
        if (ComponentType != "engine.skeletal-mesh")
        {
            return Geometry.Positions;
        }
        var Component = Entity.Record.Components.First(Component => Component.TypeId == ComponentType);
        var State = World.RenderStates.GetValueOrDefault(Component.ComponentId) as SkeletonFrameState;
        if (State is null || State.MeshId != StateString(Component.State, "Mesh") || State.Positions.Count != Geometry.Positions.Length)
        {
            return Geometry.Positions;
        }
        return State.Positions;
    }

    public static void InvalidateAssets(AssetRegistry assets)
    {
        AssetCaches.Remove(assets);
        AnimatorContent.Invalidate(assets);
    }

    internal static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    internal static bool IsFinite(Vector4 v) =>
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

    private static TriangleClipState ClassifyTriangle(Vector4 First, Vector4 Second,
        Vector4 Third)
    {
        if (!IsFinite(First) || !IsFinite(Second) || !IsFinite(Third))
        {
            return TriangleClipState.Outside;
        }
        var Intersecting = First.W < 1e-6f || Second.W < 1e-6f || Third.W < 1e-6f;
        for (var Plane = 0; Plane < 6; Plane++)
        {
            var FirstInside = ClipDistance(First, Plane) >= 0;
            var SecondInside = ClipDistance(Second, Plane) >= 0;
            var ThirdInside = ClipDistance(Third, Plane) >= 0;
            if (!FirstInside && !SecondInside && !ThirdInside)
            {
                return TriangleClipState.Outside;
            }
            Intersecting |= !FirstInside || !SecondInside || !ThirdInside;
        }
        return Intersecting ? TriangleClipState.Intersecting : TriangleClipState.Inside;
    }

    private static int ClipTriangle(Vector4 First, Vector4 Second, Vector4 Third,
        Span<Vector4> Result, Span<Vector4> Scratch)
    {
        Result[0] = First;
        Result[1] = Second;
        Result[2] = Third;
        var InputCount = 3;
        var InputIsResult = true;
        for (var Plane = 0; Plane < 6 && InputCount > 0; Plane++)
        {
            var Input = InputIsResult ? Result : Scratch;
            var Output = InputIsResult ? Scratch : Result;
            var OutputCount = 0;
            var Previous = Input[InputCount - 1];
            var PreviousDistance = ClipDistance(Previous, Plane);
            for (var Index = 0; Index < InputCount; Index++)
            {
                var Vertex = Input[Index];
                var Distance = ClipDistance(Vertex, Plane);
                if ((Distance >= 0) != (PreviousDistance >= 0))
                {
                    var Interpolation = PreviousDistance / (PreviousDistance - Distance);
                    Output[OutputCount++] = Vector4.Lerp(Previous, Vertex, Interpolation);
                }
                if (Distance >= 0)
                {
                    Output[OutputCount++] = Vertex;
                }
                Previous = Vertex;
                PreviousDistance = Distance;
            }
            InputCount = OutputCount;
            InputIsResult = !InputIsResult;
        }
        var Final = InputIsResult ? Result : Scratch;
        var ResultCount = 0;
        for (var Index = 0; Index < InputCount; Index++)
        {
            var Vertex = Final[Index];
            if (IsFinite(Vertex) && Vertex.W >= 1e-6f)
            {
                Result[ResultCount++] = Vertex;
            }
        }
        return ResultCount;
    }

    internal static void AddLine(List<ViewportVertex> lines, Vector3 a,
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

    internal static ViewportVertex ToVertex(Vector4 clip, UiRect bounds,
        Vector4 color) => new((clip.X / clip.W + 1) * 0.5f * bounds.Width,
            (1 - clip.Y / clip.W) * 0.5f * bounds.Height,
            Math.Clamp(clip.Z / clip.W, 0, 1), color.X, color.Y, color.Z, color.W);
}
