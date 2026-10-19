using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;
using SharpEngine.Logging;
using SharpEngine.Modules;

namespace SharpEngine.Editor;

public sealed record EditorAssetOpenResult(bool Success, string Code, string AssetId,
    string? OpenerId, string Message);

public sealed class EditorAssetOpenContext
{
    internal EditorAssetOpenContext(EditorRuntime Runtime, AssetDescriptor Asset, string Path)
    {
        this.Runtime = Runtime;
        this.Asset = Asset;
        this.Path = Path;
    }

    public EditorRuntime Runtime { get; }
    public AssetDescriptor Asset { get; }
    public string Path { get; }
    public void ShowPane(string PaneId) => Runtime.ShowPane(PaneId);
}

/// <summary>One type-wide default or asset-specific override. Higher priority wins;
/// equal priorities use the most recent registration. Asset-specific handlers win first.</summary>
public sealed class EditorAssetOpener
{
    public EditorAssetOpener(string Id, string AssetType, Action<EditorAssetOpenContext> Open,
        int Priority = 0, string? AssetId = null, Func<JsonObject>? GetState = null)
    {
        this.Id = EditorExtensionIds.Validate(Id, nameof(Id));
        this.AssetType = EditorExtensionIds.RequireLabel(AssetType, nameof(AssetType));
        this.AssetId = AssetId is null ? null : EditorExtensionIds.Validate(AssetId, nameof(AssetId));
        this.Priority = Priority;
        ArgumentNullException.ThrowIfNull(Open);
        var Owner = ModuleRegistrationScope.Current;
        this.Open = Context =>
        {
            using var Scope = Owner?.Enter();
            Open(Context);
        };
        this.GetState = GetState;
    }

    public string Id { get; }
    public string AssetType { get; }
    public string? AssetId { get; }
    public int Priority { get; }
    internal Action<EditorAssetOpenContext> Open { get; }
    internal Func<JsonObject>? GetState { get; }
}

public sealed class EditorAssetOpenerRegistry
{
    private readonly EditorRuntime Runtime;
    private readonly List<EditorAssetOpener> Entries = new();
    private readonly int OwnerThreadId = Environment.CurrentManagedThreadId;

    internal EditorAssetOpenerRegistry(EditorRuntime Runtime)
    {
        this.Runtime = Runtime;
    }

    public IReadOnlyList<EditorAssetOpener> Openers => Entries.ToArray();
    public EditorAssetOpenResult? LastResult { get; private set; }

    public IDisposable Register(EditorAssetOpener Opener)
    {
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(Opener);
        if (Entries.Any(Entry => string.Equals(Entry.Id, Opener.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Asset opener '{Opener.Id}' is already registered");
        }

        Entries.Add(Opener);
        return new EditorRegistration(() =>
        {
            EnsureOwnerThread();
            Entries.Remove(Opener);
        });
    }

    public EditorAssetOpener? Resolve(AssetDescriptor Asset)
    {
        return Entries.AsEnumerable().Reverse()
            .Where(Entry => string.Equals(Entry.AssetType, Asset.Type, StringComparison.OrdinalIgnoreCase) &&
                (Entry.AssetId is null || string.Equals(Entry.AssetId, Asset.Id, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(Entry => Entry.AssetId is not null)
            .ThenByDescending(Entry => Entry.Priority).FirstOrDefault();
    }

    internal EditorAssetOpenResult Open(string AssetId)
    {
        EnsureOwnerThread();
        var Asset = Runtime.Assets?.Find(AssetId);
        var Opener = Asset is null ? null : Resolve(Asset);
        if (Asset is null)
        {
            return Complete(false, "ASSET_NOT_FOUND", AssetId, null, "Asset was not found");
        }
        if (Opener is null)
        {
            return Complete(false, "ASSET_OPENER_UNAVAILABLE", Asset.Id, null,
                $"No editor tool is registered for {Asset.Type}");
        }

        try
        {
            var Path = Runtime.Assets!.ResolvePath(Asset);
            if (Path is null || !File.Exists(Path))
            {
                return Complete(false, "ASSET_FILE_MISSING", Asset.Id, Opener.Id, "Asset file is missing");
            }

            Opener.Open(new EditorAssetOpenContext(Runtime, Asset, Path));
            return Complete(true, "ASSET_OPENED", Asset.Id, Opener.Id, $"Opened {Asset.Name}");
        }
        catch (Exception Error)
        {
            SharpLog.Error("AssetEditor", $"Asset opener '{Opener.Id}' failed for {Asset.Id}", Error);
            return Complete(false, "ASSET_OPEN_FAILED", Asset.Id, Opener.Id,
                $"Could not open {Asset.Name}: {Error.Message}");
        }
    }

    internal object Describe()
    {
        return new
        {
            schemaVersion = 1,
            lastResult = LastResult,
            openers = Entries.Select(Entry => new
            {
                id = Entry.Id,
                assetType = Entry.AssetType,
                assetId = Entry.AssetId,
                priority = Entry.Priority,
                state = ReadState(Entry),
            }).ToArray(),
        };
    }

    internal void Clear() => Entries.Clear();

    private static JsonObject? ReadState(EditorAssetOpener Entry)
    {
        try
        {
            return Entry.GetState?.Invoke().DeepClone().AsObject();
        }
        catch (Exception Error)
        {
            SharpLog.Error("AssetEditor", $"Asset tool '{Entry.Id}' snapshot failed", Error);
            return new JsonObject
            {
                ["schemaVersion"] = 1,
                ["code"] = "ASSET_TOOL_SNAPSHOT_FAILED",
            };
        }
    }

    private EditorAssetOpenResult Complete(bool Success, string Code, string AssetId,
        string? OpenerId, string Message)
    {
        LastResult = new EditorAssetOpenResult(Success, Code, AssetId, OpenerId, Message);
        if (!Success)
        {
            Runtime.SetStatusFromExtension($"[{Code}] {Message}");
        }
        return LastResult;
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != OwnerThreadId)
        {
            throw new InvalidOperationException("Asset opening and registration must use the editor thread");
        }
    }
}

public sealed partial class EditorRuntime
{
    public EditorAssetOpenResult OpenAsset(string AssetId)
    {
        return AssetOpeners.Open(AssetId);
    }

    public void ShowPane(string PaneId)
    {
        if (_layout is null || !EditorPaneIds.IsBuiltIn(PaneId))
        {
            throw new ArgumentException($"Unknown editor pane '{PaneId}'", nameof(PaneId));
        }
        if (!IsPaneVisible(PaneId))
        {
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.ReopenAsFloating, PaneId));
        }

        var Location = FindPaneGroup(PaneId);
        if (Location.GroupId is not null)
        {
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.ActivateTab,
                PaneId, TargetGroupId: Location.GroupId, TargetHostId: Location.HostId));
        }
    }
}
