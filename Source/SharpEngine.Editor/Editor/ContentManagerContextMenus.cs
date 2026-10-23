using SharpEngine.Assets;
using SharpEngine.Editor.Layout;
using SharpEngine.UI;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEngine.Editor;

internal interface IContentManagerMenuHost
{
    string? ActivePaneId { get; }
    void ActivateContentPane(string PaneId);
    string? FindContentHost(string PaneId);
    EditorCommandContext BindContentContext(EditorSelection Selection, string PaneId, string HostId);
    void ContentSelectionChanged(string Status, bool Rebuild = false);
    bool CanOpenContentAsset(string AssetId);
    void OpenContentAsset(string AssetId);
}

/// <summary>Owns browser context targets and menu contributions, independent of EditorRuntime.</summary>
internal sealed class ContentManagerContextMenus
{
    private readonly IContentManagerMenuHost Host;
    private readonly EditorCommandRegistry Commands;
    private readonly EditorContextMenuRegistry Extensions;
    private readonly EditorPopupMenu Popup;
    private readonly string PaneId;
    private readonly ContentManager Model;
    private readonly Func<bool> IsAvailable;

    internal ContentManagerContextMenus(string PaneId, ContentManager Model, Func<bool> IsAvailable,
        IContentManagerMenuHost Host, EditorCommandRegistry Commands,
        EditorContextMenuRegistry Extensions, EditorPopupMenu Popup)
    {
        this.PaneId = PaneId;
        this.Model = Model;
        this.IsAvailable = IsAvailable;
        this.Host = Host;
        this.Commands = Commands;
        this.Extensions = Extensions;
        this.Popup = Popup;
    }

    internal static IReadOnlyList<IDisposable> RegisterCommands(EditorCommandRegistry Commands,
        Func<EditorCommandContext, ContentManagerContextMenus> ResolveOwner)
    {
        EditorCommand[] Definitions =
        [
            new("core.content-open", "Open",
                Context => ResolveOwner(Context).Open(Context), Context => ResolveOwner(Context).CanOpen(Context)),
            new("core.content-browse-parent", "Browse Containing Folder",
                Context => ResolveOwner(Context).BrowseParent(Context), Context => Context.SelectedAsset is not null),
            new("core.content-refresh", "Refresh", Context =>
                ResolveOwner(Context).ChangeBrowser(Context, Manager =>
                {
                }, "Content refreshed")),
            new("core.content-up", "Go Up", Context =>
                ResolveOwner(Context).ChangeBrowser(Context, Manager => Manager.GoUp(), "Parent folder"),
                Context => ResolveOwner(Context).Model.CurrentFolder.Length > 0),
            new("core.content-clear-filters", "Clear Filters", Context =>
                ResolveOwner(Context).ChangeBrowser(Context, Manager =>
                {
                    Manager.SetSearch(string.Empty);
                    Manager.SetType(null);
                }, "Filters cleared", true)),
            new("core.content-view-tile", "Tile View", Context =>
                ResolveOwner(Context).ChangeBrowser(Context, Manager => Manager.SetViewMode(ContentViewMode.Tile), "Tile view"),
                getChecked: Context => ResolveOwner(Context).Model.ViewMode == ContentViewMode.Tile),
            new("core.content-view-list", "List View", Context =>
                ResolveOwner(Context).ChangeBrowser(Context, Manager => Manager.SetViewMode(ContentViewMode.List), "List view"),
                getChecked: Context => ResolveOwner(Context).Model.ViewMode == ContentViewMode.List),
        ];
        var Registrations = new List<IDisposable>();
        try
        {
            foreach (var Definition in Definitions)
            {
                Registrations.Add(Commands.RegisterOwned(Definition));
            }
            return Registrations;
        }
        catch
        {
            foreach (var Registration in Registrations.AsEnumerable().Reverse())
            {
                Registration.Dispose();
            }
            throw;
        }
    }

