using System.Security.Cryptography;
using System.Text.Json;

namespace SharpEngine.Assets;

/// <summary>
/// Verifies and copies an imported FBX source without attempting to rebuild it
/// from renderer-neutral mesh data.  This preserves the byte-for-byte source
/// round-trip contract for import pipelines.
/// </summary>
public static class FbxSourceContract
{
    public static string ExportOriginalFbx(AssetRegistry registry, string id,
        string destination)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var descriptor = registry.Find(id) ?? throw new KeyNotFoundException(
            $"Asset was not registered: '{id}'");
        if (string.Equals(descriptor.Type, "FBXSource", StringComparison.OrdinalIgnoreCase))
        {
            var fbxPath = registry.ResolvePath(descriptor) ?? throw new InvalidDataException(
                $"Asset '{id}' has no resolved path");
            if (!fbxPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Asset '{id}' source is not an FBX file");
            if (!File.Exists(fbxPath)) throw new FileNotFoundException(
                $"FBX source file was not found for asset '{id}'", fbxPath);
            if (descriptor.Metadata.TryGetValue("sha256", out var expected) &&
                !string.IsNullOrWhiteSpace(expected))
            {
                using var sourceStream = File.OpenRead(fbxPath);
                var sourceActual = Convert.ToHexString(SHA256.HashData(sourceStream));
                if (!string.Equals(sourceActual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"FBX source hash mismatch for asset '{id}'");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            if (!PathsEqual(fbxPath, destination)) File.Copy(fbxPath, destination, true);
            return destination;
        }
        if (!string.Equals(descriptor.Type, "Mesh", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(descriptor.Type, "StaticMesh", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(descriptor.Type, "SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(descriptor.Type, "Skeleton", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Asset '{id}' is {descriptor.Type}, expected mesh asset");
        var meshPath = registry.ResolvePath(descriptor) ?? throw new InvalidDataException(
            $"Asset '{id}' has no resolved path");
        using var document = JsonDocument.Parse(File.ReadAllText(meshPath));
        if (!document.RootElement.TryGetProperty("sourcePath", out var sourcePathElement) ||
            sourcePathElement.ValueKind != JsonValueKind.String ||
            !document.RootElement.TryGetProperty("sourceSha256", out var hashElement) ||
            hashElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Asset '{id}' has no FBX source contract");
        var sourcePath = sourcePathElement.GetString()!;
        var sourceSha256 = hashElement.GetString()!;
        SourceAssetContract.ValidateMetadata(sourcePath, sourceSha256, id);
        var sourceRoot = descriptor.Source == AssetSource.Engine ? registry.EngineRoot :
            registry.ProjectRoot ?? throw new InvalidDataException(
                $"Asset '{id}' has no source root");
        var root = Path.GetFullPath(sourceRoot);
        var source = Path.GetFullPath(Path.Combine(root,
            sourcePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, source);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException($"Source path escapes its source root: '{sourcePath}'");
        if (!File.Exists(source)) throw new FileNotFoundException(
            $"FBX source file was not found for asset '{id}'", source);
        using var stream = File.OpenRead(source);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, sourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"FBX source hash mismatch for asset '{id}'");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        if (!PathsEqual(source, destination)) File.Copy(source, destination, true);
        return destination;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
