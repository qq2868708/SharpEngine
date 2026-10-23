using SharpEngine.Rendering;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;
using SharpEngine.Input;
using SharpEngine.World;

namespace SharpEngine.Editor;

public sealed record AssetEditorPaneDefinition(string PaneId, string Label,
    bool Closable = true);

public sealed record AssetEditorCommandDefinition(string CommandId, string Label,
    string Menu, int Order, string? Icon = null, bool ShowInToolbar = false,
    string? Tooltip = null);

/// <summary>One live RCSS property update for an element of an asset editor host.
/// Transported without replacing markup, so a design gesture keeps native element
/// identity, pointer capture and focus.</summary>
public readonly record struct AssetEditorLiveProperty(string ElementId, string Name,
    string Value);

/// <summary>Optional asset editor capability for direct manipulation. While a transient
/// gesture is active the host must not republish markup, otherwise the native element
/// the gesture captured would be replaced mid-drag.</summary>
public interface IAssetEditorLivePreview
{
    bool SuppressMarkupRefresh
    {
        get;
    }

    /// <summary>True once when a transient gesture or direct edit changed native element
    /// state that the published markup no longer describes, so the host must republish
    /// even if the rendered markup string happens to match its cached copy.</summary>
    bool ConsumeMarkupRefreshRequest();

    IReadOnlyList<AssetEditorLiveProperty> DrainLiveProperties();
}

public interface IAssetEditorInstance : IDisposable
{
    AssetDescriptor Asset { get; }
    string EditorId { get; }
    string Title { get; }
    string PrimaryPaneId { get; }
    bool IsDirty { get; }
    bool IsReadOnly { get; }
    IReadOnlyList<AssetEditorPaneDefinition> Panes { get; }
    IReadOnlyList<AssetEditorCommandDefinition> Commands { get; }
    EditorLayoutDocument CreateDefaultLayout();
    bool CanExecuteCommand(string CommandId);
    void ExecuteCommand(string CommandId);
    bool TryHandleAction(string Action);
    string BuildPaneMarkup(string PaneId);
    void Save();
    void Update(float DeltaSeconds);
    JsonObject Describe();
}

internal sealed class AssetEditorHost : IDisposable
{
    private readonly EditorRuntime Runtime;
    private readonly EditorAssetOpener Opener;
    private readonly Action<AssetEditorHost> Closed;
    private readonly EditorLayoutSession Layout;
    private readonly Dictionary<string, AssetEditorPaneDefinition> Panes;
    private readonly HashSet<string> LiveFloatingHosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> PublishedMarkup = new(StringComparer.Ordinal);
    private readonly List<IDisposable> ShortcutRegistrations = new();
    private bool Disposed;

    public AssetEditorHost(EditorRuntime Runtime, EditorAssetOpener Opener,
        IAssetEditorInstance Instance, string RootId, Action<AssetEditorHost> Closed)
    {
        this.Runtime = Runtime;
        this.Opener = Opener;
        this.Instance = Instance;
        this.RootId = RootId;
        this.Closed = Closed;
        Panes = Instance.Panes.ToDictionary(Pane => Pane.PaneId, StringComparer.Ordinal);
        if (Panes.Count == 0 || !Panes.ContainsKey(Instance.PrimaryPaneId))
        {
            throw new InvalidOperationException(
                $"Asset editor '{Instance.EditorId}' requires a registered primary pane");
        }

        Layout = new EditorLayoutSession(Instance.CreateDefaultLayout(), Panes.Keys.ToArray(),
            Instance.PrimaryPaneId);
    }

    public string RootId { get; }
    public IAssetEditorInstance Instance { get; }
    public string AssetId => Instance.Asset.Id;
    public string OpenerId => Opener.Id;

    public void Open()
    {
        ThrowIfDisposed();
        var Window = Runtime.CreateAssetEditorWindow(RootId, WindowTitle());
        if (Runtime.HasLiveUi && Window == 0)
        {
            throw new InvalidOperationException(
                $"Could not create asset editor window '{RootId}'");
        }

        RegisterShortcuts();
        Refresh(true);
    }

