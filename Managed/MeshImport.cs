using System.Globalization;
using System.Text.Json;

namespace SharpEngine.Assets;

/// <summary>Small, deterministic Wavefront OBJ importer used by the project tool.
/// It intentionally imports positions and triangulated faces first; materials and
/// normals remain explicit future extensions of the JSON asset schema.</summary>
public static class WavefrontObjImporter
{
    public static MeshAssetDescription Load(string path, string? name = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("OBJ file was not found", path);
        var positions = new List<AssetVector3>();
        var indices = new List<int>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts[0] == "v")
            {
                if (parts.Length < 4 || !TryFloat(parts[1], out var x) ||
                    !TryFloat(parts[2], out var y) || !TryFloat(parts[3], out var z))
                    throw new InvalidDataException($"OBJ '{path}' contains an invalid vertex");
                positions.Add(new AssetVector3(x, y, z));
                continue;
            }
            if (parts[0] != "f") continue;
            if (parts.Length < 4) throw new InvalidDataException($"OBJ '{path}' contains a face with fewer than 3 vertices");
            var face = parts[1..].Select(token => ResolveIndex(token, positions.Count, path)).ToArray();
            for (var index = 1; index + 1 < face.Length; index++)
            {
                indices.Add(face[0]); indices.Add(face[index]); indices.Add(face[index + 1]);
            }
        }
        if (positions.Count < 3 || indices.Count == 0)
            throw new InvalidDataException($"OBJ '{path}' has no triangulated mesh faces");
        return new MeshAssetDescription(name ?? Path.GetFileNameWithoutExtension(path),
            "Imported OBJ", positions.AsReadOnly(), indices.AsReadOnly(), null);
    }

    public static void WriteMeshJson(MeshAssetDescription mesh, string destination,
        string? source = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var payload = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["name"] = mesh.Name,
            ["primitive"] = mesh.Primitive,
            ["coordinateSystem"] = "Y-up",
            ["positions"] = mesh.Positions.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
            ["indices"] = mesh.Indices.ToArray(),
        };
        if (!string.IsNullOrWhiteSpace(source)) payload["source"] = source;
        File.WriteAllText(destination, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int ResolveIndex(string token, int count, string path)
    {
        var position = token.Split('/', 2)[0];
        if (!int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value == 0)
            throw new InvalidDataException($"OBJ '{path}' contains an invalid face index '{token}'");
        var index = value > 0 ? value - 1 : count + value;
        if (index < 0 || index >= count)
            throw new InvalidDataException($"OBJ '{path}' face index '{token}' is outside the vertex range");
        return index;
    }

    private static bool TryFloat(string value, out float result) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
        float.IsFinite(result);
}
