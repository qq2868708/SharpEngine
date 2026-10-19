using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Assets;

internal static class EngineCubeValidation
{
    public static void Run(string Root, AssetRegistry Registry, Action<bool, string> Check)
    {
        var Descriptor = Registry.Find("engine://meshes/cube")!;
        Check(Descriptor.Source == AssetSource.Engine && Descriptor.ReadOnly && Descriptor.Exists,
            "Cube remains a read-only registered engine asset");
        var Cube = AssetContent.LoadStaticMesh(Registry, Descriptor.Id);
        var Legacy = AssetContent.LoadMesh(Registry, Descriptor.Id);
        Check(Cube.SourcePath == "Content/Assets/Sources/Cube.fbx" && Cube.SourceSha256?.Length == 64,
            "Cube has an original FBX path and SHA-256");
        Check(Cube.Positions.Count == 8 && Cube.Indices.Count == 36 && Cube.CoordinateSystem == "Y-up",
            "Imported Cube has eight corners, twelve triangles and Y-up geometry");
        Check(Cube.Positions.SequenceEqual(Legacy.Positions) && Cube.Indices.SequenceEqual(Legacy.Indices),
            "StaticMesh and existing MeshRenderer consume identical imported geometry");
        Check(StaticMeshAsset.Parse(Cube.Serialize()).Serialize() == Cube.Serialize(),
            "Imported Cube preserves provenance in deterministic JSON round trips");
        foreach (var Point in Cube.Positions)
        {
            Check(Math.Abs(Math.Abs(Point.X) - 0.5) < 1e-5 &&
                Math.Abs(Math.Abs(Point.Y) - 0.5) < 1e-5 && Math.Abs(Math.Abs(Point.Z) - 0.5) < 1e-5,
                "Cube corners preserve centred one-unit dimensions through FBX conversion");
        }
        for (var Index = 0; Index < Cube.Indices.Count; Index += 3)
        {
            var Points = Cube.Indices.Skip(Index).Take(3).Select(Vertex => Cube.Positions[Vertex])
                .Select(Point => new Vector3(Point.X, Point.Y, Point.Z)).ToArray();
            Check(Vector3.Dot(Vector3.Cross(Points[1] - Points[0], Points[2] - Points[0]),
                (Points[0] + Points[1] + Points[2]) / 3) > 0, "Cube triangles face outward");
        }
        Check(Registry.Find("engine://sources/cube-blend") is { Exists: true, ReadOnly: true } &&
            Registry.Find("engine://sources/cube-fbx") is { Exists: true, ReadOnly: true },
            "Blender and FBX sources are registered engine assets");
        var Temporary = Path.Combine(Path.GetTempPath(), "SharpEngine-Cube-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Temporary);
        try
        {
            var Export = Path.Combine(Temporary, "Cube.fbx");
            var Original = File.ReadAllBytes(Path.Combine(Root, Cube.SourcePath!));
            foreach (var Id in new[] { Descriptor.Id, "engine://sources/cube-fbx" })
            {
                AssetContent.ExportOriginalFbx(Registry, Id, Export);
                Check(File.ReadAllBytes(Export).AsSpan().SequenceEqual(Original),
                    "Cube exports authoritative FBX bytes exactly: " + Id);
            }
            var Manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "Content/Assets/assets.json")))!.AsObject();
            var CubeRecord = Manifest["assets"]!.AsArray().Single(Asset => Asset?["id"]?.GetValue<string>() == "meshes/cube")!.DeepClone();
            Manifest["assets"] = new JsonArray(CubeRecord);
            var ManifestPath = Path.Combine(Temporary, "Content/Assets/assets.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            File.WriteAllText(ManifestPath, Manifest.ToJsonString());
            var MeshPath = Path.Combine(Temporary, Descriptor.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(MeshPath)!);
            File.Copy(Registry.ResolvePath(Descriptor)!, MeshPath);
            var SourcePath = Path.Combine(Temporary, Cube.SourcePath!);
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath)!);
            File.WriteAllBytes(SourcePath, Original.Concat(new byte[] { 0 }).ToArray());
            File.WriteAllText(Path.Combine(Temporary, "engine.json"), "{}");
            var Changed = AssetRegistry.Load(Temporary);
            foreach (var Load in new Action[]
            {
                () => AssetContent.LoadMesh(Changed, Descriptor.Id),
                () => AssetContent.LoadStaticMesh(Changed, Descriptor.Id),
                () => AssetContent.ExportOriginalFbx(Changed, Descriptor.Id, Export)
            })
            {
                var Rejected = false;
                try
                {
                    Load();
                }
                catch (InvalidDataException Error)
                {
                    Rejected = Error.Message.Contains("hash mismatch", StringComparison.OrdinalIgnoreCase);
                }
                Check(Rejected, "Changed Cube FBX is rejected by legacy, canonical and export paths");
            }
        }
        finally
        {
            Directory.Delete(Temporary, true);
        }
    }
}