    /// <summary>Editor-route shortcut scope for this asset editor. A shortcut is only
    /// taken when the key event came from one of this editor's hosts, which is exactly
    /// the window that currently owns focus. Returning false lets the same action bubble
    /// to the global editor handlers.</summary>
    private void RegisterShortcuts()
    {
        var Input = Runtime.InputRuntime;
        if (Input is null || ShortcutRegistrations.Count != 0)
        {
            return;
        }

        // A focused document claims the gesture by dispatching the same action its menu
        // and toolbar emit, so shortcut and click share one handler.
        ShortcutRegistrations.Add(Input.BindAction(InputRoute.Editor,
            EditorShortcutSettings.SaveActionId, EditorShortcutSettings.ScopedPriority,
            Event => HandleScoped(Event, "editor.asset-save")));
        ShortcutRegistrations.Add(Input.BindAction(InputRoute.Editor,
            EditorShortcutSettings.UndoActionId, EditorShortcutSettings.ScopedPriority,
            Event => HandleScoped(Event, "editor.asset-command:undo")));
        ShortcutRegistrations.Add(Input.BindAction(InputRoute.Editor,
            EditorShortcutSettings.RedoActionId, EditorShortcutSettings.ScopedPriority,
            Event => HandleScoped(Event, "editor.asset-command:redo")));
    }

    private bool HandleScoped(InputActionEvent Event, string Action)
    {
        // Only the owning host may take the shortcut, and only once per press.
        if (Event.Phase != InputActionPhase.Performed || !OwnsHost(Event.HostId))
        {
            return false;
        }
        // A read-only document declines so the same action can bubble to the global layer.
        // An editable document consumes even when the command is disabled, so Undo inside
        // a document can never silently undo the World.
        if (Instance.IsReadOnly)
        {
            return false;
        }
        return TryHandleAction(RootId, Action);
    }

    internal JsonObject DescribeShortcutScope() => new()
    {
        ["hostId"] = RootId,
        ["assetId"] = AssetId,
        ["editorId"] = Instance.EditorId,
        ["readOnly"] = Instance.IsReadOnly,
        ["priority"] = EditorShortcutSettings.ScopedPriority,
        ["routes"] = new JsonArray(
            new JsonObject
            {
                ["action"] = EditorShortcutSettings.SaveActionId,
                ["command"] = "editor.asset-save",
            },
            new JsonObject
            {
                ["action"] = EditorShortcutSettings.UndoActionId,
                ["command"] = "editor.asset-command:undo",
            },
            new JsonObject
            {
                ["action"] = EditorShortcutSettings.RedoActionId,
                ["command"] = "editor.asset-command:redo",
            }),
    };

    public void Focus()
    {
        ThrowIfDisposed();
        if (Runtime.HasLiveUi)
        {
            Runtime.CreateAssetEditorWindow(RootId, WindowTitle());
        }
    }

    public void Update(float DeltaSeconds)
    {
        if (Disposed)
        {
            return;
        }

        Instance.Update(DeltaSeconds);
        Refresh(false);
    }

