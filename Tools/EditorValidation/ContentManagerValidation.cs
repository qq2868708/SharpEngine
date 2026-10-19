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
    }
}
