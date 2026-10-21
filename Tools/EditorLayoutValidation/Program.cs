using SharpEngine.Editor.Layout;
using SharpEngine.Assets;
using SharpEngine.Editor;
using System.Text.Json.Nodes;

var engineRoot = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var store = new EditorLayoutStore();
var assertions = 0;
void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}
DockTabGroupState? FindGroup(DockNodeState node, string groupId)
{
    if (node.Kind == DockNodeKind.Tabs)
        return node.TabGroup?.GroupId == groupId ? node.TabGroup : null;
    return node.First is null ? null : FindGroup(node.First, groupId) ??
        (node.Second is null ? null : FindGroup(node.Second, groupId));
}

AssetEditorLayoutCompositionValidation.Run(engineRoot, Require);
var defaults = store.LoadDefault(engineRoot);
Require(EditorLayoutValidator.Validate(defaults, requireAllPanesInMain: true).Count == 0,
    "The engine default must expose Content Manager 1 and hide Content Managers 2-4.");
Require(defaults.HiddenPaneIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(
        new[] { EditorPaneIds.Content2, EditorPaneIds.Content3, EditorPaneIds.Content4,
            EditorPaneIds.Timeline, EditorPaneIds.UiEditor }.OrderBy(id => id,
                StringComparer.Ordinal)) &&
    defaults.ContentManagers.Count == 4 &&
    defaults.ContentManagers.All(state => state.ViewMode == ContentViewMode.Tile),
    "The default layout must own four tile-mode content manager states.");
var firstJson = EditorLayoutJson.Serialize(defaults);
var secondJson = EditorLayoutJson.Serialize(EditorLayoutJson.Parse(firstJson));
Require(firstJson == secondJson, "Layout JSON must serialize deterministically.");

var registry = AssetRegistry.Load(engineRoot, null, null);
var coordinator = new ContentManagerCoordinator(registry, defaults.ContentManagers,
    defaults.ActiveContentManagerId);
var manager1 = coordinator.Get(EditorPaneIds.Content);
manager1.SetSource(ContentSourceFilter.Engine);
Require(manager1.Select("engine://meshes/cube"),
    "Content Manager 1 should select a registered engine asset.");
var manager2 = coordinator.Get(EditorPaneIds.Content2);
manager2.SetSource(ContentSourceFilter.Engine);
manager2.SetViewMode(ContentViewMode.List);
coordinator.Activate(EditorPaneIds.Content2);
Require(coordinator.Active == manager2 && manager2.SelectedId is null &&
    manager1.SelectedId == "engine://meshes/cube" &&
    coordinator.Capture().Single(state => state.PaneId == EditorPaneIds.Content2)
        .ViewMode == ContentViewMode.List,
    "Content manager routing must preserve independent selection and view state.");

var legacy = JsonNode.Parse(firstJson)!.AsObject();
legacy["schemaVersion"] = 1;
legacy.Remove("activeContentManagerId");
legacy.Remove("contentManagers");
var legacyHidden = legacy["hiddenPaneIds"]!.AsArray();
legacyHidden.Clear();
var migrated = EditorLayoutJson.Parse(legacy.ToJsonString());
Require(migrated.SchemaVersion == EditorLayoutDocument.CurrentSchemaVersion &&
    migrated.HiddenPaneIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(
        new[] { EditorPaneIds.Content2, EditorPaneIds.Content3, EditorPaneIds.Content4,
            EditorPaneIds.Timeline, EditorPaneIds.UiEditor }.OrderBy(id => id,
                StringComparer.Ordinal)) &&
    migrated.ContentManagers.Count == 4,
    "Schema v1 layouts must migrate with optional Content Managers and legacy asset panes hidden.");

try
{
    var schemaField = $"\"schemaVersion\": {EditorLayoutDocument.CurrentSchemaVersion},";
    EditorLayoutJson.Parse(firstJson.Replace(schemaField,
        $"{schemaField}\n  \"unknown\": true,"));
    throw new Exception("Unknown JSON fields must be rejected.");
}
catch (EditorLayoutException exception)
{
    Require(exception.Diagnostics.Any(diagnostic => diagnostic.Code == "layout.json.invalid"),
        "Unknown JSON fields must produce structured diagnostics.");
}

foreach (var invalidJson in new[]
{
    firstJson.Replace($"\"schemaVersion\": {EditorLayoutDocument.CurrentSchemaVersion},", string.Empty),
    firstJson.Replace($"\"schemaVersion\": {EditorLayoutDocument.CurrentSchemaVersion},",
        $"\"schemaVersion\": {EditorLayoutDocument.CurrentSchemaVersion},\n  \"schemaVersion\": {EditorLayoutDocument.CurrentSchemaVersion},"),
})
{
    try
    {
        EditorLayoutJson.Parse(invalidJson);
        throw new Exception("Missing and duplicate required fields must be rejected.");
    }
    catch (EditorLayoutException)
    {
        assertions++;
    }
}

