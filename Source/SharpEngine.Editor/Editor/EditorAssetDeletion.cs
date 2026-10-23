using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Logging;

namespace SharpEngine.Editor;

/// <summary>Outcome of one asset deletion attempt. Success is only true after the manifest
/// entry and the source file were both removed by the same committed operation.</summary>
public sealed record AssetDeleteResult(bool Success, string Code, string AssetId, string Message)
{
    public const string Deleted = "ASSET_DELETED";
    public const string NotFound = "ASSET_DELETE_NOT_FOUND";
    public const string EngineOwned = "ASSET_DELETE_ENGINE_OWNED";
    public const string PluginOwned = "ASSET_DELETE_PLUGIN_OWNED";
    public const string ReadOnly = "ASSET_DELETE_READ_ONLY";
    public const string DuringPlay = "ASSET_DELETE_DURING_PLAY";
    public const string ActiveWorld = "ASSET_DELETE_ACTIVE_WORLD";
    public const string DirtyDocument = "ASSET_DELETE_DIRTY_DOCUMENT";
    public const string Cancelled = "ASSET_DELETE_CANCELLED";
    public const string ManifestUnavailable = "ASSET_DELETE_MANIFEST_UNAVAILABLE";
    public const string ManifestInvalid = "ASSET_DELETE_MANIFEST_INVALID";
    public const string FileFailed = "ASSET_DELETE_FILE_FAILED";
}

/// <summary>Removes a project asset. The asset manifest is the authoritative record, so the
/// workflow removes that record and the source file as one reversible step; the content
/// browser only raises the intent and never gains a second write path.</summary>
public sealed partial class EditorRuntime
{
    public AssetDeleteResult DeleteAsset(string AssetId, bool RequestConfirmation = true)
    {
        var Asset = _assets?.Find(AssetId);
        if (Asset is null)
        {
            return RejectDelete(AssetDeleteResult.NotFound, AssetId, $"Asset '{AssetId}' was not found");
        }
        // Ownership and editability are separate questions; deletion needs both answers.
        if (Asset.Source == AssetSource.Engine)
        {
            return RejectDelete(AssetDeleteResult.EngineOwned, AssetId,
                "Engine-owned assets cannot be deleted");
        }
        if (Asset.OwnerId is not null)
        {
            return RejectDelete(AssetDeleteResult.PluginOwned, AssetId,
                $"Asset is declared by plugin '{Asset.OwnerId}'");
        }
        if (Asset.ReadOnly)
        {
            return RejectDelete(AssetDeleteResult.ReadOnly, AssetId, "Asset is marked read-only");
        }
        if (PlayState != EditorPlayState.Stopped)
        {
            return RejectDelete(AssetDeleteResult.DuringPlay, AssetId, "Stop Play before deleting assets");
        }
        if (string.Equals(AssetId, _worldAssetId, StringComparison.OrdinalIgnoreCase))
        {
            return RejectDelete(AssetDeleteResult.ActiveWorld, AssetId,
                "The World currently open in the editor cannot be deleted");
        }
        if (AssetOpeners.IsDirty(AssetId))
        {
            return RejectDelete(AssetDeleteResult.DirtyDocument, AssetId,
                "Save or close the asset editor before deleting this asset");
        }
        var ManifestPath = ProjectAssetManifestPath();
        if (ManifestPath is null || !File.Exists(ManifestPath))
        {
            return RejectDelete(AssetDeleteResult.ManifestUnavailable, AssetId,
                "The project asset manifest could not be resolved");
        }
        if (RequestConfirmation && HasLiveUi && !NativeEngineApi.ConfirmDestructive(_windowHandle,
                $"Delete {Asset.Name}?\n\n{Asset.RelativePath}\n\n" +
                "The manifest entry and the source file are both removed."))
        {
            return RejectDelete(AssetDeleteResult.Cancelled, AssetId, "Deletion was cancelled");
        }

        var SourcePath = Asset.ResolvePath(_projectRoot!);
        try
        {
            CommitAssetDeletion(ManifestPath, Asset, SourcePath);
        }
        catch (AssetDeletionException Error)
        {
            return RejectDelete(Error.Code, AssetId, Error.Message);
        }
        AssetOpeners.CloseAsset(AssetId);
        if (_selection.Kind == EditorSelectionKind.Asset &&
            string.Equals(_selection.Id, AssetId, StringComparison.OrdinalIgnoreCase))
        {
            _selection = EditorSelection.Scene;
        }
        ReloadAssetRegistry();
        MarkExtensionUiDirty();
        RefreshHierarchy();
        RefreshInspector();
        RefreshViewportModeUi();
        SharpLog.Info("Assets", $"Deleted asset {AssetId}", new Dictionary<string, string>
        {
            ["path"] = SourcePath ?? string.Empty,
            ["manifest"] = ManifestPath,
        });
        SetStatus($"Deleted {Asset.Name}");
        return new AssetDeleteResult(true, AssetDeleteResult.Deleted, AssetId, $"Deleted {Asset.Name}");
    }

