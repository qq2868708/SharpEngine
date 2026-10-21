using SharpEngine.Rendering;
using System.Numerics;
using SharpEngine.Assets;
using SharpEngine.UI;

namespace SharpEngine.Editor;

internal static class MaterialPreviewRenderer
{
    private static readonly Vector3 LightDirection = Vector3.Normalize(
        new Vector3(0.35f, 0.8f, 0.45f));

    public static ViewportFrame BuildMeshFrame(
        IReadOnlyList<AssetVector3> SourcePositions, IReadOnlyList<int> Indices,
        UiRect Bounds, Vector4 BaseColor, Camera3D Camera)
    {
        var Positions = SourcePositions.Select(Position =>
            new Vector3(Position.X, Position.Y, Position.Z)).ToArray();
        return BuildMeshFrame(Positions, Indices, Bounds, BaseColor, Camera);
    }

    public static ViewportFrame BuildMeshFrame(
        IReadOnlyList<Vector3> Positions, IReadOnlyList<int> Indices,
        UiRect Bounds, Vector4 BaseColor, Camera3D Camera)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || Positions.Count < 3)
        {
            return new ViewportFrame(Bounds, [], []);
        }

        var ViewProjection = Camera.ViewProjection(Bounds.Width / Bounds.Height);
        var Triangles = new List<ViewportVertex>(Indices.Count);
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

        return new ViewportFrame(Bounds, Triangles.ToArray(), []);
    }

    private static bool CanProject(Vector4 Value) => Value.W > 0.001f &&
        float.IsFinite(Value.X) && float.IsFinite(Value.Y) &&
        float.IsFinite(Value.Z) && float.IsFinite(Value.W);

    private static ViewportVertex ToVertex(Vector4 Clip, UiRect Bounds,
        Vector4 Color)
    {
        var ReciprocalW = 1 / Clip.W;
        return new ViewportVertex(
            (Clip.X * ReciprocalW + 1) * 0.5f * Bounds.Width,
            (1 - Clip.Y * ReciprocalW) * 0.5f * Bounds.Height,
            Math.Clamp(Clip.Z * ReciprocalW, 0, 1),
            Color.X, Color.Y, Color.Z, Color.W);
    }
}
