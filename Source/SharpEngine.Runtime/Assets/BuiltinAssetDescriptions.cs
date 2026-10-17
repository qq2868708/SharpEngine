using System.Text.Json;

namespace SharpEngine.Assets;

/// <summary>Mesh positions are local-space units, using Y as the up axis.</summary>
public readonly record struct AssetVector3(float X, float Y, float Z);

public readonly record struct AssetColor(float R, float G, float B, float A = 1);

public sealed record MeshAssetDescription(
    string Name,
    string Primitive,
    IReadOnlyList<AssetVector3> Positions,
    IReadOnlyList<int> Indices,
    string? DefaultMaterial);

public sealed record MaterialAssetDescription(
    string Name,
    string Shader,
    AssetColor BaseColor,
    float Metallic,
    float Roughness);

/// <summary>Loads the data-only engine mesh/material formats through the registry.</summary>
public static class AssetContent
{
    public static MeshAssetDescription LoadMesh(AssetRegistry registry, string id)
    {
        var asset = registry.Find(id) ?? throw new KeyNotFoundException(
            $"Asset was not registered: '{id}'");
        if (!string.Equals(asset.Type, "Mesh", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Asset '{id}' is {asset.Type}, expected Mesh");
        var assetPath = registry.ResolvePath(asset) ?? throw new InvalidDataException(
            $"Asset '{id}' has no resolved path");
        if (string.Equals(asset.Importer, "SharpEngine.Obj", StringComparison.OrdinalIgnoreCase) ||
            assetPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            return WavefrontObjImporter.Load(assetPath, asset.Name);
        using var document = LoadDocument(registry, id, "Mesh");
        var root = document.RootElement;
        var positions = new List<AssetVector3>();
        foreach (var value in root.GetProperty("positions").EnumerateArray())
        {
            var xyz = value.EnumerateArray().Select(component => component.GetSingle()).ToArray();
            if (xyz.Length != 3 || xyz.Any(component => !float.IsFinite(component)))
                throw new InvalidDataException($"Mesh '{id}' has an invalid position");
            positions.Add(new AssetVector3(xyz[0], xyz[1], xyz[2]));
        }

        var indices = root.GetProperty("indices").EnumerateArray()
            .Select(value => value.GetInt32()).ToArray();
        if (positions.Count < 3 || indices.Length == 0 || indices.Length % 3 != 0 ||
            indices.Any(index => index < 0 || index >= positions.Count))
            throw new InvalidDataException($"Mesh '{id}' has invalid triangle indices");

        return new MeshAssetDescription(GetText(root, "name", id),
            GetText(root, "primitive", "Custom"), positions.AsReadOnly(),
            Array.AsReadOnly(indices), GetText(root, "defaultMaterial", string.Empty));
    }

    public static MaterialAssetDescription LoadMaterial(AssetRegistry registry, string id)
    {
        using var document = LoadDocument(registry, id, "Material");
        var root = document.RootElement;
        var rgba = root.GetProperty("baseColor").EnumerateArray()
            .Select(value => value.GetSingle()).ToArray();
        var metallic = root.GetProperty("metallic").GetSingle();
        var roughness = root.GetProperty("roughness").GetSingle();
        if (rgba.Length != 4 || rgba.Any(value => !float.IsFinite(value) || value < 0 || value > 1) ||
            !float.IsFinite(metallic) || metallic < 0 || metallic > 1 ||
            !float.IsFinite(roughness) || roughness < 0 || roughness > 1)
            throw new InvalidDataException($"Material '{id}' has invalid parameters");

        return new MaterialAssetDescription(GetText(root, "name", id),
            GetText(root, "shader", "Standard"),
            new AssetColor(rgba[0], rgba[1], rgba[2], rgba[3]), metallic, roughness);
    }

    private static JsonDocument LoadDocument(AssetRegistry registry, string id,
        string expectedType)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var asset = registry.Find(id) ?? throw new KeyNotFoundException(
            $"Asset was not registered: '{id}'");
        if (!string.Equals(asset.Type, expectedType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Asset '{id}' is {asset.Type}, expected {expectedType}");
        var path = registry.ResolvePath(asset) ?? throw new InvalidDataException(
            $"Asset '{id}' has no resolved path");
        var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("schemaVersion", out var version) ||
            version.GetInt32() != 1)
        {
            document.Dispose();
            throw new InvalidDataException($"Asset '{id}' has an unsupported schema");
        }
        return document;
    }

    private static string GetText(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;
}