    private AssetDeleteResult RejectDelete(string Code, string AssetId, string Message)
    {
        SharpLog.Warning("Assets", $"{Code} [{AssetId}] {Message}");
        SetStatus($"Delete rejected: {Message}");
        return new AssetDeleteResult(false, Code, AssetId, Message);
    }

    /// <summary>Resolves the manifest the registry actually read, so deletion targets exactly
    /// that record set instead of assuming the default path.</summary>
    private string? ProjectAssetManifestPath()
    {
        if (_projectRoot is null || _projectManifest is null)
        {
            return null;
        }
        try
        {
            using var Document = JsonDocument.Parse(File.ReadAllText(_projectManifest));
            var Relative = Document.RootElement.TryGetProperty("assets", out var Assets) &&
                Assets.TryGetProperty("manifest", out var Manifest) &&
                Manifest.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(Manifest.GetString())
                ? Manifest.GetString()!
                : "Content/Assets/assets.json";
            return Path.Combine(_projectRoot, Relative.Replace('/', Path.DirectorySeparatorChar));
        }
        catch (Exception Error) when (Error is JsonException or IOException)
        {
            SharpLog.Error("Assets", "Could not read the project manifest", Error);
            return null;
        }
    }

    /// <summary>Removes the manifest entry and the source file as one reversible step. The
    /// new manifest is staged beside the old one and moved into place only after the entry is
    /// confirmed unique; a failing file delete restores the original text so the manifest can
    /// never reference a file that still exists.</summary>
    private static void CommitAssetDeletion(string ManifestPath, AssetDescriptor Asset,
        string? SourcePath)
    {
        var Original = File.ReadAllText(ManifestPath);
        JsonObject Root;
        JsonArray Entries;
        try
        {
            Root = JsonNode.Parse(Original)?.AsObject() ??
                throw new AssetDeletionException(AssetDeleteResult.ManifestInvalid,
                    "The project asset manifest is not a JSON object");
            Entries = Root["assets"]?.AsArray() ??
                throw new AssetDeletionException(AssetDeleteResult.ManifestInvalid,
                    "The project asset manifest has no assets array");
        }
        catch (JsonException Error)
        {
            throw new AssetDeletionException(AssetDeleteResult.ManifestInvalid,
                "The project asset manifest is not valid JSON: " + Error.Message);
        }
        // The registry namespaces ids while the manifest stores the bare id.
        var Separator = Asset.Id.IndexOf("://", StringComparison.Ordinal);
        var BareId = Separator < 0 ? Asset.Id : Asset.Id[(Separator + 3)..];
        var Matches = Entries.Where(Entry => Entry is not null &&
                string.Equals(Entry["id"]?.GetValue<string>(), BareId, StringComparison.Ordinal))
            .ToArray();
        if (Matches.Length != 1)
        {
            throw new AssetDeletionException(AssetDeleteResult.ManifestInvalid,
                $"Manifest must declare '{BareId}' exactly once, found {Matches.Length}");
        }
        Entries.Remove(Matches[0]);
        var Text = Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var Staged = ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(Staged, Text);
            File.Move(Staged, ManifestPath, true);
            if (SourcePath is not null && File.Exists(SourcePath))
            {
                try
                {
                    File.Delete(SourcePath);
                }
                catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
                {
                    File.WriteAllText(ManifestPath, Original);
                    throw new AssetDeletionException(AssetDeleteResult.FileFailed,
                        "Could not delete the source file, so the manifest was restored: " + Error.Message);
                }
            }
        }
        finally
        {
            if (File.Exists(Staged))
            {
                File.Delete(Staged);
            }
        }
    }

    private sealed class AssetDeletionException(string Code, string Message) : Exception(Message)
    {
        public string Code { get; } = Code;
    }
}