var session = new EditorLayoutSession(defaults);
Require(typeof(EditorLayoutSession).GetMethod("Undo") is null &&
    typeof(EditorLayoutSession).GetMethod("Redo") is null,
    "Dock layout operations must not expose Undo or Redo history.");
var initialJson = EditorLayoutJson.Serialize(session.Document);
var rejectViewportHide = session.Apply(new(EditorLayoutCommandKind.HidePane,
    EditorPaneIds.Viewport));
Require(!rejectViewportHide.Success && session.Revision == 0 &&
    EditorLayoutJson.Serialize(session.Document) == initialJson,
    "Viewport hide must fail without partially mutating the session.");

Require(session.Apply(new(EditorLayoutCommandKind.HidePane,
    EditorPaneIds.Inspector)).Success, "Inspector hide must succeed.");
Require(session.Apply(new(EditorLayoutCommandKind.ReopenAsFloating,
    EditorPaneIds.Inspector)).Success && session.FloatingHosts.Count == 1,
    "A hidden non-viewport pane must reopen as a session-only floating host.");
var persistentWhileFloating = session.CreatePersistentSnapshot();
Require(persistentWhileFloating.HiddenPaneIds.SequenceEqual(new[]
    {
        EditorPaneIds.Content2, EditorPaneIds.Content3, EditorPaneIds.Content4,
        EditorPaneIds.Inspector, EditorPaneIds.Timeline, EditorPaneIds.UiEditor,
    }.OrderBy(Id => Id, StringComparer.Ordinal)) &&
    !EditorLayoutJson.Serialize(persistentWhileFloating).Contains("floating", StringComparison.OrdinalIgnoreCase),
    "Floating host state must project to hidden state and never serialize.");

var multiHost = new EditorLayoutSession(defaults);
Require(multiHost.Apply(new(EditorLayoutCommandKind.HidePane,
    EditorPaneIds.Inspector)).Success &&
    multiHost.Apply(new(EditorLayoutCommandKind.ReopenAsFloating,
        EditorPaneIds.Inspector)).Success,
    "A hidden pane must establish a floating dock host.");
var firstFloat = multiHost.FloatingHosts.Single();
var firstFloatGroup = firstFloat.Root.TabGroup!.GroupId;
var firstFloatRoot = firstFloat.Root.NodeId;
Require(multiHost.Apply(new(EditorLayoutCommandKind.MergeIntoCenter,
    EditorPaneIds.Content, TargetGroupId: firstFloatGroup,
    TargetHostId: firstFloat.HostId)).Success,
    "Center merge must support moving a main-host pane into a floating host.");
var beforeFloatActivation = multiHost.Revision;
Require(multiHost.Apply(new(EditorLayoutCommandKind.ActivateTab,
        EditorPaneIds.Inspector, TargetGroupId: firstFloatGroup,
        TargetHostId: firstFloat.HostId)).Success &&
    multiHost.Revision == beforeFloatActivation + 1 &&
    multiHost.FloatingHosts.Single().Root.TabGroup!.ActivePaneId == EditorPaneIds.Inspector,
    "ActivateTab must switch only the active pane in a floating tab group and commit one revision.");
var beforeInvalidResize = multiHost.Revision;
Require(!multiHost.Apply(new(EditorLayoutCommandKind.ResizeSplit,
        TargetNodeId: firstFloatRoot, Ratio: 0.4f,
        TargetHostId: firstFloat.HostId)).Success &&
    multiHost.Revision == beforeInvalidResize,
    "ResizeSplit must reject a tab node without committing.");
Require(multiHost.Apply(new(EditorLayoutCommandKind.SplitAtEdge,
    EditorPaneIds.Console, TargetNodeId: firstFloatRoot, Edge: DockEdge.Bottom,
    Ratio: 0.3f, TargetHostId: firstFloat.HostId)).Success,
    "Edge split must support a target node in a floating host.");
Require(multiHost.Apply(new(EditorLayoutCommandKind.ResizeSplit,
        TargetNodeId: firstFloatRoot, Ratio: 2f,
        TargetHostId: firstFloat.HostId)).Success &&
    multiHost.FloatingHosts.Single().Root.Ratio == EditorLayoutValidator.MaximumStoredRatio,
    "ResizeSplit must clamp and commit a floating-host split ratio.");
Require(multiHost.Apply(new(EditorLayoutCommandKind.DetachToFloating,
    EditorPaneIds.Hierarchy)).Success && multiHost.FloatingHosts.Count == 2,
    "Detach must create another session-only floating host.");
