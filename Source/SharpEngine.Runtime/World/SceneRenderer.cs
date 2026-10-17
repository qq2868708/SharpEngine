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

/// <summary>One native-friendly vertex: relative viewport pixels, 0..1 depth,
/// and RGBA. The Editor OpenGL and Game Vulkan backends consume the same data.</summary>
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

public static class SceneRenderer
{
    private sealed record SkinBone(string Id, string? ParentId, BoneTransform BindTransform);
    private sealed record SkinData(SkinBone[] Bones, int[][] BoneIndices, float[][] Weights);
    private sealed record MeshGeometry(Vector3[] Positions, int[] Indices,
        Vector4 BaseColor, SkinData? Skin = null, string? Error = null);
    private static readonly ConditionalWeakTable<AssetRegistry,
        ConcurrentDictionary<string, MeshGeometry>> AssetCaches = new();
    private static readonly ConditionalWeakTable<AssetRegistry,
        ConcurrentDictionary<string, SkeletalAnimationAsset>> AnimationCaches = new();
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
        UiRect bounds, AssetRegistry? assets = null, string? selectedEntityId = null)
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
        var lights = world.Entities.SelectMany(e => e.RenderViews("engine.directional-light"))
            .Where(entity => entity.HasEnabledComponent("engine.directional-light") && entity.Intensity > 0)
            .Select(light =>
            {
                var color = new Vector3(light.Color.R, light.Color.G, light.Color.B) / 255f;
                return (Direction: WorldDirection(world, light, -Vector3.UnitZ),
                    Radiance: color * light.Intensity);
            })
            .ToArray();

        foreach (var (entity, componentType) in Renderables(world))
        {
            var geometry = ResolveGeometry(entity, componentType, assets);
            if (geometry.Error is not null)
            {
                diagnostics.Add($"{entity.Id}: {geometry.Error}");
                continue;
            }
            var vertices = ApplySkin(geometry, entity, componentType, assets);
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
        foreach (var (candidate, componentType) in Renderables(world))
        {
            var geometry = ResolveGeometry(candidate, componentType, assets);
            if (geometry.Error is not null) continue;
            var vertices = ApplySkin(geometry, candidate, componentType, assets);
            if (!Matrix4x4.Invert(world.WorldMatrix(candidate), out var modelInverse)) continue;
            var localOrigin = Vector3.Transform(origin, modelInverse);
            var localDirection = Vector3.TransformNormal(direction, modelInverse);
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
                var skin = componentType == "engine.skeletal-mesh"
                    ? ReadSkin(mesh) : null;
                return new MeshGeometry(positions, indices, baseColor, skin);
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
        entity.Components.FirstOrDefault(c => c.TypeId == type)?.State;

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

    private static Vector3 ReadVector3(object value)
    {
        if (value is AssetVector3 vector) return new(vector.X, vector.Y, vector.Z);
        return new(Convert.ToSingle(GetProperty(value, "X")), Convert.ToSingle(GetProperty(value, "Y")),
            Convert.ToSingle(GetProperty(value, "Z")));
    }

    private static SkinData? ReadSkin(object mesh)
    {
        var rawBones = GetEnumerable(mesh, "Bones").ToArray();
        var rawInfluences = GetEnumerable(mesh, "Influences").ToArray();
        if (rawBones.Length == 0 || rawInfluences.Length == 0) return null;
        var bones = rawBones.Select(ReadBone).ToArray();
        var indices = new int[rawInfluences.Length][];
        var weights = new float[rawInfluences.Length][];
        for (var i = 0; i < rawInfluences.Length; i++)
        {
            var raw = rawInfluences[i];
            indices[i] = ReadNumbers(raw, "BoneIndices", "Indices").Select(ReadInt).ToArray();
            weights[i] = ReadNumbers(raw, "Weights").Select(Convert.ToSingle).ToArray();
            if (indices[i].Length != weights[i].Length)
                throw new InvalidDataException("Skeletal mesh influence indices and weights differ");
        }
        return new SkinData(bones, indices, weights);
    }

    private static SkinBone ReadBone(object value)
    {
        var id = GetString(value, "BoneId");
        var parent = GetProperty(value, "ParentBoneId") as string;
        var rawTransform = GetProperty(value, "BindTransform");
        if (rawTransform is null) throw new InvalidDataException($"Bone '{id}' has no bind transform");
        return new SkinBone(id, parent, ReadTransform(rawTransform));
    }

    private static BoneTransform ReadTransform(object value)
    {
        var position = ReadVector3(GetProperty(value, "Position") ?? throw new InvalidDataException("Transform position is missing"));
        var rotation = ReadQuaternion(GetProperty(value, "Rotation") ?? throw new InvalidDataException("Transform rotation is missing"));
        var scale = ReadVector3(GetProperty(value, "Scale") ?? throw new InvalidDataException("Transform scale is missing"));
        return new BoneTransform(position, rotation, scale);
    }

    private static Quaternion ReadQuaternion(object value) => new(
        Convert.ToSingle(GetProperty(value, "X")), Convert.ToSingle(GetProperty(value, "Y")),
        Convert.ToSingle(GetProperty(value, "Z")), Convert.ToSingle(GetProperty(value, "W")));

    private static IEnumerable<object> ReadNumbers(object value, params string[] names)
    {
        foreach (var name in names)
            if (GetProperty(value, name) is IEnumerable values)
                return values.Cast<object>();
        throw new InvalidDataException("Skeletal mesh influence data is missing");
    }

    private static Vector3[] ApplySkin(MeshGeometry geometry, WorldEntity entity,
        string componentType, AssetRegistry? assets)
    {
        if (geometry.Skin is null || assets is null || componentType != "engine.skeletal-mesh")
            return geometry.Positions;
        var result = geometry.Positions.ToArray();
        var state = ComponentState(entity, componentType);
        var animationId = StateString(state, "Animation");
        SkeletalAnimationAsset? animation = null;
        if (animationId.Length > 0)
        {
            try
            {
                var cache = AnimationCaches.GetOrCreateValue(assets);
                animation = cache.GetOrAdd(animationId!, id => AssetContent.LoadSkeletalAnimation(assets, id));
            }
            catch (Exception exception) when (exception is KeyNotFoundException or IOException or
                InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The mesh remains in bind pose when an optional animation is unavailable.
            }
        }
        SkeletalPose? pose = null;
        if (animation is not null)
        {
            try
            {
                var time = state?["AnimationTime"]?.GetValue<float>() ?? 0;
                if (!float.IsFinite(time)) time = 0;
                pose = (state?["LoopAnimation"]?.GetValue<bool>() ?? true)
                    ? animation.SampleLooped(time) : animation.Sample(time);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                // Invalid optional animation state leaves the mesh in bind pose.
            }
        }
        var bind = new Matrix4x4[geometry.Skin.Bones.Length];
        var current = new Matrix4x4[geometry.Skin.Bones.Length];
        var visiting = new HashSet<int>();
        for (var i = 0; i < geometry.Skin.Bones.Length; i++)
            ComputeBoneMatrices(i, geometry.Skin.Bones, pose, bind, current, visiting);
        var skinMatrices = new Matrix4x4[bind.Length];
        for (var i = 0; i < bind.Length; i++)
            skinMatrices[i] = Matrix4x4.Invert(bind[i], out var inverse) ? inverse * current[i] : Matrix4x4.Identity;
        for (var vertex = 0; vertex < result.Length && vertex < geometry.Skin.BoneIndices.Length; vertex++)
        {
            var total = 0f;
            var skinned = Vector3.Zero;
            var indices = geometry.Skin.BoneIndices[vertex];
            var weights = geometry.Skin.Weights[vertex];
            for (var influence = 0; influence < indices.Length; influence++)
            {
                var bone = indices[influence];
                var weight = weights[influence];
                if (bone < 0 || bone >= skinMatrices.Length || !float.IsFinite(weight) || weight <= 0) continue;
                skinned += Vector3.Transform(result[vertex], skinMatrices[bone]) * weight;
                total += weight;
            }
            if (total > 1e-6f) result[vertex] = skinned / total;
        }
        return result;
    }

    private static void ComputeBoneMatrices(int index, SkinBone[] bones, SkeletalPose? pose,
        Matrix4x4[] bind, Matrix4x4[] current, HashSet<int> visiting)
    {
        if (bind[index] != default) return;
        if (!visiting.Add(index)) throw new InvalidDataException("Skeletal mesh bone parent cycle");
        var bone = bones[index];
        var parent = bone.ParentId is null ? -1 : Array.FindIndex(bones,
            candidate => string.Equals(candidate.Id, bone.ParentId, StringComparison.Ordinal));
        if (parent >= 0) ComputeBoneMatrices(parent, bones, pose, bind, current, visiting);
        bind[index] = LocalMatrix(bone.BindTransform) * (parent >= 0 ? bind[parent] : Matrix4x4.Identity);
        var transform = pose is null ? bone.BindTransform :
            pose.Bones.FirstOrDefault(item => string.Equals(item.Item1, bone.Id, StringComparison.Ordinal),
                (BoneId: bone.Id, Transform: bone.BindTransform)).Transform;
        current[index] = LocalMatrix(transform) * (parent >= 0 ? current[parent] : Matrix4x4.Identity);
        visiting.Remove(index);
    }

    private static Matrix4x4 LocalMatrix(BoneTransform transform) =>
        Matrix4x4.CreateScale(transform.Scale) * Matrix4x4.CreateFromQuaternion(transform.Rotation) *
        Matrix4x4.CreateTranslation(transform.Position);

    public static void InvalidateAssets(AssetRegistry assets)
    {
        AssetCaches.Remove(assets);
        AnimationCaches.Remove(assets);
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

    internal static void AddLine(List<WorldViewportVertex> lines, Vector3 a,
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

    internal static WorldViewportVertex ToVertex(Vector4 clip, UiRect bounds,
        Vector4 color) => new((clip.X / clip.W + 1) * 0.5f * bounds.Width,
            (1 - clip.Y / clip.W) * 0.5f * bounds.Height,
            Math.Clamp(clip.Z / clip.W, 0, 1), color.X, color.Y, color.Z, color.W);
}
