using System.Numerics;
using SharpEngine.Assets;

internal static class EngineCubeValidation
{
    public static void Run(string Root, AssetRegistry Registry, Action<bool, string> Check)
    {
        var ContentRoot = Path.Combine(Root, "Content");
        var SourceFiles = Directory.Exists(ContentRoot)
            ? Directory.EnumerateFiles(ContentRoot, "*", SearchOption.AllDirectories)
                .Where(Path => string.Equals(System.IO.Path.GetExtension(Path), ".blend",
                    StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetExtension(Path), ".fbx",
                        StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<string>();
        Check(SourceFiles.Length == 0, "Content contains no Blender or FBX delivery sources");
        Check(Registry.Assets.All(Asset =>
            !string.Equals(Asset.Type, "BlenderSource", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Asset.Type, "FBXSource", StringComparison.OrdinalIgnoreCase)),
            "Asset manifest contains only formal engine assets");

        ValidateCube(Registry, Check);
        ValidateSphere(Registry, Check);
        ValidateCapsule(Registry, Check);
    }

    private static void ValidateCube(AssetRegistry Registry, Action<bool, string> Check)
    {
        var Cube = LoadPrimitive(Registry, "engine://meshes/cube", "Cube", Check);
        Check(Cube.Positions.Count == 8 && Cube.Indices.Count == 36 && Cube.CoordinateSystem == "Y-up",
            "Cube has eight corners, twelve triangles and Y-up geometry");
        Check(Cube.SourcePath is null && Cube.SourceSha256 is null,
            "Cube is a self-contained formal asset without source provenance");
        CheckBounds(Cube, new Vector3(-0.5f), new Vector3(0.5f), Check,
            "Cube preserves centred one-unit dimensions");
        CheckOutwardWinding(Cube, Check, "Cube triangles face outward");
    }

    private static void ValidateSphere(AssetRegistry Registry, Action<bool, string> Check)
    {
        var Sphere = LoadPrimitive(Registry, "engine://meshes/sphere", "Sphere", Check);
        Check(Sphere.Positions.Count == 114 && Sphere.Indices.Count == 672 &&
            Sphere.CoordinateSystem == "Y-up", "Sphere has the expected indexed geometry and Y-up coordinates");
        Check(Sphere.SourcePath is null && Sphere.SourceSha256 is null,
            "Sphere is a self-contained formal asset without source provenance");
        CheckBounds(Sphere, new Vector3(-0.5f), new Vector3(0.5f), Check,
            "Sphere preserves a centred one-metre diameter");
        Check(Sphere.Positions.All(Point => Math.Abs(Point.X * Point.X + Point.Y * Point.Y +
            Point.Z * Point.Z - 0.25f) < 1e-5f), "Sphere vertices lie on the radius 0.5 surface");
        CheckOutwardWinding(Sphere, Check, "Sphere triangles face outward");
    }

    private static void ValidateCapsule(AssetRegistry Registry, Action<bool, string> Check)
    {
        var Capsule = LoadPrimitive(Registry, "engine://meshes/capsule", "Capsule", Check);
        Check(Capsule.Positions.Count == 258 && Capsule.Indices.Count == 1536 &&
            Capsule.CoordinateSystem == "Y-up", "Capsule has the expected indexed geometry and Y-up coordinates");
        Check(Capsule.SourcePath is null && Capsule.SourceSha256 is null,
            "Capsule is a self-contained formal asset without source provenance");
        CheckBounds(Capsule, new Vector3(-0.5f, -1, -0.5f), new Vector3(0.5f, 1, 0.5f), Check,
            "Capsule preserves radius 0.5 and total height 2");
        Check(Capsule.Positions.Any(Point => Math.Abs(Point.Y - 1) < 1e-5f) &&
            Capsule.Positions.Any(Point => Math.Abs(Point.Y + 1) < 1e-5f),
            "Capsule has both hemispherical poles");
        CheckOutwardWinding(Capsule, Check, "Capsule triangles face outward");
    }

    private static StaticMeshAsset LoadPrimitive(AssetRegistry Registry, string Id,
        string Name, Action<bool, string> Check)
    {
        var Descriptor = Registry.Find(Id);
        Check(Descriptor is { Source: AssetSource.Engine, ReadOnly: true, Exists: true },
            $"{Name} remains a read-only registered engine asset");
        var Mesh = AssetContent.LoadStaticMesh(Registry, Id);
        var Legacy = AssetContent.LoadMesh(Registry, Id);
        Check(Mesh.Name == Name && Mesh.Primitive == Name,
            $"{Name} declares the formal primitive name");
        Check(Mesh.Positions.SequenceEqual(Legacy.Positions) && Mesh.Indices.SequenceEqual(Legacy.Indices),
            $"StaticMesh and MeshRenderer consume identical {Name} geometry");
        Check(StaticMeshAsset.Parse(Mesh.Serialize()).Serialize() == Mesh.Serialize(),
            $"{Name} preserves deterministic JSON round trips");
        return Mesh;
    }

    private static void CheckBounds(StaticMeshAsset Mesh, Vector3 ExpectedMinimum,
        Vector3 ExpectedMaximum, Action<bool, string> Check, string Message)
    {
        var Minimum = new Vector3(Mesh.Positions.Min(Point => Point.X),
            Mesh.Positions.Min(Point => Point.Y), Mesh.Positions.Min(Point => Point.Z));
        var Maximum = new Vector3(Mesh.Positions.Max(Point => Point.X),
            Mesh.Positions.Max(Point => Point.Y), Mesh.Positions.Max(Point => Point.Z));
        Check(Vector3.Distance(Minimum, ExpectedMinimum) < 1e-5f &&
            Vector3.Distance(Maximum, ExpectedMaximum) < 1e-5f, Message);
    }

    private static void CheckOutwardWinding(StaticMeshAsset Mesh, Action<bool, string> Check,
        string Message)
    {
        for (var Index = 0; Index < Mesh.Indices.Count; Index += 3)
        {
            var Points = Mesh.Indices.Skip(Index).Take(3)
                .Select(Vertex => Mesh.Positions[Vertex])
                .Select(Point => new Vector3(Point.X, Point.Y, Point.Z)).ToArray();
            var Normal = Vector3.Cross(Points[1] - Points[0], Points[2] - Points[0]);
            var Center = (Points[0] + Points[1] + Points[2]) / 3;
            Check(Normal.LengthSquared() > 1e-10f && Vector3.Dot(Normal, Center) > 0, Message);
        }
    }
}
