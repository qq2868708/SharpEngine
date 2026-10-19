using System.Security.Cryptography;
using System.Text.Json;

namespace SharpEngine.Assets;

/// <summary>
/// Defines the FBX provenance and export contract for imported assets.
///
/// An FBX export is semantically equivalent to the selected SharpEngine asset
/// data. A verified raw source copy is a valid export when no authored state
/// has changed, but byte-for-byte identity is never part of the contract. An
/// FBX writer is required when canonical asset state must be rebuilt.
///
/// The semantic contract covers mesh geometry and coordinate units, skeleton
/// hierarchy and bind pose, skinning influences, and animation bone tracks.
/// FBX metadata that is not represented by those asset contracts is not
/// required to survive an export.
/// </summary>
public static class FbxSourceContract
{
    /// <summary>
    /// Defines semantic export of the current registered asset. Raw FBXSource
    /// records can be copied directly. Canonical assets require reconstruction,
    /// which currently reports unsupported without touching the destination.
    /// </summary>
    public static string ExportFbx(AssetRegistry Registry, string Id,
        string Destination)
    {
        ArgumentNullException.ThrowIfNull(Registry);
        var Descriptor = Registry.Find(Id) ?? throw new KeyNotFoundException(
            $"Asset was not registered: '{Id}'");
        ValidateDestination(Destination);
        if (string.Equals(Descriptor.Type, "FBXSource", StringComparison.OrdinalIgnoreCase))
        {
            return CopyOriginalFbx(Registry, Descriptor, Destination);
        }
        ValidateAssetType(Descriptor);
        throw new NotSupportedException(
            $"FBX reconstruction is not implemented for asset '{Id}' ({Descriptor.Type}). " +
            "A source hash validates provenance only; it cannot prove equivalence to the current asset.");
    }

    /// <summary>
    /// Copies the verified original source for compatibility. This operation
    /// does not export the current mesh, skeleton, or animation state.
    /// </summary>
    [Obsolete("Source copy only; use ExportFbx for semantic export. Canonical asset reconstruction requires an FBX writer.")]
    public static string ExportOriginalFbx(AssetRegistry Registry, string Id,
        string Destination)
    {
        ArgumentNullException.ThrowIfNull(Registry);
        var Descriptor = Registry.Find(Id) ?? throw new KeyNotFoundException(
            $"Asset was not registered: '{Id}'");
        return CopyOriginalFbx(Registry, Descriptor, Destination);
    }

    private static string CopyOriginalFbx(AssetRegistry Registry, AssetDescriptor Descriptor,
        string Destination)
    {
        string Source;
        if (string.Equals(Descriptor.Type, "FBXSource", StringComparison.OrdinalIgnoreCase))
        {
            Source = Registry.ResolvePath(Descriptor) ?? throw new InvalidDataException(
                $"Asset '{Descriptor.Id}' has no resolved path");
            if (!File.Exists(Source))
            {
                throw new FileNotFoundException($"FBX source file was not found for asset '{Descriptor.Id}'", Source);
            }
            if (!Source.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Asset '{Descriptor.Id}' source is not an FBX file");
            }
            var HasExpectedHash = Descriptor.Metadata.TryGetValue("sha256", out var Expected) ||
                Descriptor.Metadata.TryGetValue("sourceSha256", out Expected);
            if (HasExpectedHash && !string.IsNullOrWhiteSpace(Expected))
            {
                using var SourceStream = File.OpenRead(Source);
                var Actual = Convert.ToHexString(SHA256.HashData(SourceStream));
                if (!string.Equals(Actual, Expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"FBX source hash mismatch for asset '{Descriptor.Id}'");
                }
            }
        }
        else
        {
            ValidateAssetType(Descriptor);
            var AssetPath = Registry.ResolvePath(Descriptor) ?? throw new InvalidDataException(
                $"Asset '{Descriptor.Id}' has no resolved path");
            using var Document = JsonDocument.Parse(File.ReadAllText(AssetPath));
            var HasSourcePath = Document.RootElement.TryGetProperty("sourcePath", out var SourcePathElement);
            var HasSourceHash = Document.RootElement.TryGetProperty("sourceSha256", out var HashElement);
            if (!HasSourcePath || SourcePathElement.ValueKind != JsonValueKind.String ||
                !HasSourceHash || HashElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"Asset '{Descriptor.Id}' has no FBX source provenance");
            }
            var SourcePath = SourcePathElement.GetString()!;
            var SourceSha256 = HashElement.GetString()!;
            SourceAssetContract.ValidateMetadata(SourcePath, SourceSha256, Descriptor.Id);
            Source = SourceAssetContract.ResolveAndValidate(Registry, Descriptor, SourcePath, SourceSha256);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Destination))!);
        if (!PathsEqual(Source, Destination))
        {
            File.Copy(Source, Destination, true);
        }
        return Destination;
    }

    private static void ValidateAssetType(AssetDescriptor Descriptor)
    {
        if (!string.Equals(Descriptor.Type, "Mesh", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Descriptor.Type, "StaticMesh", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Descriptor.Type, "SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Descriptor.Type, "Skeleton", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Descriptor.Type, "SkeletalAnimation", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Asset '{Descriptor.Id}' is {Descriptor.Type}, expected an FBX-compatible asset");
        }
    }

    private static void ValidateDestination(string Destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Destination);
        if (!Destination.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("FBX exports require a .fbx destination", nameof(Destination));
        }
    }

    private static bool PathsEqual(string Left, string Right) =>
        string.Equals(Path.GetFullPath(Left), Path.GetFullPath(Right),
            StringComparison.OrdinalIgnoreCase);
}