    public bool TryHandleAction(string HostId, string Action)
    {
        if (Disposed || !OwnsHost(HostId))
        {
            return false;
        }

        if (HostId == RootId && Action == "window.close-requested")
        {
            RequestClose();
            return true;
        }
        if (HostId != RootId && Action == "window.closed")
        {
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.CloseFloatingHost,
                TargetHostId: InternalHostId(HostId)));
            return true;
        }
        if (Action.StartsWith("editor.dock-", StringComparison.Ordinal))
        {
            HandleDockAction(Action);
            return true;
        }
        if (Action == "editor.asset-save")
        {
            Save();
            return true;
        }
        if (Action == "editor.asset-close")
        {
            RequestClose();
            return true;
        }
        if (Action.StartsWith("editor.asset-command:", StringComparison.Ordinal))
        {
            var CommandId = Action["editor.asset-command:".Length..];
            if (Instance.CanExecuteCommand(CommandId))
            {
                Instance.ExecuteCommand(CommandId);
            }

            Refresh(false);
            return true;
        }
        if (Action.StartsWith("editor.asset-pane:", StringComparison.Ordinal))
        {
            TogglePane(Action["editor.asset-pane:".Length..]);
            return true;
        }
        if (Instance.TryHandleAction(Action))
        {
            Refresh(false);
            return true;
        }

        return true;
    }

    public JsonObject Describe()
    {
        var Floating = new JsonArray(Layout.FloatingHosts.Select(Host =>
            (JsonNode)new JsonObject
            {
                ["hostId"] = NativeHostId(Host.HostId),
                ["root"] = EditorLayoutJson.ToJsonNode(Host.Root),
            }).ToArray());
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["editorId"] = Instance.EditorId,
            ["openerId"] = Opener.Id,
            ["rootId"] = RootId,
            ["assetId"] = AssetId,
            ["dirty"] = Instance.IsDirty,
            ["readOnly"] = Instance.IsReadOnly,
            ["main"] = EditorLayoutJson.ToJsonNode(Layout.Document.MainHost.Root),
            ["floating"] = Floating,
            ["state"] = Instance.Describe(),
        };
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        for (var Index = ShortcutRegistrations.Count - 1; Index >= 0; Index--)
        {
            ShortcutRegistrations[Index].Dispose();
        }
        ShortcutRegistrations.Clear();
        Runtime.DestroyAssetEditorWindow(RootId);
        Instance.Dispose();
        LiveFloatingHosts.Clear();
        PublishedMarkup.Clear();
    }

    private void Save()
    {
        if (Instance.IsReadOnly || !Instance.IsDirty)
        {
            return;
        }

        try
        {
            Instance.Save();
            Runtime.SetStatusFromExtension($"Saved {Instance.Asset.Name}");
            Refresh(false);
        }
        catch (Exception Exception)
        {
            SharpEngine.Logging.SharpLog.Error("AssetEditor",
                $"Save failed for {AssetId}", Exception);
            Runtime.SetStatusFromExtension($"Save failed: {Exception.Message}");
        }
    }

    private void RequestClose()
    {
        if (!ConfirmClose())
        {
            return;
        }

        Closed(this);
    }

    internal bool ConfirmClose()
    {
        if (Instance.IsDirty && !Instance.IsReadOnly)
        {
            var Choice = Runtime.PromptUnsavedAsset(RootId, Instance.Title);
            if (Choice == UnsavedWorldChoice.Cancel)
            {
                return false;
            }
            if (Choice == UnsavedWorldChoice.Save)
            {
                Save();
                if (Instance.IsDirty)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private void Refresh(bool Force)
    {
        var Live = Instance as IAssetEditorLivePreview;
        var LiveRefresh = Live is not null && Live.ConsumeMarkupRefreshRequest();
        if (Live is not null)
        {
            foreach (var Property in Live.DrainLiveProperties())
            {
                ApplyLiveProperty(Property);
            }
        }
        Publish(RootId, "assetEditorTitle", WebUtility.HtmlEncode(WindowTitle()), Force);
        Publish(RootId, "assetEditorStatus",
            WebUtility.HtmlEncode(Instance.IsDirty ? "Unsaved changes" : "Saved"), Force);
        Publish(RootId, "assetEditorMenus", BuildMenus(), Force);
        Publish(RootId, "assetEditorToolbar", BuildToolbar(), Force);
        if (Live is { SuppressMarkupRefresh: true })
        {
            return;
        }
        PublishDockHost("main", Layout.Document.MainHost.Root, Force || LiveRefresh);

        var Desired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Floating in Layout.FloatingHosts)
        {
            var NativeId = NativeHostId(Floating.HostId);
            Desired.Add(NativeId);
            if (!LiveFloatingHosts.Contains(NativeId))
            {
                var FirstPaneId = EditorLayoutValidator.EnumeratePanes(Floating.Root).First();
                Runtime.CreateAssetEditorFloatingWindow(RootId, NativeId,
                    Panes[FirstPaneId].Label);
            }

            PublishDockHost(Floating.HostId, Floating.Root, Force);
        }
        foreach (var HostId in LiveFloatingHosts.Where(HostId => !Desired.Contains(HostId))
            .ToArray())
        {
            Runtime.DestroyAssetEditorFloatingWindow(HostId);
            PublishedMarkup.Remove(HostId + ":workspace");
        }

        LiveFloatingHosts.Clear();
        foreach (var HostId in Desired)
        {
            LiveFloatingHosts.Add(HostId);
        }
    }

    private void ApplyLiveProperty(AssetEditorLiveProperty Property)
    {
        if (Runtime.SetAssetEditorElementProperty(RootId, Property.ElementId,
                Property.Name, Property.Value))
        {
            return;
        }
        foreach (var HostId in LiveFloatingHosts)
        {
            if (Runtime.SetAssetEditorElementProperty(HostId, Property.ElementId,
                    Property.Name, Property.Value))
            {
                return;
            }
        }
    }

    private void PublishDockHost(string InternalId, DockNodeState Root, bool Force)
    {
        var NativeId = NativeHostId(InternalId);
        var Markup = AssetEditorDockMarkup.Build(NativeId, Root, Panes,
            Instance.BuildPaneMarkup, Instance.PrimaryPaneId);
        Publish(NativeId, "workspace", Markup, Force);
    }

    private void Publish(string HostId, string ElementId, string Markup, bool Force)
    {
        var Key = HostId + ":" + ElementId;
        if (!Force && PublishedMarkup.TryGetValue(Key, out var Existing) && Existing == Markup)
        {
            return;
        }

        if (Runtime.SetAssetEditorMarkup(HostId, ElementId, Markup))
        {
            PublishedMarkup[Key] = Markup;
        }
    }

    private string BuildMenus()
    {
        var Markup = new StringBuilder();
        AppendMenu(Markup, "File",
        [
            ("Save", "editor.asset-save", !Instance.IsReadOnly && Instance.IsDirty),
            ("Close", "editor.asset-close", true),
        ]);
        foreach (var Group in Instance.Commands.GroupBy(Command => Command.Menu)
            .OrderBy(Group => Group.Key, StringComparer.Ordinal))
        {
            AppendMenu(Markup, Group.Key, Group.OrderBy(Command => Command.Order)
                .Select(Command => (Command.Label,
                    "editor.asset-command:" + Command.CommandId,
                    Instance.CanExecuteCommand(Command.CommandId))).ToArray());
        }
        AppendMenu(Markup, "Window", Instance.Panes.Select(Pane =>
            (Pane.Label, "editor.asset-pane:" + Pane.PaneId, true)).ToArray());
        return Markup.ToString();
    }

    private static void AppendMenu(StringBuilder Markup, string Label,
        IReadOnlyList<(string Label, string Action, bool Enabled)> Items)
    {
        var Id = "asset-menu-" + string.Concat(Label.ToLowerInvariant()
            .Where(Character => char.IsLetterOrDigit(Character)));
        Markup.Append("<div class=\"top-menu\"><button class=\"menu-root\" data-popup-id=\"")
            .Append(Id).Append("\">").Append(WebUtility.HtmlEncode(Label))
            .Append("</button><div id=\"").Append(Id)
            .Append("\" class=\"menu-popup dynamic-popup\">");
        foreach (var Item in Items)
        {
            Markup.Append("<button class=\"menu-command")
                .AppendAvailability(Item.Enabled)
                .AppendAction(Item.Action, Item.Enabled)
                .Append("><span>").Append(WebUtility.HtmlEncode(Item.Label))
                .Append("</span></button>");
        }
        Markup.Append("</div></div>");
    }

    private string BuildToolbar()
    {
        var Markup = new StringBuilder();
        AppendToolbarButton(Markup, "editor.asset-save", "Save", "save",
            !Instance.IsReadOnly && Instance.IsDirty, "Save asset");
        foreach (var Command in Instance.Commands.Where(Command => Command.ShowInToolbar)
            .OrderBy(Command => Command.Order))
        {
            AppendToolbarButton(Markup, "editor.asset-command:" + Command.CommandId,
                Command.Label, Command.Icon, Instance.CanExecuteCommand(Command.CommandId),
                Command.Tooltip ?? Command.Label);
        }
        return Markup.ToString();
    }

    private static void AppendToolbarButton(StringBuilder Markup, string Action,
        string Label, string? Icon, bool Enabled, string Tooltip)
    {
        Markup.Append("<button class=\"toolbar-button")
            .AppendAvailability(Enabled)
            .AppendAction(Action, Enabled)
            .Append(" title=\"").Append(WebUtility.HtmlEncode(Tooltip)).Append("\">");
        if (Icon is not null)
        {
            Markup.Append("<img class=\"icon\" src=\"")
                .Append(WebUtility.HtmlEncode(EditorResourceUris.IconSource(Icon)))
                .Append("\" />");
        }
        else
        {
            Markup.Append(WebUtility.HtmlEncode(Label));
        }
        Markup.Append("</button>");
    }

    private void TogglePane(string PaneId)
    {
        if (!Panes.ContainsKey(PaneId))
        {
            return;
        }

        var Visible = EditorLayoutValidator.EnumeratePanes(Layout.Document.MainHost.Root)
            .Contains(PaneId, StringComparer.Ordinal) || Layout.FloatingHosts.Any(Host =>
                EditorLayoutValidator.EnumeratePanes(Host.Root)
                    .Contains(PaneId, StringComparer.Ordinal));
        ApplyLayout(new EditorLayoutCommand(Visible ? EditorLayoutCommandKind.HidePane :
            EditorLayoutCommandKind.ReopenAsFloating, PaneId));
    }

    private void HandleDockAction(string Action)
    {
        if (Action.StartsWith("editor.dock-close:", StringComparison.Ordinal))
        {
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.HidePane,
                Action["editor.dock-close:".Length..]));
            return;
        }
        if (Action.StartsWith("editor.dock-activate:", StringComparison.Ordinal))
        {
            var Parts = Action["editor.dock-activate:".Length..].Split(':', 3);
            if (Parts.Length == 3)
            {
                ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.ActivateTab,
                    Parts[2], TargetGroupId: Parts[1],
                    TargetHostId: InternalHostId(Parts[0])));
            }
            return;
        }
        if (Action.StartsWith("editor.dock-resize:", StringComparison.Ordinal))
        {
            var Parts = Action["editor.dock-resize:".Length..].Split(':', 2);
            if (Parts.Length != 2)
            {
                return;
            }

            var Ratios = new List<EditorSplitRatio>();
            foreach (var Entry in Parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var Pair = Entry.Split('=', 2);
                if (Pair.Length != 2 || !float.TryParse(Pair[1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var Ratio))
                {
                    return;
                }

                Ratios.Add(new EditorSplitRatio(Pair[0], Ratio));
            }
            if (Ratios.Count != 0)
            {
                ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.ResizeSplit,
                    TargetHostId: InternalHostId(Parts[0]), SplitRatios: Ratios));
            }
            return;
        }
        if (!Action.StartsWith("editor.dock-drop:", StringComparison.Ordinal))
        {
            return;
        }

        var Drop = Action["editor.dock-drop:".Length..].Split(':', 6);
        if (Drop.Length != 6 || !int.TryParse(Drop[5], out var Index))
        {
            return;
        }

        var PaneId = Drop[0];
        var TargetHost = Drop[2] == "none" ? null : InternalHostId(Drop[2]);
        if (Drop[4] == "detach" || TargetHost is null)
        {
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.DetachToFloating,
                PaneId));
            return;
        }
        if (Drop[4] == "center")
        {
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.MergeIntoCenter,
                PaneId, TargetGroupId: Drop[3], Index: Index, TargetHostId: TargetHost));
            return;
        }

        var Edge = Drop[4] switch
        {
            "left" => DockEdge.Left,
            "right" => DockEdge.Right,
            "top" => DockEdge.Top,
            "bottom" => DockEdge.Bottom,
            _ => (DockEdge?)null,
        };
        if (Edge is not null)
        {
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.SplitAtEdge,
                PaneId, TargetNodeId: Drop[3], Edge: Edge.Value, Ratio: 0.25f,
                TargetHostId: TargetHost));
        }
    }

    private void ApplyLayout(EditorLayoutCommand Command)
    {
        var Result = Layout.Apply(Command);
        if (!Result.Success)
        {
            Runtime.SetStatusFromExtension(
                $"Asset editor layout rejected [{Result.Code}]: {Result.Message}");
            return;
        }

        Refresh(true);
    }

    private bool OwnsHost(string HostId) => HostId == RootId ||
        HostId.StartsWith(RootId + ".", StringComparison.Ordinal);

    private string NativeHostId(string InternalId) => InternalId == "main" ? RootId :
        RootId + "." + InternalId;

    private string InternalHostId(string NativeId) => NativeId == RootId ? "main" :
        NativeId.StartsWith(RootId + ".", StringComparison.Ordinal) ?
            NativeId[(RootId.Length + 1)..] : NativeId;

    private string WindowTitle() => $"{Instance.Title}{(Instance.IsDirty ? " *" : string.Empty)} - SharpEngine";

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
    }

    public static string CreateRootId(string AssetId)
    {
        var Hash = SHA256.HashData(Encoding.UTF8.GetBytes(AssetId));
        return "asset-editor-" + Convert.ToHexString(Hash.AsSpan(0, 10)).ToLowerInvariant();
    }
}

