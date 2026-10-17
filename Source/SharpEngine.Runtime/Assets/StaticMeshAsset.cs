using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEngine.Assets;

/// <summary>
/// Canonical runtime representation of a static mesh asset.  The existing
/// <see cref="MeshAssetDescription"/> remains the renderer-facing legacy
/// description; this type adds a stable asset id, coordinate-system metadata,
/// and an optional source-file round-trip contract.
/// </summary>
public sealed class StaticMeshAsset
{
    public const int CurrentSchemaVersion = 1;

    public StaticMeshAsset(string assetId, string name, string primitive,
        string coordinateSystem, IReadOnlyList<AssetVector3> positions,
        IReadOnlyList<int> indices, string? defaultMaterial = null,
        string? sourcePath = null, string? sourceSha256 = null)
    {
        AssetId = RequireText(assetId, nameof(assetId));
        Name = RequireText(name, nameof(name));
        Primitive = RequireText(primitive, nameof(primitive));
        CoordinateSystem = RequireText(coordinateSystem, nameof(coordinateSystem));
        Positions = positions?.ToArray() ?? throw new ArgumentNullException(nameof(positions));
        Indices = indices?.ToArray() ?? throw new ArgumentNullException(nameof(indices));
        DefaultMaterial = string.IsNullOrWhiteSpace(defaultMaterial) ? null : defaultMaterial.Trim();
        SourcePath = string.IsNullOrWhiteSpace(sourcePath) ? null : sourcePath.Trim();
        SourceSha256 = string.IsNullOrWhiteSpace(sourceSha256) ? null : sourceSha256.Trim();
        Validate();
    }

    public string AssetId { get; }
    public string Name { get; }
    public string Primitive { get; }
    public string CoordinateSystem { get; }
    public IReadOnlyList<AssetVector3> Positions { get; }
    public IReadOnlyList<int> Indices { get; }
    public string? DefaultMaterial { get; }
    /// <summary>Source path relative to the owning engine/project root.</summary>
    public string? SourcePath { get; }
    /// <summary>Lower- or upper-case SHA-256 of the source file bytes.</summary>
    public string? SourceSha256 { get; }

    /// <summary>Copy the authoritative imported FBX after source hash verification.</summary>
    public string ExportOriginalFbx(AssetRegistry registry, string destination) =>
        FbxSourceContract.ExportOriginalFbx(registry, AssetId, destination);

    public MeshAssetDescription ToMeshDescription() => new(Name, Primitive,
        Positions, Indices, DefaultMaterial);

    public string Serialize(bool indented = true) => ToJson().ToJsonString(
        new JsonSerializerOptions { WriteIndented = indented });

