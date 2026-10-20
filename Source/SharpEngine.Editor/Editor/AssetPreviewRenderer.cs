using System.Numerics;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Mathematics;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Editor;

internal static class AssetPreviewRenderer
{
    private static readonly Vector3 LightDirection = Vector3.Normalize(
        new Vector3(0.35f, 0.8f, 0.45f));

    public static WorldViewportFrame BuildMeshFrame(
        IReadOnlyList<AssetVector3> SourcePositions, IReadOnlyList<int> Indices,
        UiRect Bounds, Vector4 BaseColor, string CameraId)
    {
        var Positions = SourcePositions.Select(Position =>
            new Vector3(Position.X, Position.Y, Position.Z)).ToArray();
        return BuildMeshFrame(Positions, Indices, Bounds, BaseColor, CameraId);
    }

    public static WorldViewportFrame BuildMeshFrame(
        IReadOnlyList<Vector3> Positions, IReadOnlyList<int> Indices,
        UiRect Bounds, Vector4 BaseColor, string CameraId)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || Positions.Count < 3)
        {
            return new WorldViewportFrame(Bounds, [], [], 1, 0, CameraId);
        }

        var ViewProjection = BuildViewProjection(Positions, Bounds);
        var Triangles = new List<WorldViewportVertex>(Indices.Count);
        for (var Index = 0; Index + 2 < Indices.Count; Index += 3)
        {
            var Position0 = Positions[Indices[Index]];
            var Position1 = Positions[Indices[Index + 1]];
            var Position2 = Positions[Indices[Index + 2]];
            var Normal = Vector3.Cross(Position1 - Position0, Position2 - Position0);
            if (Normal.LengthSquared() < 1e-8f)
            {
                continue;
            }

            Normal = Vector3.Normalize(Normal);
            var Diffuse = MathF.Max(0, Vector3.Dot(Normal, LightDirection));
            var Lighting = 0.22f + Diffuse * 0.78f;
            var Color = new Vector4(BaseColor.X * Lighting,
                BaseColor.Y * Lighting, BaseColor.Z * Lighting, BaseColor.W);
            var Clip0 = Vector4.Transform(new Vector4(Position0, 1), ViewProjection);
            var Clip1 = Vector4.Transform(new Vector4(Position1, 1), ViewProjection);
            var Clip2 = Vector4.Transform(new Vector4(Position2, 1), ViewProjection);
            if (!CanProject(Clip0) || !CanProject(Clip1) || !CanProject(Clip2))
            {
                continue;
            }

            Triangles.Add(ToVertex(Clip0, Bounds, Color));
            Triangles.Add(ToVertex(Clip1, Bounds, Color));
            Triangles.Add(ToVertex(Clip2, Bounds, Color));
        }

        return new WorldViewportFrame(Bounds, Triangles.ToArray(), [], 1, 1, CameraId);
    }

    public static WorldViewportFrame BuildSkeletonFrame(SkeletonAsset Skeleton,
        UiRect Bounds)
    {
        var WorldTransforms = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        var Bones = Skeleton.Bones.ToDictionary(Bone => Bone.BoneId,
            StringComparer.Ordinal);
        Matrix4x4 Resolve(SkeletonBone Bone)
        {
            if (WorldTransforms.TryGetValue(Bone.BoneId, out var Existing))
            {
                return Existing;
            }

            var Local = TransformMath.Trs(Bone.BindTransform.Position,
                Bone.BindTransform.Rotation, Bone.BindTransform.Scale);
            var Parent = Bone.ParentBoneId is null ? Matrix4x4.Identity :
                Resolve(Bones[Bone.ParentBoneId]);
            var World = Local * Parent;
            WorldTransforms.Add(Bone.BoneId, World);
            return World;
        }

        foreach (var Bone in Skeleton.Bones)
        {
            Resolve(Bone);
        }

        var WorldPositions = WorldTransforms.ToDictionary(Pair => Pair.Key,
            Pair => Pair.Value.Translation, StringComparer.Ordinal);
        var Positions = WorldPositions.Values.ToArray();
        if (Positions.Length == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return new WorldViewportFrame(Bounds, [], [], 1, 0,
                "skeleton-preview-camera");
        }

        var ViewProjection = BuildViewProjection(Positions, Bounds);
        var Lines = new List<WorldViewportVertex>();
        var Color = new Vector4(0.91f, 0.72f, 0.35f, 1);
        foreach (var Bone in Skeleton.Bones)
        {
            if (Bone.ParentBoneId is null)
            {
                continue;
            }

            var Child = Vector4.Transform(new Vector4(WorldPositions[Bone.BoneId], 1),
                ViewProjection);
            var Parent = Vector4.Transform(new Vector4(WorldPositions[Bone.ParentBoneId], 1),
                ViewProjection);
            if (!CanProject(Child) || !CanProject(Parent))
            {
                continue;
            }
            Lines.Add(ToVertex(Parent, Bounds, Color));
            Lines.Add(ToVertex(Child, Bounds, Color));
        }

        return new WorldViewportFrame(Bounds, [], Lines.ToArray(), 1, 0,
            "skeleton-preview-camera");
    }

    private static Matrix4x4 BuildViewProjection(IReadOnlyList<Vector3> Positions,
        UiRect Bounds)
    {
        var Minimum = new Vector3(float.MaxValue);
        var Maximum = new Vector3(float.MinValue);
        foreach (var Position in Positions)
        {
            Minimum = Vector3.Min(Minimum, Position);
            Maximum = Vector3.Max(Maximum, Position);
        }

        var Center = (Minimum + Maximum) * 0.5f;
        var Extents = Vector3.Max(Maximum - Minimum, new Vector3(0.001f));
        var Aspect = Bounds.Width / Bounds.Height;
        var HalfFov = 35 * MathF.PI / 360;
        var FitHeight = Extents.Y * 0.5f / MathF.Tan(HalfFov);
        var FitWidth = Extents.X * 0.5f / MathF.Max(Aspect, 0.01f) /
            MathF.Tan(HalfFov);
        var Distance = MathF.Max(MathF.Max(FitHeight, FitWidth), Extents.Z * 0.5f) *
            1.45f;
        Distance = MathF.Max(Distance, 0.5f);
        var CameraOffset = Vector3.Normalize(new Vector3(1.25f, 0.65f, 1.65f)) *
            Distance;
        var View = Matrix4x4.CreateLookAt(Center + CameraOffset, Center, Vector3.UnitY);
        var Projection = Matrix4x4.CreatePerspectiveFieldOfView(35 * MathF.PI / 180,
            Aspect, MathF.Max(0.01f, Distance * 0.01f),
            MathF.Max(10, Distance * 4 + Extents.Length()));
        return View * Projection;
    }

    private static bool CanProject(Vector4 Value) => Value.W > 0.001f &&
        float.IsFinite(Value.X) && float.IsFinite(Value.Y) &&
        float.IsFinite(Value.Z) && float.IsFinite(Value.W);

    private static WorldViewportVertex ToVertex(Vector4 Clip, UiRect Bounds,
        Vector4 Color)
    {
        var ReciprocalW = 1 / Clip.W;
        return new WorldViewportVertex(
            (Clip.X * ReciprocalW + 1) * 0.5f * Bounds.Width,
            (1 - Clip.Y * ReciprocalW) * 0.5f * Bounds.Height,
            Math.Clamp(Clip.Z * ReciprocalW, 0, 1),
            Color.X, Color.Y, Color.Z, Color.W);
    }
}