var secondFloat = multiHost.FloatingHosts.Single(host => host.HostId != firstFloat.HostId);
Require(multiHost.Apply(new(EditorLayoutCommandKind.MergeIntoCenter,
    EditorPaneIds.Hierarchy, TargetGroupId: firstFloatGroup,
    TargetHostId: firstFloat.HostId)).Success &&
    multiHost.FloatingHosts.All(host => host.HostId != secondFloat.HostId),
    "Moving the last pane out of a floating host must remove the empty host.");
var revisionBeforeViewportReject = multiHost.Revision;
Require(!multiHost.Apply(new(EditorLayoutCommandKind.DetachToFloating,
        EditorPaneIds.Viewport)).Success &&
    !multiHost.Apply(new(EditorLayoutCommandKind.MergeIntoCenter,
        EditorPaneIds.Viewport, TargetGroupId: firstFloatGroup,
        TargetHostId: firstFloat.HostId)).Success &&
    multiHost.Revision == revisionBeforeViewportReject,
    "Every attempt to move the viewport outside main must fail atomically.");
Require(multiHost.Apply(new(EditorLayoutCommandKind.CloseFloatingHost,
    TargetHostId: firstFloat.HostId)).Success && multiHost.FloatingHosts.Count == 0 &&
    multiHost.Document.HiddenPaneIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(
        new[] { EditorPaneIds.Console, EditorPaneIds.Content, EditorPaneIds.Content2,
            EditorPaneIds.Content3, EditorPaneIds.Content4, EditorPaneIds.Hierarchy,
            EditorPaneIds.Inspector, EditorPaneIds.Timeline,
            EditorPaneIds.UiEditor }.OrderBy(id => id, StringComparer.Ordinal)),
    "Closing a floating host must hide every pane in its complete dock tree.");
Require(EditorLayoutValidator.ValidateSession(multiHost.Document,
    multiHost.FloatingHosts).Count == 0,
    "Multi-host docking commands must preserve the complete session invariant.");

Require(session.Apply(new(EditorLayoutCommandKind.MergeIntoCenter,
    EditorPaneIds.Inspector, TargetGroupId: "main.viewport.tabs", Index: 1)).Success &&
    session.FloatingHosts.Count == 0,
    "Center merge must redock a floating pane into a main-host tab group.");
Require(session.Apply(new(EditorLayoutCommandKind.ReorderTab,
    EditorPaneIds.Inspector, TargetGroupId: "main.viewport.tabs", Index: 0)).Success,
    "Tab reorder must work inside a group.");
Require(session.Apply(new(EditorLayoutCommandKind.ActivateTab,
        EditorPaneIds.Viewport, TargetGroupId: "main.viewport.tabs",
        TargetHostId: "main")).Success &&
    FindGroup(session.Document.MainHost.Root, "main.viewport.tabs")?.ActivePaneId ==
        EditorPaneIds.Viewport,
    "ActivateTab must switch a main-host tab without moving it.");
Require(session.Apply(new(EditorLayoutCommandKind.ResizeSplit,
        TargetNodeId: "main.root", Ratio: -1f, TargetHostId: "main")).Success &&
    session.Document.MainHost.Root.Ratio == EditorLayoutValidator.MinimumStoredRatio,
    "ResizeSplit must clamp and commit a main-host split ratio.");
var resizeSession = new EditorLayoutSession(defaults);
Require(resizeSession.Apply(new(EditorLayoutCommandKind.ResizeSplit,
        TargetHostId: "main", SplitRatios:
        [new("main.root", 0.25f), new("main.body", 0.7344f)])).Success &&
    Math.Abs(resizeSession.Document.MainHost.Root.Ratio!.Value - 0.25f) < 0.0001f &&
    Math.Abs(resizeSession.Document.MainHost.Root.Second!.Ratio!.Value - 0.7344f) < 0.0001f &&
    resizeSession.Revision == 1,
    "ResizeSplit must atomically commit every ratio needed to preserve adjacent boundaries.");
var beforeInvalidBatch = EditorLayoutJson.Serialize(resizeSession.Document);
Require(!resizeSession.Apply(new(EditorLayoutCommandKind.ResizeSplit,
        TargetHostId: "main", SplitRatios:
        [new("main.root", 0.3f), new("missing", 0.5f)])).Success &&
    resizeSession.Revision == 1 &&
    EditorLayoutJson.Serialize(resizeSession.Document) == beforeInvalidBatch,
    "An invalid resize batch must fail without a partial layout mutation.");
Require(session.Apply(new(EditorLayoutCommandKind.SplitAtEdge,
    EditorPaneIds.Console, TargetNodeId: "main.viewport", Edge: DockEdge.Bottom,
    Ratio: 0.25f)).Success,
    "Edge split must move a pane into a new main-host split.");
