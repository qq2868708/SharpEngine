using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;
using SharpEngine.Logging;
using SharpEngine.Modules;

namespace SharpEngine.Editor;

internal sealed class EditorAssetOpenCanceledException(string Message) : Exception(Message);

public sealed record EditorAssetOpenResult(bool Success, string Code, string AssetId,
    string? OpenerId, string Message);

public sealed class EditorAssetOpenContext
{
    internal EditorAssetOpenContext(EditorRuntime Runtime, AssetDescriptor Asset, string Path,
        string EditorId, string EditorRootId)
    {
        this.Runtime = Runtime;
        this.Asset = Asset;
        this.Path = Path;
        this.EditorId = EditorId;
        this.EditorRootId = EditorRootId;
    }

    public EditorRuntime Runtime { get; }
    public AssetDescriptor Asset { get; }
    public string Path { get; }
    public string EditorId { get; }
    public string EditorRootId { get; }
    public void ShowPane(string PaneId) => Runtime.ShowPane(PaneId);
    public bool TryGetElementRect(string ElementId, out SharpEngine.UI.UiRect Bounds) =>
        Runtime.TryGetAssetEditorElementRect(EditorRootId, ElementId, out Bounds);
    public void SubmitViewportFrame(string Kind, SharpEngine.World.WorldViewportFrame Frame) =>
        Runtime.SubmitAssetEditorViewportFrame(EditorRootId, Kind, Frame);
    public void SetStatus(string Message) => Runtime.SetStatusFromExtension(Message);
}

/// <summary>One type-wide default or asset-specific override. Higher priority wins;
/// equal priorities use the most recent registration. Asset-specific handlers win first.</summary>
public sealed class EditorAssetOpener
{
    private EditorAssetOpener(string Id, string AssetType, int Priority,
        string? AssetId, Func<JsonObject>? GetState, string? EditorId)
    {
        this.Id = EditorExtensionIds.Validate(Id, nameof(Id));
        this.AssetType = EditorExtensionIds.RequireLabel(AssetType, nameof(AssetType));
        this.AssetId = AssetId is null ? null : EditorExtensionIds.Validate(AssetId, nameof(AssetId));
        this.EditorId = EditorExtensionIds.Validate(EditorId ?? Id, nameof(EditorId));
        this.Priority = Priority;
        this.GetState = GetState;
    }

    public EditorAssetOpener(string Id, string AssetType, Action<EditorAssetOpenContext> Open,
        int Priority = 0, string? AssetId = null, Func<JsonObject>? GetState = null,
        string? EditorId = null) : this(Id, AssetType, Priority, AssetId, GetState, EditorId)
    {
        ArgumentNullException.ThrowIfNull(Open);
        var Owner = ModuleRegistrationScope.Current;
        this.Open = Context =>
        {
            using var Scope = Owner?.Enter();
            Open(Context);
        };
    }

    public EditorAssetOpener(string Id, string AssetType,
        Func<EditorAssetOpenContext, IAssetEditorInstance> Create,
        int Priority = 0, string? AssetId = null, string? EditorId = null)
        : this(Id, AssetType, Priority, AssetId, null, EditorId)
    {
        ArgumentNullException.ThrowIfNull(Create);
        var Owner = ModuleRegistrationScope.Current;
        this.Create = Context =>
        {
            using var Scope = Owner?.Enter();
            return Create(Context);
        };
    }

    public string Id { get; }
    public string AssetType { get; }
    public string? AssetId { get; }
    public string EditorId { get; }
    public int Priority { get; }
    internal Action<EditorAssetOpenContext>? Open { get; }
    internal Func<EditorAssetOpenContext, IAssetEditorInstance>? Create { get; }
    internal Func<JsonObject>? GetState { get; }
}