    internal bool HandleContext(UiContextMenuEvent Event)
    {
        if (string.IsNullOrEmpty(Event.Payload))
        {
            return false;
        }
        try
        {
            var Data = JsonNode.Parse(Event.Payload)!.AsObject();
            var Kind = Data["kind"]?.GetValue<string>();
            if (Kind is not ("asset" or "folder" or "content"))
            {
                return false;
            }
            var Target = Kind == "content" && Data["target"] is null
                ? Model.CurrentFolder : Data["target"]!.GetValue<string>();
            return ShowRequest(Event.HostId, Event.X, Event.Y, Kind,
                Data["source"]!.GetValue<string>(), Target);
        }
        catch (Exception Error) when (Error is JsonException or InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    // Older semantic editor.action calls stay in the feature that understands their payload.
    internal bool TryHandleRequest(string HostId, string Action)
    {
        const string Prefix = "editor.context:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var Parts = Action[Prefix.Length..].Split(':', 6);
        if (Parts.Length < 3 || Parts[2] is not ("asset" or "folder" or "content"))
        {
            return false;
        }
        var ParsedX = int.TryParse(Parts[0], out var X);
        var ParsedY = int.TryParse(Parts[1], out var Y);
        if (!IsAvailable() || Parts.Length < 5 || !ParsedX || !ParsedY)
        {
            return true;
        }
        var HasPane = Parts.Length == 6 && EditorPaneIds.IsContentManager(Parts[4]);
        var RequestedPane = HasPane ? Parts[4] : Host.ActivePaneId;
        var Target = HasPane ? Parts[5] : string.Join(':', Parts.Skip(4));
        if (RequestedPane != PaneId)
        {
            return false;
        }
        return ShowRequest(HostId, X, Y, Parts[2], Parts[3], Target);
    }

    private bool ShowRequest(string HostId, int X, int Y, string TargetKind, string SourceName, string Target)
    {
        if (TargetKind is not ("asset" or "folder" or "content"))
        {
            return false;
        }
        if (!IsAvailable())
        {
            return true;
        }
        if (Host.FindContentHost(PaneId) != HostId)
        {
            return true;
        }
        Popup.Close(HostId);
        var ParsedSource = Enum.TryParse<AssetSource>(SourceName, true, out var Source);
        if (TargetKind != "content" && (!ParsedSource || Source is not (AssetSource.Engine or AssetSource.Project)))
        {
            return true;
        }
        var Manager = Model;
        if (TargetKind != "content" && Source == AssetSource.Project && Manager.Registry.ProjectRoot is null)
        {
            return true;
        }
        EditorSelection Selection;
        try
        {
            if (TargetKind == "asset")
            {
                var Asset = Manager.Registry.Find(Target);
                var Selected = Asset is null ? Manager.SelectFile(Source, Target) :
                    Asset.Source == Source && Manager.Select(Asset.Id);
                if (!Selected)
                {
                    return true;
                }
                Selection = SelectionOf(Manager);
            }
            else if (TargetKind == "content")
            {
                if (Target != Manager.CurrentFolder)
                {
                    return true;
                }
                Selection = new EditorSelection(EditorSelectionKind.None, Path: Target);
            }
            else
            {
                if (!Manager.SelectFolder(Source, Target))
                {
                    return true;
                }
                Selection = new EditorSelection(EditorSelectionKind.Folder, Path: Manager.SelectedPath, Source: Source);
            }
        }
        catch (Exception Error) when (Error is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
        Host.ActivateContentPane(PaneId);
        Host.ContentSelectionChanged(TargetKind == "content" ? "Content menu" :
            "Selected " + (Selection.Id ?? Selection.Path ?? "Content"));
        var Context = Host.BindContentContext(Selection, PaneId, HostId);
        var Kind = TargetKind == "asset" ? EditorContextMenuKind.Asset :
            TargetKind == "folder" ? EditorContextMenuKind.Folder : EditorContextMenuKind.Content;
        var Sections = new List<(string Id, IReadOnlyList<EditorCommand> Commands)>();
        var Primary = Kind switch
        {
            EditorContextMenuKind.Asset => new[] { "core.content-open", "core.content-browse-parent" },
            EditorContextMenuKind.Folder => new[] { "core.content-open" },
            _ => Array.Empty<string>(),
        };
        Sections.Add(("content.target", Resolve(Primary)));
        Sections.AddRange(Model.MenuSections(Kind));
        foreach (var Category in Extensions.OrderedCategories(Kind))
        {
            Sections.Add((Category.Id, Resolve(Extensions.OrderedItems(Kind, Category.Id).Select(Item => Item.CommandId))));
        }
        if (Kind == EditorContextMenuKind.Content)
        {
            Sections.Add(("content.navigation", Resolve(["core.content-up", "core.content-refresh", "core.content-clear-filters"])));
            Sections.Add(("content.view", Resolve(["core.content-view-tile", "core.content-view-list"])));
        }
        Popup.Show(HostId, X, Y, Context, Sections,
            () => IsAvailable() && Host.FindContentHost(PaneId) == HostId &&
                (Kind == EditorContextMenuKind.Content
                    ? Manager.CurrentFolder == Target
                    : TargetExists(Manager, Selection)), Model.MenuCommands);
        return true;
    }

    internal bool SelectFolder(AssetSource Source, string Path)
    {
        if (!IsAvailable() || !Model.SelectFolder(Source, Path))
        {
            return false;
        }
        Host.ActivateContentPane(PaneId);
        Host.ContentSelectionChanged("Selected Content/" + Path);
        return true;
    }

    internal static EditorSelection SelectionOf(ContentManager Manager)
    {
        var Asset = Manager.SelectedAsset;
        if (Asset is not null)
        {
            return new EditorSelection(EditorSelectionKind.Asset, Asset.Id, Manager.SelectedPath, Asset.Source);
        }
        if (Manager.SelectedPath is not null && Manager.SelectedSource is not null)
        {
            return new EditorSelection(Manager.IsFolder(Manager.SelectedSource.Value, Manager.SelectedPath)
                ? EditorSelectionKind.Folder : EditorSelectionKind.Asset,
                Path: Manager.SelectedPath, Source: Manager.SelectedSource);
        }
        return EditorSelection.None;
    }

    private static bool TargetExists(ContentManager Manager, EditorSelection Selection)
    {
        if (Selection.Source is null || Selection.Path is null)
        {
            return false;
        }
        if (Selection.Id is not null)
        {
            var Asset = Manager.Registry.Find(Selection.Id);
            return Asset is not null && File.Exists(Manager.Registry.ResolvePath(Asset));
        }
        return Selection.Kind == EditorSelectionKind.Folder
            ? Manager.IsFolder(Selection.Source.Value, Selection.Path)
            : Manager.IsFile(Selection.Source.Value, Selection.Path);
    }

    private IReadOnlyList<EditorCommand> Resolve(IEnumerable<string> Ids)
    {
        var Result = new List<EditorCommand>();
        foreach (var Id in Ids)
        {
            var Found = Commands.TryGet(Id, out var Command);
            if (Found)
            {
                Result.Add(Command);
            }
        }
        return Result;
    }

    private bool CanOpen(EditorCommandContext Context)
    {
        var Manager = Model;
        return Context.Selection.Kind == EditorSelectionKind.Folder
            ? Manager is not null && Context.Selection.Source is not null && Context.Selection.Path is not null &&
                Manager.IsFolder(Context.Selection.Source.Value, Context.Selection.Path)
            : Context.SelectedAsset is { } Asset && Host.CanOpenContentAsset(Asset.Id);
    }

    private void Open(EditorCommandContext Context)
    {
        if (Context.Selection.Kind == EditorSelectionKind.Folder)
        {
            ChangeBrowser(Context, Manager =>
            {
                Manager.SetSource(Context.Selection.Source == AssetSource.Engine ? ContentSourceFilter.Engine : ContentSourceFilter.Project);
                Manager.OpenFolder(Context.Selection.Path);
            }, "Folder opened", true);
        }
        else if (Context.SelectedAsset is not null)
        {
            Host.OpenContentAsset(Context.SelectedAsset.Id);
        }
    }

    private void BrowseParent(EditorCommandContext Context)
    {
        var Asset = Context.SelectedAsset;
        if (Asset is null)
        {
            return;
        }
        ChangeBrowser(Context, Manager =>
        {
            Manager.SetSource(Asset.Source == AssetSource.Engine ? ContentSourceFilter.Engine : ContentSourceFilter.Project);
            Manager.SetSearch(string.Empty);
            Manager.SetType(null);
            Manager.Select(Asset.Id);
            var Path = Manager.SelectedPath!;
            var Slash = Path.LastIndexOf('/');
            Manager.OpenFolder(Slash < 0 ? string.Empty : Path[..Slash]);
            Manager.Select(Asset.Id);
        }, "Showing " + Asset.Name, true);
    }

    private void ChangeBrowser(EditorCommandContext Context, Action<ContentManager> Change, string Status, bool Rebuild = false)
    {
        var Manager = Model;
        if (!IsAvailable())
        {
            return;
        }
        Change(Manager);
        Host.ActivateContentPane(PaneId);
        Host.ContentSelectionChanged(Status, Rebuild);
    }
}
