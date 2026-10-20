using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Playables;

namespace SharpEngine.Animation;

/// <summary>Standalone, renderer-neutral skeleton asset.</summary>
public sealed class SkeletonAsset
{
    public const int CurrentSchemaVersion = 2;

    public SkeletonAsset(string assetId, string name, IReadOnlyList<SkeletonBone> bones,
        string? sourcePath = null, string? sourceSha256 = null,
        string? previewMeshId = null)
    {
        AssetId = RequireText(assetId, nameof(assetId));
        Name = RequireText(name, nameof(name));
        Bones = bones?.ToArray() ?? throw new ArgumentNullException(nameof(bones));
        SourcePath = string.IsNullOrWhiteSpace(sourcePath) ? null : sourcePath.Trim();
        SourceSha256 = string.IsNullOrWhiteSpace(sourceSha256) ? null : sourceSha256.Trim();
        PreviewMeshId = string.IsNullOrWhiteSpace(previewMeshId) ? null :
            previewMeshId.Trim();
        ValidateBones(Bones);
        SourceAssetContract.ValidateMetadata(SourcePath, SourceSha256, AssetId);
    }

    public string AssetId { get; }
    public string Name { get; }
    public IReadOnlyList<SkeletonBone> Bones { get; }
    public string? SourcePath { get; }
    public string? SourceSha256 { get; }
    /// <summary>Editor-only default mesh used to preview animations for this skeleton.</summary>
    public string? PreviewMeshId { get; }

    /// <summary>Export an FBX that is semantically equivalent to this skeleton.</summary>
    public string ExportFbx(AssetRegistry Registry, string Destination) =>
        FbxSourceContract.ExportFbx(Registry, AssetId, Destination);

    /// <summary>Compatibility source copy; does not include current authored asset changes.</summary>
    [Obsolete("Source copy only; use ExportFbx for semantic export. Canonical asset reconstruction requires an FBX writer.")]
    public string ExportOriginalFbx(AssetRegistry Registry, string Destination) =>
        FbxSourceContract.ExportOriginalFbx(Registry, AssetId, Destination);

    public string Serialize(bool indented = true) => ToJson().ToJsonString(
        new JsonSerializerOptions { WriteIndented = indented });

    public JsonObject ToJson()
    {
        var root = new JsonObject
        {
        ["schemaVersion"] = CurrentSchemaVersion,
        ["kind"] = "skeleton",
        ["assetId"] = AssetId,
        ["name"] = Name,
        ["previewMeshId"] = PreviewMeshId,
        ["bones"] = new JsonArray(Bones.Select(ToJson).ToArray()),
        };
        if (SourcePath is not null) root["sourcePath"] = SourcePath;
        if (SourceSha256 is not null) root["sourceSha256"] = SourceSha256;
        return root;
    }