public sealed class EditorAssetOpenerRegistry
{
    private readonly EditorRuntime Runtime;
    private readonly List<EditorAssetOpener> Entries = new();
    private readonly Dictionary<string, AssetEditorHost> Editors =
        new(StringComparer.OrdinalIgnoreCase);
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
            foreach (var Editor in Editors.Values.Where(Editor =>
                Editor.OpenerId.Equals(Opener.Id,
                    StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                Close(Editor);
            }
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

            if (Editors.TryGetValue(Asset.Id, out var Existing))
            {
                Existing.Focus();
                return Complete(true, "ASSET_EDITOR_REUSED", Asset.Id, Opener.Id,
                    $"Focused {Asset.Name}");
            }

            var RootId = AssetEditorHost.CreateRootId(Asset.Id);
            var Context = new EditorAssetOpenContext(Runtime, Asset, Path, Opener.EditorId,
                RootId);
            if (Opener.Create is not null)
            {
                var Instance = Opener.Create(Context);
                if (!Instance.Asset.Id.Equals(Asset.Id, StringComparison.OrdinalIgnoreCase) ||
                    !Instance.EditorId.Equals(Opener.EditorId, StringComparison.OrdinalIgnoreCase))
                {
                    Instance.Dispose();
                    throw new InvalidOperationException(
                        "Asset editor instance identity does not match its opener context");
                }

                var Host = new AssetEditorHost(Runtime, Opener, Instance, RootId, Close);
                Editors.Add(Asset.Id, Host);
                try
                {
                    Host.Open();
                }
                catch
                {
                    Editors.Remove(Asset.Id);
                    Host.Dispose();
                    throw;
                }
            }
            else
            {
                Opener.Open!(Context);
            }
            return Complete(true, "ASSET_OPENED", Asset.Id, Opener.Id,
                $"Opened {Asset.Name}");
        }
        catch (EditorAssetOpenCanceledException Error)
        {
            return Complete(false, "ASSET_OPEN_CANCELLED", Asset.Id, Opener.Id,
                Error.Message);
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
            schemaVersion = 2,
            lastResult = LastResult,
            openers = Entries.Select(Entry => new
            {
                id = Entry.Id,
                editorId = Entry.EditorId,
                assetType = Entry.AssetType,
                assetId = Entry.AssetId,
                priority = Entry.Priority,
                state = ReadState(Entry),
            }).ToArray(),
            editors = Editors.Values.OrderBy(Editor => Editor.AssetId,
                StringComparer.OrdinalIgnoreCase).Select(Editor => Editor.Describe()).ToArray(),
        };
    }

    internal bool TryHandleHostAction(string HostId, string Action)
    {
        EnsureOwnerThread();
        foreach (var Editor in Editors.Values.ToArray())
        {
            if (Editor.TryHandleAction(HostId, Action))
            {
                return true;
            }
        }

        return false;
    }

    internal void Update(float DeltaSeconds)
    {
        EnsureOwnerThread();
        foreach (var Editor in Editors.Values.ToArray())
        {
            Editor.Update(DeltaSeconds);
        }
    }

    internal bool ConfirmCloseAll()
    {
        EnsureOwnerThread();
        foreach (var Editor in Editors.Values.OrderBy(Editor => Editor.AssetId,
            StringComparer.OrdinalIgnoreCase))
        {
            if (!Editor.ConfirmClose())
            {
                return false;
            }
        }

        return true;
    }

    internal IReadOnlyList<JsonObject> DescribeEditors(string EditorId)
    {
        EnsureOwnerThread();
        return Editors.Values.Where(Editor => Editor.Instance.EditorId.Equals(EditorId,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(Editor => Editor.AssetId, StringComparer.OrdinalIgnoreCase)
            .Select(Editor => Editor.Describe().DeepClone().AsObject()).ToArray();
    }

    internal void Clear()
    {
        foreach (var Editor in Editors.Values.ToArray())
        {
            Editor.Dispose();
        }

        Entries.Clear();
        Editors.Clear();
    }

    private void Close(AssetEditorHost Editor)
    {
        Editors.Remove(Editor.AssetId);
        Editor.Dispose();
    }

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
