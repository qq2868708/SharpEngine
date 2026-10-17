using SharpEngine.Editor.Layout;

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

var defaults = store.LoadDefault(engineRoot);
Require(EditorLayoutValidator.Validate(defaults, requireAllPanesInMain: true).Count == 0,
    "The engine default must be a complete five-pane main-window layout.");
var firstJson = EditorLayoutJson.Serialize(defaults);
var secondJson = EditorLayoutJson.Serialize(EditorLayoutJson.Parse(firstJson));
Require(firstJson == secondJson, "Layout JSON must serialize deterministically.");

try
{
    EditorLayoutJson.Parse(firstJson.Replace("\"schemaVersion\": 1,",
        "\"schemaVersion\": 1,\n  \"unknown\": true,"));
    throw new Exception("Unknown JSON fields must be rejected.");
}
catch (EditorLayoutException exception)
{
    Require(exception.Diagnostics.Any(diagnostic => diagnostic.Code == "layout.json.invalid"),
        "Unknown JSON fields must produce structured diagnostics.");
}

foreach (var invalidJson in new[]
{
    firstJson.Replace("\"schemaVersion\": 1,", string.Empty),
    firstJson.Replace("\"schemaVersion\": 1,",
        "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,"),
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
Require(persistentWhileFloating.HiddenPaneIds.SequenceEqual([EditorPaneIds.Inspector]) &&
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
        new[] { EditorPaneIds.Console, EditorPaneIds.Content, EditorPaneIds.Hierarchy,
            EditorPaneIds.Inspector }.OrderBy(id => id, StringComparer.Ordinal)),
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

var arrangement = DockLayoutCalculator.Arrange(defaults.MainHost.Root,
    new DockRect(0, 0, 500, 300));
Require(arrangement.MinimumSize.Width > 500 && arrangement.MinimumSize.Height > 300 &&
    arrangement.Nodes.Any(node => node.RatioWasClamped),
    "Derived layout must report minimum size pressure and clamp effective ratios.");

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
