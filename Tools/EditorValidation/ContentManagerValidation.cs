using SharpEngine.Assets;
using SharpEngine.Editor;
using SharpEngine.Editor.Layout;

internal static class ContentManagerValidation
{
    internal static void Run(EditorRuntime runtime, string projectRoot,
        Action<bool, string> require)
    {
        var coordinator = runtime.ContentManagers!;
        foreach (var pane in EditorPaneIds.ContentManagers.Skip(1))
            runtime.InvokeUiAction("editor.dock-toggle:" + pane);
        runtime.InvokeUiAction("editor.content.source:content:engine");
        runtime.InvokeUiAction("editor.content.select:content:engine://meshes/cube");
        runtime.InvokeUiAction("editor.content.select-folder:content:engine|Plugins");
        require(coordinator.Get("content").CurrentFolder.Length == 0 &&
            coordinator.Get("content").SelectedPath == "Plugins" &&
            coordinator.Get("content").SelectedSource == AssetSource.Engine,
            "Selecting the virtual Plugins folder must keep the root and replace the prior selection");
        runtime.InvokeUiAction("editor.content.source:content:project");
        runtime.InvokeUiAction("editor.content.select-folder:content:project|Plugins");
        require(coordinator.Get("content").CurrentFolder.Length == 0 &&
            coordinator.Get("content").SelectedPath == "Plugins" &&
            coordinator.Get("content").SelectedSource == AssetSource.Project,
            "Selecting the project virtual Plugins folder must keep the root and replace the prior selection");
        runtime.InvokeUiAction("editor.content.source:content:engine");
        runtime.InvokeUiAction("editor.content.select:content:engine://meshes/cube");
        runtime.InvokeUiAction("editor.content.source:content-2:project");
        runtime.InvokeUiAction("editor.content.open:content-2:project|Worlds");
        runtime.InvokeUiAction("editor.content.select:content-2:project://worlds/main");
        runtime.InvokeUiAction("editor.content.view:content-2:list");
        runtime.InvokeUiAction("editor.select-entity:player");
        require(coordinator.Get("content").SelectedId == "engine://meshes/cube" &&
            coordinator.Get("content-2").SelectedId == "project://worlds/main",
            "Scene selection must not erase independent content selections");
        runtime.InvokeUiAction("editor.content.activate:content");
        require(runtime.Selection.Id == "engine://meshes/cube",
            "Reactivation must restore the instance selection after scene interaction");
        runtime.InvokeUiAction("editor.context:10:10:asset:engine:engine://meshes/cube");
        require(runtime.Selection.Id == "engine://meshes/cube",
            "Legacy context actions must preserve URI colons");
        runtime.InvokeUiAction("editor.content.activate:content-2");
        runtime.InvokeUiAction("editor.dock-save-layout");
        var saved = new EditorLayoutStore().LoadSavedOrDefault(runtime.EngineRoot, projectRoot);
        var restored = new ContentManagerCoordinator(runtime.Assets!, saved.ContentManagers,
            saved.ActiveContentManagerId);
        require(restored.Get("content").SelectedId == "engine://meshes/cube" &&
            restored.Get("content-2").SelectedId == "project://worlds/main" &&
            restored.Get("content-2").ViewMode == ContentViewMode.List &&
            restored.Get("content-2").CurrentFolder == "Worlds" &&
            restored.ActivePaneId == "content-2",
            "Save Layout and reload must round-trip all independent state");
        runtime.InvokeUiAction("editor.dock-close:content-2");
        require(coordinator.ActivePaneId == "content" &&
            runtime.Selection.Id == "engine://meshes/cube",
            "Closing active content must restore MRU selection");
        runtime.InvokeUiAction("editor.command:core.browse-current-level");
        require(coordinator.ActivePaneId == "content" &&
            coordinator.Active.SelectedId == "project://worlds/main",
            "External browse must route through the active manager");
        foreach (var pane in EditorPaneIds.ContentManagers)
            runtime.InvokeUiAction("editor.dock-close:" + pane);
        runtime.InvokeUiAction("editor.command:core.browse-current-level");
        require(coordinator.ActivePaneId == "content" &&
            coordinator.Active.SelectedId == "project://worlds/main" &&
            runtime.DescribeEditorJson().Contains("float.content", StringComparison.Ordinal),
            "External browse must reopen manager 1 when every manager is closed");

        // Pane commands are owned and scoped by each browser instance.
        foreach (var pane in EditorPaneIds.ContentManagers)
        {
            var manager = coordinator.Get(pane);
            require(manager.Commands.Count == 1 &&
                manager.Commands[0].Id == ContentManager.DeleteAssetCommandId &&
                manager.ContextItems.Count == 1 &&
                manager.ContextItems[0].CommandId == ContentManager.DeleteAssetCommandId,
                $"Content pane {pane} must own its own command and context item");
        }
        require(!ReferenceEquals(coordinator.Get("content").Commands[0],
                coordinator.Get("content-2").Commands[0]),
            "Two content panes must not share one command instance");
        require(runtime.Commands.Commands.All(command =>
                command.Id != ContentManager.DeleteAssetCommandId),
            "Pane commands must stay out of the global editor command registry");

        // A pane publishes its own action; the host dispatches it without parsing the key.
        var manifestPath = Path.Combine(projectRoot, "Content", "Assets", "assets.json");
        var deletablePath = Path.Combine(projectRoot, "Content", "Assets", "Materials",
            "DeleteMe.material.json");
        Directory.CreateDirectory(Path.GetDirectoryName(deletablePath)!);
        File.WriteAllText(deletablePath,
            """{"schemaVersion":1,"name":"Delete Me","shader":"Standard","baseColor":[1,0,0,1],"metallic":0,"roughness":1}""");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(manifestPath))!.AsObject();
        manifest["assets"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "materials/delete-me",
            ["type"] = "Material",
            ["path"] = "Assets/Materials/DeleteMe.material.json",
        });
        File.WriteAllText(manifestPath, manifest.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        runtime.ReloadAssetRegistry();
        // Reloading replaces the browser aggregate; re-acquire the pane that owns the
        // refreshed registry before dispatching its command.
        coordinator = runtime.ContentManagers!;
        var BrowsePane = coordinator.Get("content");
        runtime.InvokeUiAction("editor.content.source:content:project");
        BrowsePane.Select("project://materials/delete-me");
        require(BrowsePane.SelectedId == "project://materials/delete-me",
            "The pane must hold its own selection before its command runs");
        // Engine-owned assets are not deletable, and the pane decides that locally.
        runtime.InvokeUiAction("editor.content.source:content:engine");
        BrowsePane.Select("engine://meshes/cube");
        var engineAction = BrowsePane.CommandAction(ContentManager.DeleteAssetCommandId);
        runtime.InvokeUiAction(engineAction);
        require(runtime.Assets!.Find("engine://meshes/cube") is not null,
            "A pane command must not delete an engine-owned asset");
        runtime.InvokeUiAction("editor.content.source:content:project");
        BrowsePane.Select("project://materials/delete-me");
        runtime.InvokeUiAction(BrowsePane.CommandAction(ContentManager.DeleteAssetCommandId));
        require(runtime.Assets!.Find("project://materials/delete-me") is null,
            "A dispatched pane command must remove the registry entry");
        require(!File.Exists(deletablePath),
            "A dispatched pane command must remove the source file");
        require(!File.ReadAllText(manifestPath).Contains("materials/delete-me",
                StringComparison.Ordinal),
            "A dispatched pane command must remove the manifest entry");
    }
}
