using SharpEngine.Settings;
using SharpEngine.Editor;
using SharpEngine.Editor.Layout;
using SharpEngine.UI;
using SharpEngine.World;
using System.Text.Json.Nodes;

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
    DevelopmentSettingsValidation.Run(temporary.FullName, Require);
    McpConfigurationValidation.Run(temporary.FullName, Require);
    var worlds = Directory.CreateDirectory(Path.Combine(temporary.FullName, "Content", "Worlds"));
    var assets = Directory.CreateDirectory(Path.Combine(temporary.FullName, "Content", "Assets"));
    var ui = Directory.CreateDirectory(Path.Combine(temporary.FullName, "Content", "UI"));
    var materialDirectory = Directory.CreateDirectory(Path.Combine(temporary.FullName,
        "Content", "Assets", "Materials"));
    var meshDirectory = Directory.CreateDirectory(Path.Combine(temporary.FullName,
        "Content", "Assets", "Meshes"));
    var dataDirectory = Directory.CreateDirectory(Path.Combine(temporary.FullName,
        "Content", "Assets", "Data"));
    var inputActionDirectory = Directory.CreateDirectory(Path.Combine(temporary.FullName,
        "Content", "Input", "Actions"));
    var inputContextDirectory = Directory.CreateDirectory(Path.Combine(temporary.FullName,
        "Content", "Input", "Contexts"));
    var pluginRoot = Directory.CreateDirectory(Path.Combine(temporary.FullName,
        "Plugins", "ValidationPlugin"));
    var pluginAssets = Directory.CreateDirectory(Path.Combine(pluginRoot.FullName,
        "Content", "Assets"));
    var pluginData = Directory.CreateDirectory(Path.Combine(pluginRoot.FullName,
        "Content", "PluginData"));
    var pluginIcons = Directory.CreateDirectory(Path.Combine(pluginRoot.FullName,
        "Resources", "Editor", "Icons"));
    File.WriteAllBytes(Path.Combine(pluginIcons.FullName, "validation.tga"), [0]);
    File.WriteAllText(Path.Combine(pluginData.FullName, "Sample.pluginasset.json"),
        "{\"schemaVersion\":1,\"value\":\"validation\"}\n");
    File.WriteAllText(Path.Combine(pluginAssets.FullName, "assets.json"), """
        {"schemaVersion":1,"assets":[
          {"id":"sample","type":"PluginData","path":"Content/PluginData/Sample.pluginasset.json"}
        ]}
        """);
    File.WriteAllText(Path.Combine(pluginRoot.FullName, "ValidationPlugin.sharpplugin"), """
        {"schemaVersion":1,"id":"validation.plugin","version":"1.0.0",
         "modules":[],"assets":{"manifest":"Content/Assets/assets.json"}}
        """);
    var worldPath = Path.Combine(worlds.FullName, "Main.sworld");
    File.Copy(Path.Combine(root, "Content", "Worlds", "Main.sworld"), worldPath);
    var uiPath = Path.Combine(ui.FullName, "Main.rml");
    File.WriteAllText(uiPath, """
        <rml data-sharpengine-schema-version="1" data-view-model="validation.main@1">
        <head><link type="text/rcss" href="Main.rcss" /></head>
        <body><div id="screen" class="screen"><span id="headline" class="headline" data-bind="screen.title">Validation UI</span><button id="continue" data-command="screen.continue">Continue</button></div></body>
        </rml>
        """);
    File.WriteAllText(Path.Combine(ui.FullName, "Main.rcss"),
        ".screen { display: flex; flex-direction: column; } .headline { color: #ffffff; }");
    var materialPath = Path.Combine(materialDirectory.FullName, "Validation.material.json");
    File.WriteAllText(materialPath, """
        {"schemaVersion":1,"name":"Validation Material","shader":"Standard","baseColor":[0.2,0.4,0.7,1],"metallic":0.1,"roughness":0.6}
        """);
    var meshPath = Path.Combine(meshDirectory.FullName, "Validation.mesh.json");
    File.WriteAllText(meshPath, """
        {"schemaVersion":1,"name":"Validation Mesh","primitive":"Triangle","coordinateSystem":"Y-up","defaultMaterial":"project://materials/validation","positions":[[-0.5,0,0],[0.5,0,0],[0,1,0]],"indices":[0,1,2]}
        """);
    var inputActionPath = Path.Combine(inputActionDirectory.FullName,
        "ValidationMove.inputaction.json");
    File.WriteAllText(inputActionPath, """
        {"schemaVersion":1,"actionId":"project://input/actions/validation-move","valueType":"Axis2D","continuous":true}
        """);
    var inputContextPath = Path.Combine(inputContextDirectory.FullName,
        "Validation.inputmapping.json");
    File.WriteAllText(inputContextPath, """
        {"schemaVersion":1,"contextId":"project://input/contexts/validation","route":"Gameplay","priority":0,"activateByDefault":false,"bindings":[{"action":"project://input/actions/validation-move","control":"Keyboard/W","scaleX":0,"scaleY":1,"modifiers":[],"exactModifiers":false}]}
        """);
    var dataAssetPath = Path.Combine(dataDirectory.FullName, "ReloadRifle.sdataasset");
    File.WriteAllText(dataAssetPath, """
        {"schemaVersion":1,"kind":"Validation.Reload.Rifle","assetId":"project://data/reload-rifle","name":"Reload Rifle","damage":35,"magazineSize":30}
        """);
    File.WriteAllText(Path.Combine(assets.FullName, "assets.json"), """
        {"schemaVersion":1,"assets":[
          {"id":"worlds/main","type":"World","path":"Content/Worlds/Main.sworld"},
          {"id":"ui/main","type":"RmlDocument","path":"Content/UI/Main.rml"},
          {"id":"ui/main-style","type":"StyleSheet","path":"Content/UI/Main.rcss"},
          {"id":"materials/validation","type":"Material","path":"Content/Assets/Materials/Validation.material.json"},
          {"id":"meshes/validation","type":"Mesh","path":"Content/Assets/Meshes/Validation.mesh.json"},
          {"id":"input/actions/validation-move","type":"InputAction","path":"Content/Input/Actions/ValidationMove.inputaction.json"},
          {"id":"input/contexts/validation","type":"InputMappingContext","path":"Content/Input/Contexts/Validation.inputmapping.json"},
          {"id":"data/reload-rifle","type":"Validation.Reload.Rifle","path":"Content/Assets/Data/ReloadRifle.sdataasset"}
        ]}
        """);
    var manifest = Path.Combine(temporary.FullName, "Validation.sharpproject");
    File.WriteAllText(manifest, """
        {"schemaVersion":3,"projectId":"validation","engine":{"id":"SharpEngine","version":"0.2.0-dev","association":"validation","manifest":"engine.json"},"modules":[],"plugins":["project://Plugins/ValidationPlugin/ValidationPlugin.sharpplugin"],"assets":{"manifest":"Content/Assets/assets.json"},"world":{"startup":"Content/Worlds/Main.sworld"}}
        """);
    Environment.SetEnvironmentVariable("SHARPENGINE_ROOT", root);
    Environment.SetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST", manifest);
    var runtime = new EditorRuntime(0, 1440, 900, 0);
    runtime.Initialize();
    DataAssetModuleReloadValidation.Run(runtime, Require);
    if (args.Contains("--data-assets-only", StringComparer.Ordinal))
    {
        runtime.Shutdown();
        Console.WriteLine($"PASS: {assertions} focused data-asset and prerequisite assertions");
        return;
    }
    var logPath = Path.Combine(temporary.FullName, "Saved", "Logs", "Engine.log");
    using (var logStream = new FileStream(logPath, FileMode.Append, FileAccess.Write,
        FileShare.ReadWrite | FileShare.Delete))
    using (var logWriter = new StreamWriter(logStream, new System.Text.UTF8Encoding(false)))
        logWriter.WriteLine(
            "{\"schemaVersion\":1,\"timestamp\":\"2026-10-18T00:00:00.000Z\",\"level\":\"warning\",\"source\":\"Editor\",\"category\":\"RmlUi\",\"message\":\"native validation record\",\"processId\":1,\"threadId\":1,\"exception\":null,\"properties\":{}}");
    runtime.Update(0);
    using var loggingSnapshot = System.Text.Json.JsonDocument.Parse(runtime.DescribeEditorJson());
    Require(loggingSnapshot.RootElement.GetProperty("logging").GetProperty("recentEvents")
            .EnumerateArray().Any(value => value.GetProperty("category").GetString() == "RmlUi" &&
                value.GetProperty("message").GetString() == "native validation record"),
        "Logs pane history must include records appended by the native unified log sink");
    runtime.RegisterProjectSettingsAssembly(typeof(ValidationProjectSettings).Assembly);
    using var uiContract = runtime.UiRuntime.RegisterViewModel<ValidationMainScreen>(
        "validation.main", 1);
    Require(runtime.UiRuntime.ViewModelContracts.Single().JsonSchema.Contains(
            "\"screen\"", StringComparison.Ordinal) &&
        runtime.UiRuntime.ViewModelContracts.Single().JsonSchema.Contains(
            "\"title\"", StringComparison.Ordinal),
        "Runtime Module ViewModel registration must publish a generated JSON Schema");
    var defaultLayout = new EditorLayoutStore().LoadDefault(root);
    var dockMarkup = EditorDockMarkup.Build(defaultLayout.MainHost, "Main Scene");
    Require(dockMarkup.Contains("data-dock-split-node=\"main.root\"", StringComparison.Ordinal) &&
        dockMarkup.Contains("data-dock-split-part=\"first\"", StringComparison.Ordinal) &&
        dockMarkup.Contains("data-dock-split-part=\"second\"", StringComparison.Ordinal) &&
        dockMarkup.Contains("data-dock-split-ratio=\"0.76\"", StringComparison.Ordinal) &&
        !dockMarkup.Contains("data-pane-id=\"ui-editor\"", StringComparison.Ordinal) &&
        !dockMarkup.Contains("data-pane-id=\"timeline\"", StringComparison.Ordinal) &&
        !dockMarkup.Contains("UI Canvas", StringComparison.Ordinal),
        "Main Dock markup must expose recursive splits without asset-editor panes");
    Require(dockMarkup.Contains("Content Manager 1", StringComparison.Ordinal) &&
        dockMarkup.Contains("id=\"content-search\"", StringComparison.Ordinal) &&
        dockMarkup.Contains("editor.content.view:content:tile", StringComparison.Ordinal) &&
        dockMarkup.Contains("editor.content.view:content:list", StringComparison.Ordinal),
        "Content Manager markup must expose instance-scoped search and Tile/List controls");
    Require(dockMarkup.Contains("data-pane-id=\"console\"", StringComparison.Ordinal) &&
        dockMarkup.Contains("data-pane-id=\"logs\"", StringComparison.Ordinal) &&
        dockMarkup.Contains("id=\"logs-main\"", StringComparison.Ordinal) &&
        dockMarkup.Contains("Unified Log", StringComparison.Ordinal),
        "Console and Logs must be separate dock panes with distinct markup");
    Require(runtime.CustomBars.RegisteredSurfaces.Any(surface =>
            surface.Id == EditorCustomBarRegistry.ViewportSurfaceId) &&
        runtime.CustomBars.BuildMarkup(EditorCustomBarRegistry.ViewportSurfaceId)
            .Contains("core.viewport-move", StringComparison.Ordinal),
        "Viewport CustomBar must be an engine-created surface with built-in transform commands");
    Require(runtime.Commands.Commands.Any(command => command.Id == "view.console") &&
        runtime.Commands.Commands.Any(command => command.Id == "view.logs") &&
        runtime.Menus.Items.Any(item => item.CommandId == "view.console") &&
        runtime.Menus.Items.Any(item => item.CommandId == "view.logs"),
        "View menu must expose independent Console and Logs commands");
    Require(runtime.World?.SourcePath == worldPath, "Editor must load the project-owned startup world");
    Require(runtime.Assets!.EngineAssets.Any() && runtime.Assets.ProjectAssets.Count() == 9,
        "Public, project, and Plugin Content assets must both be available to a project editor");
    var pluginAsset = runtime.Assets.Find("project://plugins/validation.plugin/sample");
    Require(pluginAsset?.OwnerId == "validation.plugin" &&
        pluginAsset.RelativePath == "Plugins/ValidationPlugin/Content/PluginData/Sample.pluginasset.json" &&
        runtime.Assets.ResolvePath(pluginAsset) == Path.Combine(pluginRoot.FullName,
            "Content", "PluginData", "Sample.pluginasset.json"),
        "Plugin Content must be namespaced, rooted at its Plugin, and resolvable");
    Require(runtime.Assets.Find("engine://worlds/main")?.ReadOnly == false &&
        runtime.Assets.Find("engine://meshes/cube")?.ReadOnly == true,
        "Engine Worlds must remain ordinary editable assets without changing other asset policies");
    Require(runtime.Assets.Diagnostics.Count == 0, "Project and engine asset manifests must resolve cleanly");
    Require(runtime.Content is not null && runtime.Content.SourceFilter == SharpEngine.Assets.ContentSourceFilter.Project &&
        runtime.Content.ContentRoot == Path.Combine(temporary.FullName, "Content") &&
        runtime.Content.VisibleEntries.Any(entry => entry.IsDirectory && entry.Name == "Worlds"),
        "Content manager should open the project Content folder and show directories");
    var content = runtime.Content!;
    var hasPluginFolder = content.VisibleEntries.Any(entry => entry.IsDirectory && entry.Name == "Plugins");
    runtime.InvokeUiAction("editor.content.open:project|Plugins/ValidationPlugin/Content/PluginData");
    var pluginFolderOpened = content.CurrentFolder == "Plugins/ValidationPlugin/Content/PluginData";
    Require(hasPluginFolder &&
        pluginFolderOpened &&
        content.VisibleEntries.Any(entry => entry.Name == "Sample.pluginasset.json" &&
            entry.AssetId == "project://plugins/validation.plugin/sample"),
        "Content manager must browse declared Plugin Content through the project source");
    content.ResetFolder();
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
    runtime.InvokeUiAction("editor.content.view:content-2:list");
    runtime.InvokeUiAction("editor.content.source:content-2:project");
    runtime.InvokeUiAction("editor.content.open:content-2:project|Worlds/Main.sworld");
    var content2 = runtime.ContentManagers!.Get(EditorPaneIds.Content2);
    Require(runtime.ContentManagers.ActivePaneId == EditorPaneIds.Content2 &&
        content2.ViewMode == SharpEngine.Assets.ContentViewMode.List &&
        content2.SelectedId == "project://worlds/main" &&
        content.SelectedId == "engine://meshes/cube" &&
        runtime.Selection.Id == "project://worlds/main",
        "Content managers must keep independent view and selection state while the active instance owns editor selection");
    runtime.InvokeUiAction("editor.content.activate:content");
    Require(runtime.Selection.Id == "engine://meshes/cube" &&
        runtime.ContentManagers.ActivePaneId == EditorPaneIds.Content,
        "Activating a content manager must restore that instance's selection");
    runtime.InvokeUiAction("editor.context:10:10:folder:project:Worlds");
    Require(runtime.Selection.Kind == EditorSelectionKind.Folder &&
        runtime.SelectedFolderPath == "Worlds" && runtime.SelectedEntity is null,
        "Folder context selection must replace selection from every other editor surface");
    runtime.InvokeUiAction("editor.content.clear");
    if (args.Contains("--content-only", StringComparer.Ordinal))
    {
        ContentCreationValidation.Run(runtime, temporary.FullName, Require);
        ContentManagerValidation.Run(runtime, temporary.FullName, Require);
        ContentManagerMenuValidation.Run(Require);
        runtime.Shutdown();
        Console.WriteLine($"PASS: {assertions} focused Content Manager assertions");
        return;
    }
    Require(!DevelopmentSettings.Get<EngineSettings>().InvertYAxis,
        "Engine Settings should auto-register and support static runtime reads");
    Require(DevelopmentSettings.Get<ValidationProjectSettings>().MoveSpeed == 600 &&
        DevelopmentSettings.Get<ValidationProjectSettings>().Profile.Title == "Default" &&
        DevelopmentSettings.Get<ValidationProjectSettings>().Tags.SequenceEqual(["starter"]),
        "Project Settings should auto-register nested defaults from the project assembly");
    Require(runtime.BuildSettingsMarkup(SettingsScope.Engine).Contains(
            "sharpengine.editor-navigation", StringComparison.Ordinal) &&
        runtime.BuildSettingsMarkup(SettingsScope.Project).Contains(
            "validation.gameplay", StringComparison.Ordinal),
        "Engine and Project Settings panels must filter the shared registry by scope");
    var detachedSnapshot = DevelopmentSettings.Get<ValidationProjectSettings>();
    detachedSnapshot.Tags.Add("caller-mutation");
    Require(DevelopmentSettings.Get<ValidationProjectSettings>().Tags.SequenceEqual(["starter"]),
        "Static settings reads must return detached snapshots instead of mutable authority");
    Require(runtime.Commands.Commands.Any(command => command.Id == "core.engine-settings") &&
        runtime.Commands.Commands.Any(command => command.Id == "core.project-settings") &&
        runtime.Menus.Items.Any(item => item.MenuId == "file" &&
            item.CommandId == "core.engine-settings") &&
        runtime.Menus.Items.Any(item => item.MenuId == "file" &&
            item.CommandId == "core.project-settings"),
        "File must expose separate Engine Settings and Project Settings commands");
    var engineSettingsPath = Path.Combine(temporary.FullName, "Config", "EngineSettings",
        "sharpengine.editor-navigation.json");
    runtime.InvokeUiAction(
        "editor.settings-change:engine:sharpengine.editor-navigation:invertYAxis:true");
    Require(DevelopmentSettings.Get<EngineSettings>().InvertYAxis &&
        !File.Exists(engineSettingsPath),
        "Settings edits should publish a new runtime snapshot without saving implicitly");
    runtime.InvokeUiAction(
        "editor.settings-save:engine:sharpengine.editor-navigation");
    Require(File.Exists(engineSettingsPath) &&
        File.ReadAllText(engineSettingsPath).Contains("\"invertYAxis\": true",
            StringComparison.Ordinal),
        "Engine Settings overrides should save explicitly under the project Config folder");
    runtime.InvokeUiAction(
        "editor.settings-change:project:validation.gameplay:profile/title:Configured");
    Require(DevelopmentSettings.Get<ValidationProjectSettings>().Profile.Title == "Configured",
        "Nested Project Settings edits must update static runtime reads atomically");
    ShortcutValidation.Run(runtime, Require);
    if (args.Contains("--settings-only", StringComparer.Ordinal))
    {
        runtime.Shutdown();
        Console.WriteLine($"PASS: {assertions} focused settings assertions");
        return;
    }
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
        ["validation.raise-status"], 10, icon:
            "sharpengine-resource://project/Plugins/ValidationPlugin/Resources/Editor/Icons/validation.tga"));
    var extensionSnapshot = runtime.DescribeEditorJson();
    Require(extensionSnapshot.Contains("validation.raise-status", StringComparison.Ordinal) &&
        extensionSnapshot.Contains("Raise Status", StringComparison.Ordinal) &&
        extensionSnapshot.Contains(
            "sharpengine-resource://project/Plugins/ValidationPlugin/Resources/Editor/Icons/validation.tga",
            StringComparison.Ordinal),
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
    runtime.InvokeUiAction("editor.dock-close:logs");
    using var logsHiddenSnapshot = System.Text.Json.JsonDocument.Parse(runtime.DescribeEditorJson());
    Require(logsHiddenSnapshot.RootElement.GetProperty("layout").GetProperty("hiddenPaneIds")
            .EnumerateArray().Any(value => value.GetString() == "logs") &&
        !logsHiddenSnapshot.RootElement.GetProperty("layout").GetProperty("hiddenPaneIds")
            .EnumerateArray().Any(value => value.GetString() == "console"),
        "Closing Logs must hide Logs while keeping Console visible");
    runtime.InvokeUiAction("editor.dock-toggle:logs");
    Require(runtime.DescribeEditorJson().Contains("\"hostId\": \"float.logs",
            StringComparison.Ordinal),
        "View menu reopen must create a distinct floating Logs host");
    runtime.InvokeUiAction("editor.dock-close:logs");
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
    var savedLayoutPath = Path.Combine(temporary.FullName, "Saved", "Config",
        "EditorLayout.json");
    Require(File.Exists(savedLayoutPath) &&
        File.ReadAllText(savedLayoutPath).Contains("\"paneId\": \"content-2\"",
            StringComparison.Ordinal) &&
        File.ReadAllText(savedLayoutPath).Contains("\"viewMode\": \"list\"",
            StringComparison.Ordinal),
        "Layout persistence must occur only after the explicit Save Layout command");
    Require(runtime.DescribeEditorJson().Contains("\"mode\": \"scene3d\"",
            StringComparison.Ordinal),
        "The viewport must remain a dedicated 3D scene surface");
    runtime.InvokeUiAction("editor.select-asset:project://ui/main");
    Require(!runtime.DescribeEditorJson().Contains("\"assetId\": \"project://ui/main\"",
            StringComparison.Ordinal),
        "Selecting an RML asset must only select it, without opening an editor tool");
    runtime.InvokeUiAction("editor.open-asset:project://ui/main");
    string AssetEditorRoot(string AssetId)
    {
        using var Snapshot = System.Text.Json.JsonDocument.Parse(runtime.DescribeEditorJson());
        return Snapshot.RootElement.GetProperty("assetTools").GetProperty("editors")
            .EnumerateArray().Single(Editor =>
                Editor.GetProperty("assetId").GetString() == AssetId)
            .GetProperty("rootId").GetString()!;
    }
    bool AssetEditorDirty(string AssetId)
    {
        using var Snapshot = System.Text.Json.JsonDocument.Parse(runtime.DescribeEditorJson());
        return Snapshot.RootElement.GetProperty("assetTools").GetProperty("editors")
            .EnumerateArray().Single(Editor =>
                Editor.GetProperty("assetId").GetString() == AssetId)
            .GetProperty("dirty").GetBoolean();
    }
    var ProjectUiRoot = AssetEditorRoot("project://ui/main");
    Require(runtime.DescribeEditorJson().Contains("validation.main@1", StringComparison.Ordinal) &&
        runtime.DescribeEditorJson().Contains("\"viewModelContractAvailable\": true",
            StringComparison.Ordinal),
        "Opening an RML asset must create an independent UI editor root");
    runtime.OpenAsset("engine://ui/editor");
    var EngineUiRoot = AssetEditorRoot("engine://ui/editor");
    Require(ProjectUiRoot != EngineUiRoot,
        "Different RML assets must own different top-level editor roots");
    var ReusedUi = runtime.OpenAsset("project://ui/main");
    Require(ReusedUi.Success && ReusedUi.Code == "ASSET_EDITOR_REUSED" &&
        AssetEditorRoot("project://ui/main") == ProjectUiRoot,
        "Opening the same RML asset must focus and reuse its editor root");
    runtime.InvokeUiAction($"editor.host-action:{ProjectUiRoot}:editor.ui-select:headline");
    runtime.InvokeUiAction($"editor.host-action:{ProjectUiRoot}:editor.ui-change:text:Edited through UI Editor");
    Require(AssetEditorDirty("project://ui/main"),
        "UI property changes must dirty only the open UI document");
    runtime.InvokeUiAction($"editor.host-action:{ProjectUiRoot}:editor.asset-command:undo");
    Require(!AssetEditorDirty("project://ui/main"),
        "UI edits must participate in document-local Undo");
    runtime.InvokeUiAction($"editor.host-action:{ProjectUiRoot}:editor.ui-change:text:Saved UI headline");
    runtime.InvokeUiAction($"editor.host-action:{ProjectUiRoot}:editor.asset-save");
    Require(File.ReadAllText(uiPath).Contains("Saved UI headline", StringComparison.Ordinal) &&
        !AssetEditorDirty("project://ui/main"),
        "UI Save must validate and atomically replace the authoritative RML source");

    foreach (var AssetId in new[]
    {
        "engine://meshes/cube",
        "engine://meshes/robot-static",
        "engine://meshes/uefn-mannequin",
        "engine://skeletons/uefn-mannequin",
        "engine://materials/default",
        "engine://ui/editor-style",
        "engine://input/actions/demo.move",
        "engine://input/contexts/demo-gameplay",
    })
    {
        var Opened = runtime.OpenAsset(AssetId);
        Require(Opened.Success && !AssetEditorDirty(AssetId),
            $"Engine asset editor must open read-only asset {AssetId}");
    }

    var ProjectMeshOpen = runtime.OpenAsset("project://meshes/validation");
    var ProjectMeshRoot = AssetEditorRoot("project://meshes/validation");
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectMeshRoot}:editor.mesh-default-material:engine://materials/default");
    Require(ProjectMeshOpen.Success && AssetEditorDirty("project://meshes/validation"),
        "Mesh editor must author only the project mesh default material");
    runtime.InvokeUiAction($"editor.host-action:{ProjectMeshRoot}:editor.asset-save");
    Require(File.ReadAllText(meshPath).Contains("engine://materials/default",
            StringComparison.Ordinal) && !AssetEditorDirty("project://meshes/validation"),
        "Mesh editor Save must validate and atomically persist defaultMaterial");

    var ProjectMaterialOpen = runtime.OpenAsset("project://materials/validation");
    var ProjectMaterialRoot = AssetEditorRoot("project://materials/validation");
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectMaterialRoot}:editor.material-field:metallic:0.45");
    Require(ProjectMaterialOpen.Success && AssetEditorDirty("project://materials/validation"),
        "Material editor must author structured shader parameters");
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectMaterialRoot}:editor.asset-command:undo");
    Require(!AssetEditorDirty("project://materials/validation"),
        "Material editor Undo must restore its savepoint");
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectMaterialRoot}:editor.asset-command:redo");
    runtime.InvokeUiAction($"editor.host-action:{ProjectMaterialRoot}:editor.asset-save");
    Require(File.ReadAllText(materialPath).Contains("0.45", StringComparison.Ordinal) &&
        !AssetEditorDirty("project://materials/validation"),
        "Material editor Redo and Save must persist the validated value");
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectMaterialRoot}:editor.material-field:metallic:2");
    Require(!AssetEditorDirty("project://materials/validation"),
        "Material editor must reject out-of-range parameters without partial mutation");
    var SavedMaterialText = File.ReadAllText(materialPath);
    File.WriteAllText(materialPath, SavedMaterialText + Environment.NewLine);
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectMaterialRoot}:editor.material-field:roughness:0.55");
    runtime.InvokeUiAction($"editor.host-action:{ProjectMaterialRoot}:editor.asset-save");
    Require(AssetEditorDirty("project://materials/validation") &&
        File.ReadAllText(materialPath) == SavedMaterialText + Environment.NewLine,
        "Asset editor Save must reject an external source change without overwriting it");
    File.WriteAllText(materialPath, SavedMaterialText);
    runtime.InvokeUiAction($"editor.host-action:{ProjectMaterialRoot}:editor.asset-save");
    Require(!AssetEditorDirty("project://materials/validation"),
        "Asset editor Save must succeed after the external conflict is resolved");

    var ProjectStyleOpen = runtime.OpenAsset("project://ui/main-style");
    var ProjectStyleRoot = AssetEditorRoot("project://ui/main-style");
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectStyleRoot}:editor.asset-command:add-line");
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectStyleRoot}:editor.stylesheet-line:1:.probe {{ color: #123456; }}");
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectStyleRoot}:editor.stylesheet-line-remove:1");
    Require(!AssetEditorDirty("project://ui/main-style"),
        "StyleSheet editor add and remove line actions must restore the savepoint exactly");
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectStyleRoot}:editor.stylesheet-line:0:.screen {{ color: #abcdef; }}");
    Require(ProjectStyleOpen.Success && AssetEditorDirty("project://ui/main-style"),
        "StyleSheet editor must retain an editable RCSS text document");
    runtime.InvokeUiAction($"editor.host-action:{ProjectStyleRoot}:editor.asset-save");
    Require(File.ReadAllText(Path.Combine(ui.FullName, "Main.rcss")).Contains("#abcdef",
            StringComparison.Ordinal) && !AssetEditorDirty("project://ui/main-style"),
        "StyleSheet editor Save must persist a syntax-valid RCSS source");

    var ProjectActionOpen = runtime.OpenAsset(
        "project://input/actions/validation-move");
    var ProjectActionRoot = AssetEditorRoot(
        "project://input/actions/validation-move");
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectActionRoot}:editor.input-field:valueType:Axis1D");
    runtime.InvokeUiAction($"editor.host-action:{ProjectActionRoot}:editor.asset-save");
    Require(ProjectActionOpen.Success && File.ReadAllText(inputActionPath)
            .Contains("Axis1D", StringComparison.Ordinal),
        "InputAction editor must validate and save its structured fields");

    var ProjectContextOpen = runtime.OpenAsset("project://input/contexts/validation");
    var ProjectContextRoot = AssetEditorRoot("project://input/contexts/validation");
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectContextRoot}:editor.input-field:priority:5");
    runtime.InvokeUiAction(
        $"editor.host-action:{ProjectContextRoot}:editor.asset-command:add-binding");
    runtime.InvokeUiAction($"editor.host-action:{ProjectContextRoot}:editor.asset-save");
    Require(ProjectContextOpen.Success && File.ReadAllText(inputContextPath)
            .Contains("\"priority\": 5", StringComparison.Ordinal) &&
        JsonNode.Parse(File.ReadAllText(inputContextPath))!["bindings"]!.AsArray().Count == 2,
        "InputMappingContext editor must edit fields and add validated bindings");

    var DataAssetOpen = runtime.OpenAsset("project://data/reload-rifle");
    var DataAssetRoot = AssetEditorRoot("project://data/reload-rifle");
    runtime.InvokeUiAction(
        $"editor.host-action:{DataAssetRoot}:editor.data-asset-field:damage:42");
    runtime.InvokeUiAction(
        $"editor.host-action:{DataAssetRoot}:editor.data-asset-field:magazineSize:12");
    runtime.InvokeUiAction($"editor.host-action:{DataAssetRoot}:editor.asset-save");
    var SavedDataAsset = JsonNode.Parse(File.ReadAllText(dataAssetPath))!;
    Require(DataAssetOpen.Success && SavedDataAsset["damage"]!.GetValue<double>() == 42 &&
        SavedDataAsset["magazineSize"]!.GetValue<int>() == 12,
        "Generic DataAsset reflection editor must validate and save inherited and local properties");
    runtime.InvokeUiAction(
        $"editor.host-action:{DataAssetRoot}:editor.data-asset-field:damage:-1");
    Require(!AssetEditorDirty("project://data/reload-rifle"),
        "Generic DataAsset editor must enforce reflected range constraints atomically");

    var initialCameraPosition = runtime.World!.ActiveCamera!.Position;
    runtime.ViewportKey(0x57, true, true);
    runtime.Update(0.1f);
    runtime.ViewportKey(0x57, false, true);
    Require(runtime.World!.ActiveCamera!.Position != initialCameraPosition,
        "Viewport keyboard navigation should move the serialized camera");
    const string RuntimeTransformEntityId = "crate";
    var original = runtime.World!.Entities.Single(Entity => Entity.Id == RuntimeTransformEntityId).Rotation;
    runtime.InvokeUiAction("editor.play");
    var liveTransform = runtime.RuntimeWorld!.FindEntity(RuntimeTransformEntityId)!.GetComponent<SharpEngine.Components.TransformComponent>()!;
    liveTransform.Rotation += new System.Numerics.Vector3(0, 15, 0);
    runtime.Update(0.1f);
    Require(runtime.PlayState == EditorPlayState.Playing, "Play must enter simulation");
    var playing = runtime.World.Entities.Single(Entity => Entity.Id == RuntimeTransformEntityId);
    Require(playing.Rotation != original, "Runtime component changes must drive the viewport world independently");
    var PlayingCameraPosition = runtime.World.ActiveCamera!.Position;
    runtime.ViewportKey(0x57, true, true);
    runtime.Update(0.1f);
    runtime.ViewportKey(0x57, false, true);
    Require(runtime.World.ActiveCamera.Position == PlayingCameraPosition,
        "Playing must route viewport keyboard input to gameplay without moving the editor camera");
    var pausedRotation = playing.Rotation;
    runtime.InvokeUiAction("editor.pause");
    runtime.Update(0.1f);
    Require(runtime.PlayState == EditorPlayState.Paused && playing.Rotation == pausedRotation,
        "Pause must freeze simulation changes");
    runtime.InvokeUiAction("editor.save");
    Require(WorldDocument.Load(worldPath).Entities.First(e => e.Id == playing.Id).Rotation == original,
        "Saving while playing/paused must not persist simulation changes");
    runtime.InvokeUiAction("editor.play");
    liveTransform.Rotation += new System.Numerics.Vector3(0, 15, 0);
    runtime.Update(0.1f);
    Require(playing.Rotation != pausedRotation, "Play from pause must resume the same simulation");
    runtime.InvokeUiAction("editor.stop");
    Require(runtime.PlayState == EditorPlayState.Stopped, "Stop must leave simulation");
    var restored = runtime.World.Entities.First(e => e.Id == playing.Id);
    Require(restored.Rotation == original, "Stop must restore the edit world");
    var editedPosition = new WorldVector3(3, 2, -1);
    runtime.EditWorld("validation.position", runtime.DocumentRevision, Candidate =>
    {
        var View = new WorldDocument(Candidate, runtime.World.Registry);
        View.Entities.Single(Entity => Entity.Id == restored.Id).Position = editedPosition;
    }, [restored.Id]);
    runtime.InvokeUiAction("editor.save");
    Require(WorldDocument.Load(worldPath).Entities.First(e => e.Id == restored.Id).Position == editedPosition,
        "Save must write edits to the project-owned world file");
    runtime.InvokeUiAction("editor.save-scene-json");
    var snapshotPath = Path.Combine(temporary.FullName, "Saved", "Scenes", "Main.scene.json");
    Require(File.Exists(snapshotPath) && File.ReadAllText(snapshotPath).Contains("worldMatrix", StringComparison.Ordinal),
        "Scene export must contain the AI-facing resolved transform description");
    const string PrefabSourceRootId = "player";
    runtime.InvokeUiAction("editor.select-entity:" + PrefabSourceRootId);
    var MainEntityIdsBeforePrefab = runtime.World.Entities.Select(Entity => Entity.Id).ToHashSet(StringComparer.Ordinal);
    var MainEntityCountBeforePrefab = MainEntityIdsBeforePrefab.Count;
    var MainWorldBeforePrefab = runtime.World;
    var MainStateBeforePrefab = runtime.World.Serialize();
    var PrefabSourceEntityIds = runtime.World.Data.Subtree(PrefabSourceRootId)
        .Select(Entity => Entity.EntityId).ToHashSet(StringComparer.Ordinal);
    runtime.InvokeUiAction("editor.prefab-export");
    var ExportedPrefabAsset = runtime.Assets.ProjectAssets.Single(Asset =>
        string.Equals(Asset.Type, "Prefab", StringComparison.OrdinalIgnoreCase));
    var PrefabPath = runtime.Assets.ResolvePath(ExportedPrefabAsset)!;
    var ExportedPrefab = WorldDocument.Load(PrefabPath, runtime.Components);
    var ExportedPrefabRoot = ExportedPrefab.Data.Entities.Single(Entity => Entity.ParentId is null);
    Require(ExportedPrefab.Data.IsPrefab && ExportedPrefab.Data.Entities.Count > 1 &&
        PrefabSourceEntityIds.SetEquals(ExportedPrefab.Data.Entities.Select(Entity => Entity.EntityId)) &&
        ExportedPrefabRoot.EntityId == PrefabSourceRootId,
        "Prefab export preserves the complete selected multi-entity subtree and its stable root");
    var PrefabRoot = AssetEditorRoot(ExportedPrefabAsset.Id);
    Require(ReferenceEquals(MainWorldBeforePrefab, runtime.World) &&
        runtime.World.Serialize() == MainStateBeforePrefab,
        "Export opens an independent Prefab editor without replacing the main World");
    runtime.InvokeUiAction(
        $"editor.host-action:{PrefabRoot}:editor.asset-command:add-entity");
    Require(AssetEditorDirty(ExportedPrefabAsset.Id) &&
        runtime.World.Serialize() == MainStateBeforePrefab,
        "Prefab edits are isolated from the main World data and history");
    runtime.InvokeUiAction($"editor.host-action:{PrefabRoot}:editor.asset-command:undo");
    Require(!AssetEditorDirty(ExportedPrefabAsset.Id),
        "Prefab Undo uses its own document-local history");
    runtime.InvokeUiAction($"editor.prefab-instantiate:{ExportedPrefabAsset.Id}|");
    var InstantiatedRootId = runtime.SelectedEntityId!;
    var InstantiatedEntities = runtime.World.Data.Entities
        .Where(Entity => !MainEntityIdsBeforePrefab.Contains(Entity.EntityId)).ToArray();
    var InstantiatedRoot = InstantiatedEntities.Single(Entity => Entity.EntityId == InstantiatedRootId);
    var ExportedComponentIds = ExportedPrefab.Data.Entities.SelectMany(Entity => Entity.Components)
        .Select(Component => Component.ComponentId).ToHashSet(StringComparer.Ordinal);
    Require(runtime.World.Entities.Count == MainEntityCountBeforePrefab + ExportedPrefab.Data.Entities.Count &&
        InstantiatedEntities.Length == ExportedPrefab.Data.Entities.Count &&
        InstantiatedRoot.ParentId is null &&
        runtime.World.Data.Subtree(InstantiatedRootId).Count() == InstantiatedEntities.Length &&
        InstantiatedEntities.All(Entity => Entity.Components.All(Component =>
            !ExportedComponentIds.Contains(Component.ComponentId))) &&
        runtime.World.Entities.Single(Entity => Entity.Id == InstantiatedRootId).Position ==
            ExportedPrefab.Entities.Single(Entity => Entity.Id == ExportedPrefabRoot.EntityId).Position,
        "Prefab instantiation creates an independent World subtree with new IDs");
    var InstantiatedComponents = InstantiatedEntities.SelectMany(Entity => Entity.Components).ToArray();
    var InstantiatedController = InstantiatedComponents.Single(Component => Component.TypeId == "engine.player-controller");
    var InstantiatedAnimator = InstantiatedComponents.Single(Component => Component.TypeId == "engine.animator");
    var InstantiatedCameraEntity = InstantiatedEntities.Single(Entity => Entity.Components.Any(Component =>
        Component.ComponentId == InstantiatedController.State["ViewCamera"]!.GetValue<string>()));
    var InstantiatedMeshEntity = InstantiatedEntities.Single(Entity => Entity.Components.Any(Component =>
        Component.ComponentId == InstantiatedController.State["FacingMesh"]!.GetValue<string>()));
    Require(InstantiatedController.State["ControlledEntity"]!.GetValue<string>() == InstantiatedRootId &&
        InstantiatedCameraEntity.ParentId == InstantiatedRootId && InstantiatedMeshEntity.ParentId == InstantiatedRootId &&
        InstantiatedAnimator.State["Target"]!.GetValue<string>() ==
            InstantiatedController.State["FacingMesh"]!.GetValue<string>() &&
        InstantiatedComponents.Single(Component => Component.TypeId == "engine.third-person-camera")
            .State["Target"]!.GetValue<string>() == InstantiatedRootId,
        "Prefab instantiation remaps parent, entity, and cross-child component references to the new subtree");
    var MainStateAfterPrefab = runtime.World.Serialize();
    runtime.InvokeUiAction("editor.undo");
    Require(runtime.World.Entities.Count == MainEntityCountBeforePrefab &&
        runtime.World.Serialize() == MainStateBeforePrefab,
        "Prefab instantiation is one undoable transaction that restores the original World");
    runtime.InvokeUiAction("editor.redo");
    Require(runtime.World.Serialize() == MainStateAfterPrefab &&
        runtime.World.Data.Entities.Any(Entity => Entity.EntityId == InstantiatedRootId),
        "Prefab Redo restores the entire subtree with identical remapped IDs and references");
    runtime.InvokeUiAction("editor.undo");
    runtime.OpenPrefab(PrefabPath);
    runtime.InvokeUiAction("editor.play");
    Require(runtime.World.Entities.Count == MainEntityCountBeforePrefab && !runtime.World.Data.IsPrefab,
        "Play while a Prefab editor is open still runs the main World");
    runtime.InvokeUiAction("editor.stop");
    ContentCreationValidation.Run(runtime, temporary.FullName, Require);
    ContentManagerMenuValidation.Run(Require);
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

[DevelopmentSettings("validation.gameplay", SettingsScope.Project, 1,
    DisplayName = "Gameplay", Category = "Game")]
public sealed class ValidationProjectSettings : DevelopmentSettings
{
    [Setting("moveSpeed", DisplayName = "Move Speed", Minimum = 0)]
    public float MoveSpeed { get; init; } = 600;

    [Setting("profile", DisplayName = "Profile")]
    public ValidationSettingsProfile Profile { get; init; } = new();

    [Setting("tags", DisplayName = "Tags")]
    public List<string> Tags { get; init; } = ["starter"];

    [Setting("weights")]
    public Dictionary<string, float> Weights { get; init; } = new();

    [Setting("profiles")]
    public List<ValidationSettingsProfile> Profiles { get; init; } = [];
}

public sealed class ValidationSettingsProfile
{
    [Setting("title", DisplayName = "Title")]
    public string Title { get; init; } = "Default";

    [Setting("strength", Minimum = 0)]
    public float Strength { get; init; } = 1;
}

public sealed class ValidationMainScreen
{
    public ValidationScreenState Screen { get; } = new();
}

public sealed class ValidationScreenState
{
    public string Title { get; set; } = "Validation";
}
