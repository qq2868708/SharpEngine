using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;
using SharpEngine.UI;

namespace SharpEngine.Editor;

/// <summary>Owns the four independent content browsers and routes external
/// browse operations to the most recently active instance.</summary>
public sealed class ContentManagerCoordinator : IDisposable
{
    private readonly Dictionary<string, ContentManager> _managers =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _mostRecentlyUsed = [];
    private readonly Dictionary<string, (ContentManager Pane,
        Func<EditorCommandContext, bool> Handler)> CommandHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentManagerView> Views = new(StringComparer.Ordinal);
    private readonly List<IDisposable> CommandRegistrations = [];

    internal void AttachUi(IContentManagerMenuHost Host, EditorCommandRegistry Commands,
        EditorContextMenuRegistry Extensions, EditorPopupMenu Popup, UiContextMenuRouter Routing)
    {
        if (Views.Count != 0)
        {
            throw new InvalidOperationException("Content Manager views are already attached");
        }
        try
        {
            foreach (var PaneId in EditorPaneIds.ContentManagers)
            {
                Views.Add(PaneId, new ContentManagerView(PaneId, Get(PaneId), Host, Commands, Extensions, Popup, Routing));
            }
            CommandRegistrations.AddRange(ContentManagerContextMenus.RegisterCommands(Commands,
                Context => Views[Context.ContentPaneId ?? ActivePaneId].Menu));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool SelectFolder(string PaneId, AssetSource Source, string Path)
    {
        return Views[PaneId].Menu.SelectFolder(Source, Path);
    }

    internal string BuildEntries(string PaneId, AssetSource Source)
    {
        return Views[PaneId].BuildEntries(Source);
    }

    public void Dispose()
    {
        foreach (var View in Views.Values)
        {
            View.Dispose();
        }
        Views.Clear();
        foreach (var Registration in CommandRegistrations.AsEnumerable().Reverse())
        {
            Registration.Dispose();
        }
        CommandRegistrations.Clear();
        CommandHandlers.Clear();
    }

    public ContentManagerCoordinator(AssetRegistry registry,
        IReadOnlyList<ContentManagerLayoutState> states, string activePaneId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(states);
        foreach (var paneId in EditorPaneIds.ContentManagers)
        {
            var manager = new ContentManager(paneId, registry);
            var state = states.Single(item => item.PaneId == paneId);
            Restore(manager, state);
            var Owner = manager;
            manager.PublishCommands((Action, Handler) =>
                CommandHandlers[Action] = (Owner, Handler));
            manager.DeleteRequested += asset => DeleteRequested?.Invoke(asset);
            _managers.Add(paneId, manager);
        }
        ActivePaneId = EditorPaneIds.IsContentManager(activePaneId)
            ? activePaneId : EditorPaneIds.Content;
        _mostRecentlyUsed.AddFirst(ActivePaneId);
        foreach (var paneId in EditorPaneIds.ContentManagers)
            if (paneId != ActivePaneId) _mostRecentlyUsed.AddLast(paneId);
    }

    /// <summary>Dispatches a pane-published action straight to the instance closure that
    /// registered it. The key is opaque: no caller parses a pane or command out of it, and
    /// the context is built for the owning pane rather than whichever pane is active.</summary>
    public bool TryHandleCommand(string action,
        Func<ContentManager, EditorCommandContext> createContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(createContext);
        var Found = CommandHandlers.TryGetValue(action, out var Entry);
        if (!Found)
        {
            return false;
        }
        return Entry.Handler(createContext(Entry.Pane));
    }

    public string ActivePaneId { get; private set; }
    public ContentManager Active => _managers[ActivePaneId];
    public IReadOnlyList<string> MostRecentlyUsed => _mostRecentlyUsed.ToArray();

    /// <summary>Aggregates the delete intent raised by any pane. The coordinator only
    /// forwards it; deciding and performing the removal belongs to the editor workflow that
    /// owns the asset manifest.</summary>
    public event Action<AssetDescriptor>? DeleteRequested;

    public ContentManager Get(string paneId) => _managers.TryGetValue(paneId, out var manager)
        ? manager : throw new ArgumentOutOfRangeException(nameof(paneId), paneId,
            "Unknown Content Manager pane ID");

    public bool Activate(string paneId)
    {
        if (!_managers.ContainsKey(paneId)) return false;
        ActivePaneId = paneId;
        _mostRecentlyUsed.Remove(paneId);
        _mostRecentlyUsed.AddFirst(paneId);
        return true;
    }

    public IReadOnlyList<ContentManagerLayoutState> Capture() =>
        EditorPaneIds.ContentManagers.Select(paneId =>
        {
            var manager = _managers[paneId];
            return new ContentManagerLayoutState
            {
                PaneId = paneId,
                Search = manager.SearchText,
                Source = manager.SourceFilter,
                Type = manager.TypeFilter,
                CurrentFolder = manager.CurrentFolder,
                SelectedId = manager.SelectedId,
                SelectedPath = manager.SelectedPath,
                SelectedSource = manager.SelectedSource,
                ViewMode = manager.ViewMode,
            };
        }).ToArray();

    public string ToJson(JsonSerializerOptions? options = null) => JsonSerializer.Serialize(new
    {
        schemaVersion = 2,
        activePaneId = ActivePaneId,
        managers = EditorPaneIds.ContentManagers.Select(paneId => new
        {
            paneId,
            number = EditorPaneIds.ContentManagerNumber(paneId),
            active = paneId == ActivePaneId,
            state = JsonNode.Parse(_managers[paneId].ToJson()),
        }),
    }, options ?? new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    });

    private static void Restore(ContentManager manager, ContentManagerLayoutState state)
    {
        manager.RestoreBrowserState(state.Search, state.Source, state.Type,
            state.CurrentFolder, state.SelectedId, state.SelectedPath, state.SelectedSource,
            state.ViewMode);
    }
}