public sealed partial class EditorRuntime
{
    internal nint CreateAssetEditorWindow(string RootId, string Title) =>
        _nativeApi.CreateEditorWindow(RootId, Title);

    internal nint CreateAssetEditorFloatingWindow(string RootId, string HostId,
        string Title) => _nativeApi.CreateOwnedDockWindow(RootId, HostId, Title);

    internal bool DestroyAssetEditorFloatingWindow(string HostId) =>
        _nativeApi.DestroyDockWindow(HostId);

    internal bool DestroyAssetEditorWindow(string RootId) =>
        _nativeApi.DestroyEditorWindow(RootId);

    internal bool SetAssetEditorMarkup(string HostId, string ElementId,
        string Markup) => _nativeApi.SetHostMarkup(HostId, ElementId, Markup);

    internal UnsavedWorldChoice PromptUnsavedAsset(string RootId, string Title)
    {
        var Window = _nativeApi.CreateEditorWindow(RootId, Title);
        return NativeEngineApi.PromptUnsavedWorld(Window == 0 ? _windowHandle : Window, Title);
    }

    internal bool TryGetAssetEditorElementRect(string HostId, string ElementId,
        out SharpEngine.UI.UiRect Bounds) =>
        _nativeApi.TryGetHostUiElementRect(HostId, ElementId, out Bounds);

    internal bool SetAssetEditorElementProperty(string HostId, string ElementId,
        string Name, string Value) =>
        _nativeApi.SetHostUiProperty(HostId, ElementId, Name, Value);

    internal void SubmitAssetEditorViewportFrame(string HostId, string Kind,
        ViewportFrame Frame) =>
        _nativeApi.SubmitHostViewportFrame(HostId, Kind, Frame);
}