    public static SkeletonAsset Parse(string json, string source = "<memory>")
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            EnsureNoDuplicateProperties(document.RootElement);
            var root = JsonNode.Parse(json) as JsonObject ??
                throw new InvalidDataException("Skeleton root must be an object");
            var Version = root["schemaVersion"]?.GetValue<int>() ??
                throw new InvalidDataException("Skeleton schemaVersion is required");
            if (Version == 1)
            {
                ExactWithOptional(root,
                    ["schemaVersion", "kind", "assetId", "name", "bones"],
                    ["sourcePath", "sourceSha256"]);
            }
            else if (Version == CurrentSchemaVersion)
            {
                ExactWithOptional(root,
                    ["schemaVersion", "kind", "assetId", "name", "previewMeshId", "bones"],
                    ["sourcePath", "sourceSha256"]);
            }
            else
            {
                throw new InvalidDataException("Unsupported skeleton schema");
            }
            if (root["kind"]!.GetValue<string>() != "skeleton")
            {
                throw new InvalidDataException("Unsupported skeleton kind");
            }
            var bones = root["bones"]!.AsArray().Select(ParseBone).ToArray();
            return new SkeletonAsset(RequiredString(root, "assetId"),
                RequiredString(root, "name"), bones, OptionalString(root, "sourcePath"),
                OptionalString(root, "sourceSha256"), OptionalString(root, "previewMeshId"));
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or
            InvalidOperationException or FormatException or ArgumentException or
            OverflowException)
        {
            throw new InvalidDataException($"Invalid skeleton '{source}': {exception.Message}", exception);
        }
    }

    private static JsonObject ToJson(SkeletonBone bone) => new()
    {
        ["boneId"] = bone.BoneId,
        ["name"] = bone.Name,
        ["parentBoneId"] = bone.ParentBoneId,
        ["bindPosition"] = PlayableJson.Vector3(bone.BindTransform.Position),
        ["bindRotation"] = PlayableJson.Quaternion(bone.BindTransform.Rotation),
        ["bindScale"] = PlayableJson.Vector3(bone.BindTransform.Scale),
    };

    internal static SkeletonBone ParseBone(JsonNode? node)
    {
        var value = node?.AsObject() ?? throw new InvalidDataException("Bone must be an object");
        Exact(value, "boneId", "name", "parentBoneId", "bindPosition", "bindRotation", "bindScale");
        var parent = value["parentBoneId"] is null ||
            value["parentBoneId"]!.GetValueKind() == JsonValueKind.Null
            ? null : value["parentBoneId"]!.GetValue<string>();
        return new SkeletonBone(RequiredString(value, "boneId"),
            RequiredString(value, "name"), parent,
            new BoneTransform(ParseVector3(value["bindPosition"], "bindPosition"),
                ParseQuaternion(value["bindRotation"], "bindRotation"),
                ParseVector3(value["bindScale"], "bindScale")));
    }

    internal static Vector3 ParseVector3(JsonNode? node, string path)
    {
        var values = node?.AsArray() ?? throw new InvalidDataException($"{path} must be a vector");
        if (values.Count != 3) throw new InvalidDataException($"{path} must have three values");
        return new(PlayableJson.Number(values[0], path), PlayableJson.Number(values[1], path),
            PlayableJson.Number(values[2], path));
    }

    internal static Quaternion ParseQuaternion(JsonNode? node, string path)
    {
        var values = node?.AsArray() ?? throw new InvalidDataException($"{path} must be a quaternion");
        if (values.Count != 4) throw new InvalidDataException($"{path} must have four values");
        return new(PlayableJson.Number(values[0], path), PlayableJson.Number(values[1], path),
            PlayableJson.Number(values[2], path), PlayableJson.Number(values[3], path));
    }

    internal static void ValidateBones(IReadOnlyList<SkeletonBone> bones)
    {
        if (bones.Count == 0) throw new InvalidDataException("A skeleton requires at least one bone");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bone in bones)
        {
            if (string.IsNullOrWhiteSpace(bone.BoneId) || !ids.Add(bone.BoneId))
                throw new InvalidDataException("Bone IDs must be unique and non-empty");
            if (string.IsNullOrWhiteSpace(bone.Name))
                throw new InvalidDataException($"Bone '{bone.BoneId}' requires a name");
            ValidateTransform(bone.BindTransform, $"bone '{bone.BoneId}' bindTransform");
            if (bone.ParentBoneId is not null && !ids.Contains(bone.ParentBoneId) &&
                !bones.Any(candidate => string.Equals(candidate.BoneId, bone.ParentBoneId,
                    StringComparison.Ordinal)))
                throw new InvalidDataException($"Bone '{bone.BoneId}' references a parent that does not exist");
        }
        foreach (var bone in bones)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var parent = bone.ParentBoneId;
            while (parent is not null)
            {
                if (!visited.Add(parent)) throw new InvalidDataException(
                    $"Skeleton contains a parent cycle at bone '{bone.BoneId}'");
                parent = bones.First(candidate => string.Equals(candidate.BoneId, parent,
                    StringComparison.Ordinal)).ParentBoneId;
            }
        }
    }

    internal static void ValidateTransform(BoneTransform transform, string path)
    {
        if (!IsFinite(transform.Position) || !IsFinite(transform.Rotation) ||
            !IsFinite(transform.Scale) || transform.Scale.X <= 0 || transform.Scale.Y <= 0 ||
            transform.Scale.Z <= 0 || transform.Rotation.LengthSquared() < 0.999f ||
            transform.Rotation.LengthSquared() > 1.001f)
            throw new InvalidDataException($"{path} contains an invalid transform");
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) &&
        float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool IsFinite(Quaternion value) => float.IsFinite(value.X) &&
        float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static string RequiredString(JsonObject root, string name) =>
        root[name]?.GetValue<string>() is { Length: > 0 } value ? value.Trim() :
        throw new InvalidDataException($"{name} is required");

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(
            "A non-empty value is required", parameterName) : value.Trim();

    private static void Exact(JsonObject value, params string[] fields)
    {
        if (value.Count != fields.Length || fields.Any(field => !value.ContainsKey(field)))
            throw new InvalidDataException("Unexpected or missing fields in skeleton asset");
    }

    private static void ExactWithOptional(JsonObject value, string[] required, string[] optional)
    {
        if (required.Any(field => !value.ContainsKey(field)) || value.Any(property =>
            !required.Contains(property.Key, StringComparer.Ordinal) &&
            !optional.Contains(property.Key, StringComparer.Ordinal)))
            throw new InvalidDataException("Unexpected or missing fields in skeleton asset");
    }

    private static string? OptionalString(JsonObject root, string name) =>
        root[name] is null || root[name]!.GetValueKind() == JsonValueKind.Null
            ? null : root[name]!.GetValue<string>();

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

