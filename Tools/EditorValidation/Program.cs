using SharpEngine.Editor;
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
try
{
    var projectDirectory = Directory.CreateDirectory(Path.Combine(temporary.FullName, "Project"));
    var worlds = Directory.CreateDirectory(Path.Combine(temporary.FullName, "Content", "Worlds"));
    var assets = Directory.CreateDirectory(Path.Combine(temporary.FullName, "Content", "Assets"));
    var worldPath = Path.Combine(worlds.FullName, "Main.world.xml");
    File.Copy(Path.Combine(root, "Content", "Worlds", "Main.world.xml"), worldPath);
    File.WriteAllText(Path.Combine(assets.FullName, "assets.json"), """
        {"schemaVersion":1,"assets":[{"id":"worlds/main","type":"World","path":"Content/Worlds/Main.world.xml"}]}
        """);
    var manifest = Path.Combine(projectDirectory.FullName, "project.json");
    File.WriteAllText(manifest, """
        {"schemaVersion":1,"assets":{"manifest":"Content/Assets/assets.json"},"world":{"startup":"Content/Worlds/Main.world.xml"}}
        """);
    Environment.SetEnvironmentVariable("SHARPENGINE_ROOT", root);
    Environment.SetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST", manifest);
    var runtime = new EditorRuntime(0, 1440, 900, 0);
    runtime.Initialize();
    Require(runtime.World?.SourcePath == worldPath, "Editor must load the project-owned startup world");
    Require(runtime.Assets!.EngineAssets.Any() && runtime.Assets.ProjectAssets.Count() == 1,
        "Public and project assets must both be available to a project editor");
    Require(runtime.Assets.Diagnostics.Count == 0, "Project and engine asset manifests must resolve cleanly");
    Require(runtime.Content is not null && runtime.Content.SourceFilter == SharpEngine.Assets.ContentSourceFilter.Project &&
        runtime.Content.ContentRoot == Path.Combine(temporary.FullName, "Content") &&
        runtime.Content.VisibleEntries.Any(entry => entry.IsDirectory && entry.Name == "Worlds"),
        "Content manager should open the project Content folder and show directories");
    var content = runtime.Content!;
    runtime.InvokeUiAction("editor.content.open:project|Worlds");
    Require(content.CurrentFolder == "Worlds" &&
        content.VisibleEntries.Any(entry => entry.Name == "Main.world.xml"),
        "Content manager should enter a Content subfolder and browse files");
    runtime.InvokeUiAction("editor.content.type:World");
    Require(content.VisibleAssets.Count == 1 && content.VisibleAssets[0].Type == "World",
        "Content manager type filter should narrow the visible assets");
    runtime.InvokeUiAction("editor.content.open:project|Worlds/Main.world.xml");
    Require(content.SelectedId == "project://worlds/main" &&
        content.SelectedPath == "Worlds/Main.world.xml",
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
    Console.WriteLine($"PASS: {assertions} editor integration assertions");
}
finally
{
    Environment.SetEnvironmentVariable("SHARPENGINE_ROOT", previousRoot);
    Environment.SetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST", previousManifest);
    temporary.Delete(true);
}
