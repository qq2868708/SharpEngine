using SharpEngine.Editor;
using SharpEngine.Editor.Layout;
using SharpEngine.UI;
using SharpEngine.World;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var previousRoot = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT");
var previousManifest = Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST");
var temporary = Directory.CreateTempSubdirectory("SharpEngine-EditorValidation-");
var assertions = 0;
void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}
static bool MatrixNear(System.Numerics.Matrix4x4 left, System.Numerics.Matrix4x4 right)
{
    var a = new[] { left.M11, left.M12, left.M13, left.M14, left.M21, left.M22,
        left.M23, left.M24, left.M31, left.M32, left.M33, left.M34, left.M41,
        left.M42, left.M43, left.M44 };
    var b = new[] { right.M11, right.M12, right.M13, right.M14, right.M21, right.M22,
        right.M23, right.M24, right.M31, right.M32, right.M33, right.M34, right.M41,
        right.M42, right.M43, right.M44 };
    return a.Zip(b).All(pair => Math.Abs(pair.First - pair.Second) < 0.001f);
}
try
{
    var worlds = Directory.CreateDirectory(Path.Combine(temporary.FullName, "Content", "Worlds"));
    var assets = Directory.CreateDirectory(Path.Combine(temporary.FullName, "Content", "Assets"));
    var worldPath = Path.Combine(worlds.FullName, "Main.sworld");
    File.Copy(Path.Combine(root, "Content", "Worlds", "Main.sworld"), worldPath);
    File.WriteAllText(Path.Combine(assets.FullName, "assets.json"), """
        {"schemaVersion":1,"assets":[{"id":"worlds/main","type":"World","path":"Content/Worlds/Main.sworld"}]}
        """);
    var manifest = Path.Combine(temporary.FullName, "Validation.sharpproject");
    File.WriteAllText(manifest, """
        {"schemaVersion":3,"projectId":"validation","engine":{"id":"SharpEngine","version":"0.2.0-dev","association":"validation","manifest":"engine.json"},"modules":[],"assets":{"manifest":"Content/Assets/assets.json"},"world":{"startup":"Content/Worlds/Main.sworld"}}
        """);
    Environment.SetEnvironmentVariable("SHARPENGINE_ROOT", root);
    Environment.SetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST", manifest);
    var runtime = new EditorRuntime(0, 1440, 900, 0);
    runtime.Initialize();
    var defaultLayout = new EditorLayoutStore().LoadDefault(root);
    var dockMarkup = EditorDockMarkup.Build(defaultLayout.MainHost, "Main Scene");
    Require(dockMarkup.Contains("data-dock-split-node=\"main.root\"", StringComparison.Ordinal) &&
        dockMarkup.Contains("data-dock-split-part=\"first\"", StringComparison.Ordinal) &&
        dockMarkup.Contains("data-dock-split-part=\"second\"", StringComparison.Ordinal) &&
        dockMarkup.Contains("data-dock-split-ratio=\"0.17\"", StringComparison.Ordinal),
        "Dock markup must expose recursive split containers for transient resize preview");
    Require(runtime.World?.SourcePath == worldPath, "Editor must load the project-owned startup world");
    Require(runtime.Assets!.EngineAssets.Any() && runtime.Assets.ProjectAssets.Count() == 1,
        "Public and project assets must both be available to a project editor");
    Require(runtime.Assets.Find("engine://worlds/main")?.ReadOnly == false &&
        runtime.Assets.Find("engine://meshes/cube")?.ReadOnly == true,
        "Engine Worlds must remain ordinary editable assets without changing other asset policies");
    Require(runtime.Assets.Diagnostics.Count == 0, "Project and engine asset manifests must resolve cleanly");
    Require(runtime.Content is not null && runtime.Content.SourceFilter == SharpEngine.Assets.ContentSourceFilter.Project &&
        runtime.Content.ContentRoot == Path.Combine(temporary.FullName, "Content") &&
        runtime.Content.VisibleEntries.Any(entry => entry.IsDirectory && entry.Name == "Worlds"),
        "Content manager should open the project Content folder and show directories");
    var content = runtime.Content!;
    runtime.InvokeUiAction("editor.content.open:project|Worlds");
    Require(content.CurrentFolder == "Worlds" &&
        content.VisibleEntries.Any(entry => entry.Name == "Main.sworld"),
        "Content manager should enter a Content subfolder and browse files");
    runtime.InvokeUiAction("editor.content.type:World");
    Require(content.VisibleAssets.Count == 1 && content.VisibleAssets[0].Type == "World",
        "Content manager type filter should narrow the visible assets");
    runtime.InvokeUiAction("editor.content.open:project|Worlds/Main.sworld");
    Require(content.SelectedId == "project://worlds/main" &&
        content.SelectedPath == "Worlds/Main.sworld",
        "Content manager should select a browsed manifest asset");
    runtime.InvokeUiAction("editor.content.up");
    runtime.InvokeUiAction("editor.content.clear");
    Require(content.SourceFilter == SharpEngine.Assets.ContentSourceFilter.Project &&
        content.TypeFilter is null && content.SearchText.Length == 0,
        "Content manager clear should reset all filters");
    runtime.InvokeUiAction("editor.content.source:engine");
    runtime.InvokeUiAction("editor.search-assets:cube");
    Require(content.VisibleAssets.Any(asset => asset.Name == "Cube"),
        "Content manager search should match asset names");
    runtime.InvokeUiAction("editor.select-asset:engine://meshes/cube");
    Require(content.SelectedId == "engine://meshes/cube" &&
        runtime.Selection.Kind == EditorSelectionKind.Asset &&
        runtime.SelectedEntity is null &&
        runtime.DescribeContentJson().Contains("engine://meshes/cube", StringComparison.Ordinal),
        "Asset selection must replace the active entity selection and persist in Content Manager");
    runtime.InvokeUiAction("editor.context:10:10:folder:project:Worlds");
    Require(runtime.Selection.Kind == EditorSelectionKind.Folder &&
        runtime.SelectedFolderPath == "Worlds" && runtime.SelectedEntity is null,
        "Folder context selection must replace selection from every other editor surface");
    runtime.InvokeUiAction("editor.content.clear");
    Require(!runtime.Settings.InvertYAxis, "Engine Settings should default to normal Y navigation");
    var commandCalls = 0;
    using var commandRegistration = runtime.Commands.Register(new EditorCommand(
        "validation.raise-status",
        "Raise Status",
        context =>
        {
            commandCalls++;
            context.MarkDirty();
            context.SetStatus("Validation command executed");
        },
        context => context.SelectedEntity is not null,
        "Run the managed validation command"));
    using var toolbarCategory = runtime.Toolbar.RegisterCategory(
        new EditorToolbarCategory("validation", 500));
    using var toolbarRegistration = runtime.Toolbar.RegisterTool(new EditorToolbarTool(
        "validation.raise-status", "validation", "Raise Status",
        ["validation.raise-status"], 10));
    var extensionSnapshot = runtime.DescribeEditorJson();
    Require(extensionSnapshot.Contains("validation.raise-status", StringComparison.Ordinal) &&
        extensionSnapshot.Contains("Raise Status", StringComparison.Ordinal),
        "Managed editor commands and Toolbar contributions must be discoverable");
    runtime.InvokeUiAction("editor.select-entity:player");
    runtime.InvokeUiAction("editor.command:validation.raise-status");
    Require(commandCalls == 1 && runtime.IsDirty,
        "Toolbar command action must execute its managed callback and mark the editor dirty");
    runtime.InvokeUiAction("editor.save");
    Require(!runtime.IsDirty, "Editor save should clear managed extension dirty state");
    var player = runtime.World!.Entities.First(entity => entity.Id == "player");
    var playerWorld = runtime.World.WorldMatrix(player);
    var hierarchyRevision = runtime.DocumentRevision;
    runtime.InvokeUiAction("editor.hierarchy-drop:player:crate:inside");
    Require(player.ParentId == "crate" && runtime.DocumentRevision == hierarchyRevision + 1 &&
        MatrixNear(runtime.World.WorldMatrix(player), playerWorld) && runtime.IsDirty,
        "Hierarchy center drop must reparent atomically, preserve world transform, and dirty the World");
    runtime.InvokeUiAction("editor.hierarchy-toggle:crate");
    var collapsedHierarchy = runtime.BuildHierarchyMarkup();
    Require(!collapsedHierarchy.Contains("Hierarchy contains unreachable entities",
            StringComparison.Ordinal) &&
        !collapsedHierarchy.Contains("data-action=\"editor.select-entity:player\"",
            StringComparison.Ordinal),
        "Collapsed descendants must stay structurally reachable without being rendered");
    runtime.InvokeUiAction("editor.hierarchy-toggle:crate");
    runtime.InvokeUiAction("editor.undo");
    Require(player.ParentId.Length == 0 && MatrixNear(runtime.World.WorldMatrix(player), playerWorld) &&
        !runtime.IsDirty,
        "Hierarchy reparent must undo back to the saved Authoring state");
    runtime.InvokeUiAction("editor.search-hierarchy:player");
    Require(!runtime.BuildHierarchyMarkup().Contains("Hierarchy contains unreachable entities",
            StringComparison.Ordinal),
        "Hierarchy filtering must not report hidden non-matching entities as unreachable");
    runtime.InvokeUiAction("editor.hierarchy-drop:player:crate:inside");
    Require(player.ParentId.Length == 0,
        "Hierarchy drag must be disabled while its search filter is active");
    runtime.InvokeUiAction("editor.search-hierarchy:");
    runtime.InvokeUiAction("editor.dock-close:console");
    using var hiddenSnapshot = System.Text.Json.JsonDocument.Parse(runtime.DescribeEditorJson());
    Require(hiddenSnapshot.RootElement.GetProperty("layout").GetProperty("hiddenPaneIds")
            .EnumerateArray().Any(value => value.GetString() == "console"),
        "Closing Console must hide the real Dock pane");
    runtime.InvokeUiAction("editor.dock-toggle:console");
    Require(runtime.DescribeEditorJson().Contains("\"hostId\": \"float.console",
            StringComparison.Ordinal),
        "View menu reopen must create a transient floating Dock host");
    runtime.InvokeUiAction("editor.dock-save-layout");
    Require(File.Exists(Path.Combine(temporary.FullName, "Saved", "Config", "EditorLayout.json")),
        "Layout persistence must occur only after the explicit Save Layout command");
    Require(runtime.ViewportMode == EditorViewportMode.Scene3D &&
        runtime.DescribeEditorJson().Contains("\"mode\": \"scene3d\"", StringComparison.Ordinal),
        "Editor should start in serialized 3D scene mode");
    var uiFrame = WorldViewportRenderer.BuildUiFrame(new UiRect(0, 0, 640, 360));
    Require(uiFrame.CameraId == "ui2d" && uiFrame.Triangles.Length == 0 &&
        uiFrame.Lines.Length > 0,
        "UI canvas mode should submit a 2D grid and coordinate axes");
    var uiCameraPosition = runtime.World!.ActiveCamera!.Position;
    runtime.InvokeUiAction("editor.mode-ui");
    Require(runtime.ViewportMode == EditorViewportMode.Ui2D &&
        runtime.DescribeEditorJson().Contains("\"mode\": \"ui2d\"", StringComparison.Ordinal),
        "UI canvas action should update the AI-facing viewport mode");
    runtime.ViewportKey(0x57, true, true);
    runtime.Update(0.1f);
    runtime.ViewportKey(0x57, false, true);
    Require(runtime.World.ActiveCamera!.Position == uiCameraPosition,
        "UI canvas mode must not move the 3D editor camera");
    runtime.InvokeUiAction("editor.mode-scene");
    Require(runtime.ViewportMode == EditorViewportMode.Scene3D,
        "Scene action should restore 3D navigation mode");
    runtime.InvokeUiAction("editor.toggle-invert-y");
    Require(runtime.Settings.InvertYAxis &&
        EngineSettings.Load(root, temporary.FullName).InvertYAxis,
        "Invert Y Axis should persist in the project-local Engine Settings file");
    var initialCameraPosition = runtime.World!.ActiveCamera!.Position;
    runtime.ViewportKey(0x57, true, true);
    runtime.Update(0.1f);
    runtime.ViewportKey(0x57, false, true);
    Require(runtime.World!.ActiveCamera!.Position != initialCameraPosition,
        "Viewport keyboard navigation should move the serialized camera");
    var original = runtime.World!.Entities.First(e => e.Kind == WorldEntityKind.Mesh && !e.Asset.EndsWith("/plane")).Rotation;
    runtime.InvokeUiAction("editor.play");
    runtime.Update(0.1f);
    Require(runtime.PlayState == EditorPlayState.Playing, "Play must enter simulation");
    var playing = runtime.World.Entities.First(e => e.Kind == WorldEntityKind.Mesh && !e.Asset.EndsWith("/plane"));
    Require(playing.Rotation != original, "C# simulation must change the world Transform");
    var pausedRotation = playing.Rotation;
    runtime.InvokeUiAction("editor.pause");
    runtime.Update(0.1f);
    Require(runtime.PlayState == EditorPlayState.Paused && playing.Rotation == pausedRotation,
        "Pause must freeze simulation changes");
    runtime.InvokeUiAction("editor.save");
    Require(WorldDocument.Load(worldPath).Entities.First(e => e.Id == playing.Id).Rotation == original,
        "Saving while playing/paused must not persist simulation changes");
    runtime.InvokeUiAction("editor.play");
    runtime.Update(0.1f);
    Require(playing.Rotation != pausedRotation, "Play from pause must resume the same simulation");
    runtime.InvokeUiAction("editor.stop");
    Require(runtime.PlayState == EditorPlayState.Stopped, "Stop must leave simulation");
    var restored = runtime.World.Entities.First(e => e.Id == playing.Id);
    Require(restored.Rotation == original, "Stop must restore the edit world");
    var editedPosition = new WorldVector3(3, 2, -1);
    restored.Position = editedPosition;
    runtime.InvokeUiAction("editor.save");
    Require(WorldDocument.Load(worldPath).Entities.First(e => e.Id == restored.Id).Position == editedPosition,
        "Save must write edits to the project-owned world file");
    runtime.InvokeUiAction("editor.save-scene-json");
    var snapshotPath = Path.Combine(temporary.FullName, "Saved", "Scenes", "Main.scene.json");
    Require(File.Exists(snapshotPath) && File.ReadAllText(snapshotPath).Contains("worldMatrix", StringComparison.Ordinal),
        "Scene export must contain the AI-facing resolved transform description");
    EditorExtensionValidation.Run(Require);
    runtime.Shutdown();

    var untitledRoot = Directory.CreateDirectory(Path.Combine(temporary.FullName, "UntitledProject"));
    Directory.CreateDirectory(Path.Combine(untitledRoot.FullName, "Content", "Assets"));
    File.WriteAllText(Path.Combine(untitledRoot.FullName, "Content", "Assets", "assets.json"),
        "{\"schemaVersion\":1,\"assets\":[]}");
    var untitledManifest = Path.Combine(untitledRoot.FullName, "Untitled.sharpproject");
    File.WriteAllText(untitledManifest,
        "{\"schemaVersion\":3,\"projectId\":\"untitled\"," +
        "\"engine\":{\"id\":\"SharpEngine\",\"version\":\"0.2.0-dev\",\"association\":\"validation\",\"manifest\":\"engine.json\"},\"modules\":[]," +
        "\"assets\":{\"manifest\":\"Content/Assets/assets.json\"}}");
    var untitledAssets = SharpEngine.Assets.AssetRegistry.Load(root,
        untitledRoot.FullName, untitledManifest);
    var untitled = WorldAssetWorkflow.LoadInitial(root, untitledRoot.FullName,
        untitledManifest, untitledAssets);
    Require(untitled.IsUntitled && untitled.AssetId is null &&
        untitled.Document.Entities.Count == runtime.World.Entities.Count,
        "A project without startup World must deep-copy the engine default as Untitled");
    var savedUntitled = WorldAssetWorkflow.SaveUntitled(untitled.Document,
        untitledRoot.FullName, untitledManifest,
        Path.Combine(untitledRoot.FullName, "Content", "Worlds", "First.sworld"));
    Require(File.Exists(savedUntitled.Path) && savedUntitled.AssetId == "project://worlds/first" &&
        File.ReadAllText(untitledManifest).Contains("Content/Worlds/First.sworld",
            StringComparison.Ordinal) &&
        File.ReadAllText(Path.Combine(untitledRoot.FullName, "Content", "Assets", "assets.json"))
            .Contains("worlds/first", StringComparison.Ordinal),
        "First save must commit .sworld, asset registration, and project startup together");
    var brokenRoot = Directory.CreateDirectory(Path.Combine(temporary.FullName, "BrokenProject"));
    Directory.CreateDirectory(Path.Combine(brokenRoot.FullName, "Content", "Assets"));
    File.WriteAllText(Path.Combine(brokenRoot.FullName, "Content", "Assets", "assets.json"),
        "{\"schemaVersion\":1,\"assets\":[]}");
    var brokenManifest = Path.Combine(brokenRoot.FullName, "Broken.sharpproject");
    File.WriteAllText(brokenManifest,
        "{\"schemaVersion\":3,\"projectId\":\"broken\"," +
        "\"engine\":{\"id\":\"SharpEngine\",\"version\":\"0.2.0-dev\",\"association\":\"validation\",\"manifest\":\"engine.json\"},\"modules\":[]," +
        "\"assets\":{\"manifest\":\"Content/Assets/assets.json\"}," +
        "\"world\":{\"startup\":\"Content/Worlds/Missing.sworld\"}}");
    var missingStartupRejected = false;
    try
    {
        _ = WorldAssetWorkflow.LoadInitial(root, brokenRoot.FullName, brokenManifest,
            SharpEngine.Assets.AssetRegistry.Load(root, brokenRoot.FullName, brokenManifest));
    }
    catch (FileNotFoundException) { missingStartupRejected = true; }
    Require(missingStartupRejected,
        "An explicit missing startup World must fail instead of falling back to a preset");
    Console.WriteLine($"PASS: {assertions} editor integration assertions");
}
finally
{
    Environment.SetEnvironmentVariable("SHARPENGINE_ROOT", previousRoot);
    Environment.SetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST", previousManifest);
    temporary.Delete(true);
}