/// <summary>One vertex's normalized skeletal skinning influences.</summary>
public readonly record struct BoneInfluences(
    IReadOnlyList<int> BoneIndices, IReadOnlyList<float> Weights)
{
    public static BoneInfluences Four(int a, int b, int c, int d,
        float wa, float wb, float wc, float wd) => new([a, b, c, d], [wa, wb, wc, wd]);
}

/// <summary>Runtime skeletal mesh geometry and its bind-pose skinning data.</summary>
public sealed class SkeletalMeshAsset
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumInfluences = 12;

    public SkeletalMeshAsset(string assetId, string name, string skeletonId,
        IReadOnlyList<SkeletonBone> bones, IReadOnlyList<AssetVector3> positions,
        IReadOnlyList<int> indices, IReadOnlyList<BoneInfluences> influences,
        string? defaultMaterial = null, string? sourcePath = null,
        string? sourceSha256 = null)
    {
        AssetId = RequireText(assetId, nameof(assetId));
        Name = RequireText(name, nameof(name));
        SkeletonId = RequireText(skeletonId, nameof(skeletonId));
        Bones = bones?.ToArray() ?? throw new ArgumentNullException(nameof(bones));
        Positions = positions?.ToArray() ?? throw new ArgumentNullException(nameof(positions));
        Indices = indices?.ToArray() ?? throw new ArgumentNullException(nameof(indices));
        Influences = influences?.ToArray() ?? throw new ArgumentNullException(nameof(influences));
        DefaultMaterial = string.IsNullOrWhiteSpace(defaultMaterial) ? null : defaultMaterial.Trim();
        SourcePath = string.IsNullOrWhiteSpace(sourcePath) ? null : sourcePath.Trim();
        SourceSha256 = string.IsNullOrWhiteSpace(sourceSha256) ? null : sourceSha256.Trim();
        SkeletonAsset.ValidateBones(Bones);
        Validate();
    }

    public string AssetId { get; }
    public string Name { get; }
    public string SkeletonId { get; }
    public IReadOnlyList<SkeletonBone> Bones { get; }
    public IReadOnlyList<AssetVector3> Positions { get; }
    public IReadOnlyList<int> Indices { get; }
    public IReadOnlyList<BoneInfluences> Influences { get; }
    public string? DefaultMaterial { get; }
    public string? SourcePath { get; }
    public string? SourceSha256 { get; }

    /// <summary>Export an FBX that is semantically equivalent to this skeletal mesh.</summary>
    public string ExportFbx(AssetRegistry Registry, string Destination) =>
        FbxSourceContract.ExportFbx(Registry, AssetId, Destination);

    /// <summary>Compatibility source copy; does not include current authored asset changes.</summary>
    [Obsolete("Source copy only; use ExportFbx for semantic export. Canonical asset reconstruction requires an FBX writer.")]
    public string ExportOriginalFbx(AssetRegistry Registry, string Destination) =>
        FbxSourceContract.ExportOriginalFbx(Registry, AssetId, Destination);

    public string Serialize(bool indented = true) => ToJson().ToJsonString(
        new JsonSerializerOptions { WriteIndented = indented });

    public JsonObject ToJson()
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["kind"] = "skeletal-mesh",
            ["assetId"] = AssetId,
            ["name"] = Name,
            ["skeletonId"] = SkeletonId,
            ["bones"] = new JsonArray(Bones.Select(bone => new JsonObject
            {
                ["boneId"] = bone.BoneId,
                ["name"] = bone.Name,
                ["parentBoneId"] = bone.ParentBoneId,
                ["bindPosition"] = PlayableJson.Vector3(bone.BindTransform.Position),
                ["bindRotation"] = PlayableJson.Quaternion(bone.BindTransform.Rotation),
                ["bindScale"] = PlayableJson.Vector3(bone.BindTransform.Scale),
            }).ToArray()),
            ["positions"] = new JsonArray(Positions.Select(position =>
                new JsonArray(position.X, position.Y, position.Z)).ToArray()),
            ["indices"] = new JsonArray(Indices.Select(index => JsonValue.Create(index)).ToArray()),
            ["influences"] = new JsonArray(Influences.Select(influence => new JsonObject
            {
                ["boneIndices"] = new JsonArray(influence.BoneIndices.Select(index => JsonValue.Create(index)).ToArray()),
                ["weights"] = new JsonArray(influence.Weights.Select(weight => JsonValue.Create(weight)).ToArray()),
            }).ToArray()),
            ["defaultMaterial"] = DefaultMaterial,
        };
        if (SourcePath is not null) root["sourcePath"] = SourcePath;
        if (SourceSha256 is not null) root["sourceSha256"] = SourceSha256;
        return root;
    }

    public static SkeletalMeshAsset Parse(string json, string source = "<memory>")
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            EnsureNoDuplicateProperties(document.RootElement);
            var root = JsonNode.Parse(json) as JsonObject ??
                throw new InvalidDataException("Skeletal mesh root must be an object");
            var required = new[] { "schemaVersion", "kind", "assetId", "name", "skeletonId",
                "bones", "positions", "indices", "influences" };
            var optional = new[] { "defaultMaterial", "sourcePath", "sourceSha256" };
            if (required.Any(field => !root.ContainsKey(field)) || root.Select(property => property.Key).Any(field =>
                !required.Contains(field, StringComparer.Ordinal) && !optional.Contains(field, StringComparer.Ordinal)))
                throw new InvalidDataException("Unexpected or missing fields in skeletal mesh asset");
            var Version = root["schemaVersion"]!.GetValue<int>();
            if ((Version != 1 && Version != CurrentSchemaVersion) ||
                root["kind"]!.GetValue<string>() != "skeletal-mesh")
            {
                throw new InvalidDataException("Unsupported skeletal mesh schema");
            }
            var bones = root["bones"]!.AsArray().Select(SkeletonAsset.ParseBone).ToArray();
            var positions = root["positions"]!.AsArray().Select((node, index) =>
                ParseVector3(node, $"positions[{index}]")).ToArray();
            var indices = root["indices"]!.AsArray().Select((node, index) =>
                ParseInt(node, $"indices[{index}]")).ToArray();
            var influences = root["influences"]!.AsArray().Select(ParseInfluences).ToArray();
            if (Version == 1 && influences.Any(Influence => Influence.BoneIndices.Count > 4))
            {
                throw new InvalidDataException("Legacy skeletal mesh schema 1 supports at most four influences");
            }
            return new SkeletalMeshAsset(RequiredString(root, "assetId"),
                RequiredString(root, "name"), RequiredString(root, "skeletonId"), bones,
                positions, indices, influences, OptionalString(root, "defaultMaterial"),
                OptionalString(root, "sourcePath"), OptionalString(root, "sourceSha256"));
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or
            InvalidOperationException or FormatException or ArgumentException or
            OverflowException)
        {
            throw new InvalidDataException($"Invalid skeletal mesh '{source}': {exception.Message}", exception);
        }
    }

    private void Validate()
    {
        if (Positions.Count < 3) throw new InvalidDataException(
            $"Skeletal mesh '{AssetId}' requires at least three positions");
        if (Indices.Count == 0 || Indices.Count % 3 != 0 ||
            Indices.Any(index => index < 0 || index >= Positions.Count))
            throw new InvalidDataException($"Skeletal mesh '{AssetId}' has invalid triangle indices");
        if (Positions.Any(position => !IsFinite(position)))
            throw new InvalidDataException($"Skeletal mesh '{AssetId}' has a non-finite position");
        if (Influences.Count != Positions.Count)
            throw new InvalidDataException($"Skeletal mesh '{AssetId}' requires one influence set per vertex");
        for (var vertex = 0; vertex < Influences.Count; vertex++)
        {
            var influence = Influences[vertex];
            if (influence.BoneIndices.Count is < 1 or > MaximumInfluences ||
                influence.Weights.Count != influence.BoneIndices.Count)
            {
                throw new InvalidDataException($"Skeletal mesh '{AssetId}' vertex {vertex} must have one to {MaximumInfluences} influences");
            }
            var WeightedBones = influence.BoneIndices.Zip(influence.Weights)
                .Where(Pair => Pair.Second > 0).Select(Pair => Pair.First).ToArray();
            if (WeightedBones.Distinct().Count() != WeightedBones.Length)
            {
                throw new InvalidDataException($"Skeletal mesh '{AssetId}' vertex {vertex} has duplicate bone influences");
            }
            var sum = 0f;
            for (var index = 0; index < influence.BoneIndices.Count; index++)
            {
                var boneIndex = influence.BoneIndices[index];
                var weight = influence.Weights[index];
                if (boneIndex < 0 || boneIndex >= Bones.Count || !float.IsFinite(weight) || weight < 0)
                    throw new InvalidDataException($"Skeletal mesh '{AssetId}' vertex {vertex} has invalid influence data");
                sum += weight;
            }
            if (!float.IsFinite(sum) || MathF.Abs(sum - 1) > 0.001f)
                throw new InvalidDataException($"Skeletal mesh '{AssetId}' vertex {vertex} influence weights must sum to one");
        }
        SourceAssetContract.ValidateMetadata(SourcePath, SourceSha256, AssetId);
    }

    private static BoneInfluences ParseInfluences(JsonNode? node)
    {
        var value = node?.AsObject() ?? throw new InvalidDataException("Influence set must be an object");
        Exact(value, "boneIndices", "weights");
        var boneIndices = value["boneIndices"]!.AsArray().Select((item, index) =>
            ParseInt(item, $"boneIndices[{index}]")).ToArray();
        var weights = value["weights"]!.AsArray().Select((item, index) =>
            Number(item, $"weights[{index}]")).ToArray();
        return new BoneInfluences(boneIndices, weights);
    }

    private static AssetVector3 ParseVector3(JsonNode? node, string path)
    {
        var vector = SkeletonAsset.ParseVector3(node, path);
        return new AssetVector3(vector.X, vector.Y, vector.Z);
    }

    private static int ParseInt(JsonNode? node, string path) =>
        node?.GetValue<int>() ?? throw new InvalidDataException($"{path} is required");
    private static float Number(JsonNode? node, string path) => PlayableJson.Number(node, path);
    private static bool IsFinite(AssetVector3 value) => float.IsFinite(value.X) &&
        float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static string RequiredString(JsonObject root, string name) =>
        root[name]?.GetValue<string>() is { Length: > 0 } value ? value.Trim() :
        throw new InvalidDataException($"{name} is required");
    private static string? OptionalString(JsonObject root, string name) =>
        root[name] is null || root[name]!.GetValueKind() == JsonValueKind.Null
            ? null : root[name]!.GetValue<string>();
    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(
            "A non-empty value is required", parameterName) : value.Trim();
    private static void Exact(JsonObject value, params string[] fields)
    {
        if (value.Count != fields.Length || fields.Any(field => !value.ContainsKey(field)))
            throw new InvalidDataException("Unexpected or missing fields in skeletal mesh asset");
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

public static class SkeletonContent
{
    public static SkeletonAsset Load(AssetRegistry registry, string id)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var descriptor = registry.Find(id) ?? throw new KeyNotFoundException($"Asset was not registered: '{id}'");
        if (!string.Equals(descriptor.Type, "Skeleton", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Asset '{id}' is {descriptor.Type}, expected Skeleton");
        var path = registry.ResolvePath(descriptor) ?? throw new InvalidDataException($"Asset '{id}' has no resolved path");
        var parsed = SkeletonAsset.Parse(File.ReadAllText(path), path);
        if (!string.Equals(parsed.AssetId, id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Skeleton '{id}' does not match its serialized assetId '{parsed.AssetId}'");
        if (parsed.SourcePath is not null)
            SourceAssetContract.ResolveAndValidate(registry, descriptor, parsed.SourcePath,
                parsed.SourceSha256!);
        return parsed;
    }
}

public static class SkeletalMeshContent
{
    public static SkeletalMeshAsset Load(AssetRegistry registry, string id)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var Registry = registry;
        var Id = id;
        var Descriptor = Registry.Find(Id) ?? throw new KeyNotFoundException($"Asset was not registered: '{Id}'");
        if (!string.Equals(Descriptor.Type, "SkeletalMesh", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Asset '{Id}' is {Descriptor.Type}, expected SkeletalMesh");
        }
        var Path = Registry.ResolvePath(Descriptor) ?? throw new InvalidDataException($"Asset '{Id}' has no resolved path");
        var Parsed = SkeletalMeshAsset.Parse(File.ReadAllText(Path), Path);
        if (!string.Equals(Parsed.AssetId, Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Skeletal mesh '{Id}' does not match its serialized assetId '{Parsed.AssetId}'");
        }
        var Skeleton = SkeletonContent.Load(Registry, Parsed.SkeletonId);
        SkeletonBinding.Validate(Skeleton.Bones, Parsed.Bones, $"Skeletal mesh '{Id}' and skeleton '{Skeleton.AssetId}'");
        if (Parsed.SourcePath is not null)
        {
            SourceAssetContract.ResolveAndValidate(Registry, Descriptor, Parsed.SourcePath, Parsed.SourceSha256!);
        }
        return Parsed;
    }
}
