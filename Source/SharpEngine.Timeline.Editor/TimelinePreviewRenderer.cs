using System.Numerics;
using SharpEngine.Animation;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Timeline;

internal static class TimelinePreviewRenderer
{
    private static readonly Vector3 LightDirection =
        Vector3.Normalize(new Vector3(0.35f, 0.8f, 0.45f));

    public static WorldViewportFrame BuildFrame(SkeletalMeshAsset Mesh,
        SkeletalAnimationAsset Animation, SkeletalPose Pose, UiRect Bounds)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || Mesh.Positions.Count < 3)
        {
            return new WorldViewportFrame(Bounds, [], [], 1, 0,
                "timeline-preview-camera");
        }

        var Prepared = new SkeletonInstance(Mesh).Prepare(
            "timeline-preview", "timeline-preview", Pose, "SKELETON_READY");
        var Positions = Prepared.Positions;
        var (Minimum, Maximum) = GetBounds(Positions);
        var Center = (Minimum + Maximum) * 0.5f;
        var Extents = Vector3.Max(Maximum - Minimum, new Vector3(0.001f));
        var Aspect = Bounds.Width / Bounds.Height;
        var HalfFov = 35 * MathF.PI / 360;
        var FitHeight = Extents.Y * 0.5f / MathF.Tan(HalfFov);
        var FitWidth = Extents.X * 0.5f / MathF.Max(Aspect, 0.01f) /
            MathF.Tan(HalfFov);
        var Distance = MathF.Max(MathF.Max(FitHeight, FitWidth), Extents.Z * 0.5f) * 1.35f;
        var CameraOffset = Vector3.Normalize(new Vector3(1.25f, 0.6f, 1.65f)) * Distance;
        var CameraPosition = Center + CameraOffset;
        var View = Matrix4x4.CreateLookAt(CameraPosition, Center, Vector3.UnitY);
        var Projection = Matrix4x4.CreatePerspectiveFieldOfView(
            35 * MathF.PI / 180, Aspect, MathF.Max(0.01f, Distance * 0.01f),
            MathF.Max(10, Distance * 4 + Extents.Length()));
        var ViewProjection = View * Projection;
        var Triangles = new List<WorldViewportVertex>(Mesh.Indices.Count);

        for (var Index = 0; Index + 2 < Mesh.Indices.Count; Index += 3)
        {
            var Position0 = Positions[Mesh.Indices[Index]];
            var Position1 = Positions[Mesh.Indices[Index + 1]];
            var Position2 = Positions[Mesh.Indices[Index + 2]];
            var Normal = Vector3.Cross(Position1 - Position0, Position2 - Position0);
            if (Normal.LengthSquared() < 1e-8f)
            {
                continue;
            }
            Normal = Vector3.Normalize(Normal);
            var Diffuse = MathF.Max(0, Vector3.Dot(Normal, LightDirection));
            var Lighting = 0.22f + Diffuse * 0.78f;
            var Color = new Vector4(0.58f * Lighting, 0.7f * Lighting,
                0.86f * Lighting, 1);
            var Clip0 = Vector4.Transform(new Vector4(Position0, 1), ViewProjection);
            var Clip1 = Vector4.Transform(new Vector4(Position1, 1), ViewProjection);
            var Clip2 = Vector4.Transform(new Vector4(Position2, 1), ViewProjection);
            if (Clip0.W <= 0.001f || Clip1.W <= 0.001f || Clip2.W <= 0.001f ||
                !IsFinite(Clip0) || !IsFinite(Clip1) || !IsFinite(Clip2))
            {
                continue;
            }
            Triangles.Add(ToVertex(Clip0, Bounds, Color));
            Triangles.Add(ToVertex(Clip1, Bounds, Color));
            Triangles.Add(ToVertex(Clip2, Bounds, Color));
        }

        return new WorldViewportFrame(Bounds, Triangles.ToArray(), [], 1, 1,
            $"timeline-preview-camera:{Animation.AssetId}");
    }

    private static (Vector3 Minimum, Vector3 Maximum) GetBounds(
        IReadOnlyList<Vector3> Positions)
    {
        var Minimum = new Vector3(float.MaxValue);
        var Maximum = new Vector3(float.MinValue);
        foreach (var Position in Positions)
        {
            Minimum = Vector3.Min(Minimum, Position);
            Maximum = Vector3.Max(Maximum, Position);
        }
        return (Minimum, Maximum);
    }

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

    private static bool IsFinite(Vector4 Value) =>
        float.IsFinite(Value.X) && float.IsFinite(Value.Y) &&
        float.IsFinite(Value.Z) && float.IsFinite(Value.W);
}