Require(EditorLayoutValidator.ValidateSession(session.Document, session.FloatingHosts).Count == 0,
    "Every successful command must leave a valid session.");

var beforeHideRevision = session.Revision;
Require(session.Apply(new(EditorLayoutCommandKind.HidePane, EditorPaneIds.Content)).Success &&
    session.Revision == beforeHideRevision + 1,
    "A layout command must commit one monotonic revision without creating history.");

var contentStates = session.Document.ContentManagers;
contentStates.Single(state => state.PaneId == EditorPaneIds.Content2).ViewMode =
    ContentViewMode.List;
contentStates.Single(state => state.PaneId == EditorPaneIds.Content2).CurrentFolder = "UI";
session.UpdateContentManagers(contentStates, EditorPaneIds.Content2);
Require(session.Document.ActiveContentManagerId == EditorPaneIds.Content2 &&
    session.Document.ContentManagers.Single(state =>
        state.PaneId == EditorPaneIds.Content2).ViewMode == ContentViewMode.List &&
    session.IsDirty,
    "Content manager state and active routing must participate in layout persistence.");

var arrangement = DockLayoutCalculator.Arrange(defaults.MainHost.Root,
    new DockRect(0, 0, 500, 300));
Require(arrangement.MinimumSize.Width > 500 && arrangement.MinimumSize.Height > 300 &&
    arrangement.Nodes.Any(node => node.RatioWasClamped),
    "Derived layout must report minimum size pressure and clamp effective ratios.");

var AssetLayout = new EditorLayoutDocument
{
    MainHost = new DockHostState
    {
        HostId = "main",
        Root = new DockNodeState
        {
            NodeId = "asset.root",
            Kind = DockNodeKind.Tabs,
            TabGroup = new DockTabGroupState
            {
                GroupId = "asset.tabs",
                ActivePaneId = "preview",
                Panes =
                [
                    new DockPaneState { PaneId = "preview" },
                    new DockPaneState { PaneId = "timeline" },
                    new DockPaneState { PaneId = "details" },
                ],
            },
        },
    },
    HiddenPaneIds = [],
    ActiveContentManagerId = string.Empty,
    ContentManagers = [],
};
var AssetSession = new EditorLayoutSession(AssetLayout,
    ["preview", "timeline", "details"], "preview");
Require(!AssetSession.Apply(new(EditorLayoutCommandKind.DetachToFloating,
        "preview")).Success,
    "An asset editor primary pane must remain in its top-level editor root.");
Require(AssetSession.Apply(new(EditorLayoutCommandKind.DetachToFloating,
        "details")).Success && AssetSession.FloatingHosts.Count == 1 &&
    EditorLayoutValidator.ValidateSession(AssetSession.Document,
        AssetSession.FloatingHosts, ["preview", "timeline", "details"],
        "preview", false).Count == 0,
    "An ordinary asset-editor pane may become an owner-scoped floating tab.");

var temporary = Directory.CreateTempSubdirectory("SharpEngine-EditorLayoutValidation-");
try
{
    var temporaryEngine = Directory.CreateDirectory(Path.Combine(temporary.FullName, "Engine"));
    var defaultDirectory = Directory.CreateDirectory(Path.Combine(temporaryEngine.FullName,
        "Config"));
    File.Copy(Path.Combine(engineRoot, EditorLayoutStore.DefaultRelativePath.Replace('/',
        Path.DirectorySeparatorChar)), Path.Combine(defaultDirectory.FullName,
        "DefaultEditorLayout.json"));
    var project = Directory.CreateDirectory(Path.Combine(temporary.FullName, "Project"));
    var savedPath = EditorLayoutStore.GetUserPath(temporaryEngine.FullName, project.FullName);
    Require(!File.Exists(savedPath), "Commands and Reset must not implicitly save a user layout.");
    session.Save(store, temporaryEngine.FullName, project.FullName);
    Require(File.Exists(savedPath) && !session.IsDirty,
        "Explicit Save must atomically create the user layout and clear dirty state.");
    var saved = File.ReadAllText(savedPath);
    Require(EditorLayoutValidator.Validate(store.LoadSavedOrDefault(temporaryEngine.FullName,
        project.FullName)).Count == 0, "The saved user layout must round-trip.");
    session.ResetToEngineDefault(store.LoadDefault(temporaryEngine.FullName));
    Require(session.IsDirty && File.ReadAllText(savedPath) == saved,
        "Reset must restore only the live session until an explicit Save.");
}
finally
{
    temporary.Delete(true);
}

Console.WriteLine($"PASS: {assertions} editor layout assertions");