    public JsonObject ToJson()
    {
        var result = new JsonObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["kind"] = "static-mesh",
            ["assetId"] = AssetId,
            ["name"] = Name,
            ["primitive"] = Primitive,
            ["coordinateSystem"] = CoordinateSystem,
            ["defaultMaterial"] = DefaultMaterial,
            ["positions"] = new JsonArray(Positions.Select(position =>
                new JsonArray(position.X, position.Y, position.Z)).ToArray()),
            ["indices"] = new JsonArray(Indices.Select(index =>
                JsonValue.Create(index)).ToArray()),
        };
        if (SourcePath is not null) result["sourcePath"] = SourcePath;
        if (SourceSha256 is not null) result["sourceSha256"] = SourceSha256;
        return result;
    }

    /// <summary>
    /// Parse canonical static-mesh JSON.  The original Mesh JSON schema is
    /// accepted as a compatibility format; its asset id is supplied by the
    /// manifest loader or derived from <paramref name="source"/>.
    /// </summary>
    public static StaticMeshAsset Parse(string json, string source = "<memory>",
        string? fallbackAssetId = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            EnsureNoDuplicateProperties(document.RootElement);
            var root = JsonNode.Parse(json) as JsonObject ??
                throw new InvalidDataException("Static mesh root must be an object");
            var canonical = root.ContainsKey("kind") || root.ContainsKey("assetId");
            if (canonical)
            {
                ExactWithOptional(root, ["schemaVersion", "kind", "assetId", "name", "primitive",
                    "coordinateSystem", "positions", "indices"],
                    ["defaultMaterial", "sourcePath", "sourceSha256"]);
                if (root["schemaVersion"]!.GetValue<int>() != CurrentSchemaVersion ||
                    root["kind"]!.GetValue<string>() != "static-mesh")
                    throw new InvalidDataException("Unsupported static mesh schema");
                return Create(root["assetId"]!.GetValue<string>(), root, source);
            }

            Exact(root, "schemaVersion", "name", "primitive", "coordinateSystem",
                "defaultMaterial", "positions", "indices");
            if (root["schemaVersion"]!.GetValue<int>() != CurrentSchemaVersion)
                throw new InvalidDataException("Unsupported mesh schema");
            var id = string.IsNullOrWhiteSpace(fallbackAssetId)
                ? "memory://" + Path.GetFileNameWithoutExtension(source)
                : fallbackAssetId;
            return Create(id!, root, source);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or
            InvalidOperationException or FormatException or ArgumentException or
            OverflowException)
        {
            throw new InvalidDataException($"Invalid static mesh '{source}': {exception.Message}", exception);
        }
    }

    private static StaticMeshAsset Create(string assetId, JsonObject root, string source)
    {
        var positionsNode = root["positions"]?.AsArray() ??
            throw new InvalidDataException("positions must be an array");
        var positions = positionsNode.Select((node, index) => ParseVector3(node,
            $"positions[{index}]")).ToArray();
        var indicesNode = root["indices"]?.AsArray() ??
            throw new InvalidDataException("indices must be an array");
        var indices = indicesNode.Select((node, index) => ParseInt(node,
            $"indices[{index}]")).ToArray();
        return new StaticMeshAsset(assetId,
            RequiredString(root, "name"), RequiredString(root, "primitive"),
            RequiredString(root, "coordinateSystem"), positions, indices,
            OptionalString(root, "defaultMaterial"), OptionalString(root, "sourcePath"),
            OptionalString(root, "sourceSha256"));
    }

    private void Validate()
    {
        if (Positions.Count < 3) throw new InvalidDataException(
            $"Static mesh '{AssetId}' requires at least three positions");
        if (Indices.Count == 0 || Indices.Count % 3 != 0 ||
            Indices.Any(index => index < 0 || index >= Positions.Count))
            throw new InvalidDataException($"Static mesh '{AssetId}' has invalid triangle indices");
        if (Positions.Any(position => !IsFinite(position)))
            throw new InvalidDataException($"Static mesh '{AssetId}' has a non-finite position");
        SourceAssetContract.ValidateMetadata(SourcePath, SourceSha256, AssetId);
    }

    private static AssetVector3 ParseVector3(JsonNode? node, string path)
    {
        var values = node?.AsArray() ?? throw new InvalidDataException($"{path} must be a vector");
        if (values.Count != 3) throw new InvalidDataException($"{path} must have three values");
        var result = new AssetVector3(Number(values[0], path), Number(values[1], path), Number(values[2], path));
        if (!IsFinite(result)) throw new InvalidDataException($"{path} must be finite");
        return result;
    }

    private static int ParseInt(JsonNode? node, string path) =>
        node?.GetValue<int>() ?? throw new InvalidDataException($"{path} is required");

    private static float Number(JsonNode? node, string path)
    {
        var value = node?.GetValue<float>() ?? throw new InvalidDataException($"{path} is required");
        if (!float.IsFinite(value)) throw new InvalidDataException($"{path} must be finite");
        return value;
    }

    private static string RequiredString(JsonObject root, string name) =>
        root[name]?.GetValue<string>() is { Length: > 0 } value ? value.Trim() :
        throw new InvalidDataException($"{name} is required");

    private static string? OptionalString(JsonObject root, string name) =>
        root[name] is null || root[name]!.GetValueKind() == JsonValueKind.Null
            ? null : root[name]!.GetValue<string>();

    private static bool IsFinite(AssetVector3 value) => float.IsFinite(value.X) &&
        float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(
            "A non-empty value is required", parameterName) : value.Trim();

    private static void Exact(JsonObject value, params string[] fields)
    {
        if (value.Count != fields.Length || fields.Any(field => !value.ContainsKey(field)))
            throw new InvalidDataException("Unexpected or missing fields in static mesh asset");
    }

    private static void ExactWithOptional(JsonObject value, IReadOnlyList<string> required,
        IReadOnlyList<string> optional)
    {
        if (required.Any(field => !value.ContainsKey(field)) ||
            value.Select(property => property.Key).Any(field => !required.Contains(field, StringComparer.Ordinal) &&
                !optional.Contains(field, StringComparer.Ordinal)))
            throw new InvalidDataException("Unexpected or missing fields in static mesh asset");
    }

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException(
                    $"Duplicate JSON field '{property.Name}'");
                EnsureNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) EnsureNoDuplicateProperties(item);
    }
}

/// <summary>Source file metadata shared by imported runtime assets.</summary>
internal static class SourceAssetContract
{
    public static void ValidateMetadata(string? path, string? sha256, string assetId)
    {
        if ((path is null) != (sha256 is null))
            throw new InvalidDataException($"Asset '{assetId}' must provide both sourcePath and sourceSha256");
        if (path is null) return;
        if (Path.IsPathRooted(path) || path.Contains('\\') || path.Contains("..", StringComparison.Ordinal) ||
            path.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character)))
            throw new InvalidDataException($"Asset '{assetId}' has an invalid sourcePath");
        if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Asset '{assetId}' sourcePath must point to an FBX file");
        if (sha256!.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"Asset '{assetId}' has an invalid sourceSha256");
    }

    public static string ResolveAndValidate(AssetRegistry registry, AssetDescriptor descriptor,
        string sourcePath, string sourceSha256)
    {
        var root = descriptor.Source == AssetSource.Engine ? registry.EngineRoot : registry.ProjectRoot;
        if (root is null) throw new InvalidDataException(
            $"Asset '{descriptor.Id}' has no source root");
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot,
            sourcePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException($"Source path escapes its source root: '{sourcePath}'");
        if (!File.Exists(fullPath)) throw new FileNotFoundException(
            $"FBX source file was not found for asset '{descriptor.Id}'", fullPath);
        using var stream = File.OpenRead(fullPath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, sourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"FBX source hash mismatch for asset '{descriptor.Id}'");
        return fullPath;
    }
}
