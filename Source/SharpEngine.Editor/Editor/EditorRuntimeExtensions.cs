using System.Text;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;
using SharpEngine.Logging;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    internal string ResolveCommandLabel(EditorCommand Command, string? Fallback)
    {
        if (Command.GetLabel is null)
        {
            return Command.Label;
        }

        try
        {
            return EditorExtensionIds.RequireLabel(Command.GetLabel(_commandContext), "getLabel");
        }
        catch (Exception Exception)
        {
            SharpLog.Error("EditorExtension", $"Command {Command.Id} GetLabel failed", Exception);
            return Fallback ?? Command.Label;
        }
    }

    private void RegisterBuiltInEditorUi()
    {
        AssetOpeners.Register(new EditorAssetOpener("engine.ui-editor", "RmlDocument",
            Context => new UiAssetEditorInstance(Context), Priority: -100,
            EditorId: "ui-editor"));
        AssetOpeners.Register(new EditorAssetOpener("engine.prefab-editor", "Prefab",
            Context => new PrefabAssetEditorInstance(Context), Priority: -100,
            EditorId: "prefab-editor"));
        AssetOpeners.Register(new EditorAssetOpener("engine.world-editor", "World",
            OpenWorldAsset, Priority: -100, EditorId: "main-editor"));
        _commands.Register(new EditorCommand("core.new-csharp", "New C# Class...",
            _ => OpenSourceWizard(), _ => _projectManifest is not null));
        _commands.Register(new EditorCommand("core.build-project-code", "Build Project Code",
            _ => RequestProjectBuild?.Invoke(), _ => RequestProjectBuild is not null && _projectManifest is not null));
        _commands.Register(new EditorCommand("core.new-prefab", "New Prefab", _ => InvokeUiAction("editor.prefab-new"),
            _ => PlayState == EditorPlayState.Stopped));
        _commands.Register(new EditorCommand("core.new-entity", "Create Empty Entity", _ => InvokeUiAction("editor.component-create-entity")));
        _commands.Register(new EditorCommand("core.step", "Step", _ =>
        {
            Instance?.Step();
            if (_runtimeWorld is not null)
            {
                _world!.ApplyRuntimeFrame(_runtimeWorld.Frame);
            }
            RefreshInspector();
        },
            _ => PlayState == EditorPlayState.Paused));
        _commands.Register(new EditorCommand("core.new-project", "New Project...",
            command => command.SetStatus("Choose a folder for the new project")));
        _commands.Register(new EditorCommand("core.open-project", "Open Project...",
            command => command.SetStatus("Choose a project folder")));
        _commands.Register(new EditorCommand("core.undo", "Undo",
            _ => InvokeUiAction("editor.undo"),
            _ => PlayState == EditorPlayState.Stopped && _authoring?.CanUndo == true,
            "Undo the last Authoring transaction")
        {
            GetLabel = _ => _authoring?.UndoDescription is { } value
                ? $"Undo {value}" : "Undo",
        });
        _commands.Register(new EditorCommand("core.redo", "Redo",
            _ => InvokeUiAction("editor.redo"),
            _ => PlayState == EditorPlayState.Stopped && _authoring?.CanRedo == true,
            "Redo the next Authoring transaction")
        {
            GetLabel = _ => _authoring?.RedoDescription is { } value
                ? $"Redo {value}" : "Redo",
        });
        _commands.Register(new EditorCommand("core.save-current-level", "Save Current Level",
            command => command.Save(),
            command => command.PlayState == EditorPlayState.Stopped,
            "Save the current level"));
        _commands.Register(new EditorCommand("core.export-scene", "Export Scene Description...",
            command => command.SaveSceneDescription(), tooltip: "Export the current scene as JSON"));
        _commands.Register(new EditorCommand("core.browse-current-level", "Browse Current Level",
            _ => BrowseCurrentLevel(), _ => CurrentWorldAsset() is not null,
            "Select the current level in Content Manager"));
        _commands.Register(new EditorCommand("core.engine-settings", "Engine Settings...",
            _ => InvokeUiAction("editor.engine-settings")));
        _commands.Register(new EditorCommand("core.project-settings", "Project Settings...",
            _ => InvokeUiAction("editor.project-settings")));
        _commands.Register(new EditorCommand("core.show-project-content", "Show Project Content",
            _ => InvokeUiAction("editor.assets-menu")));
        RegisterPaneVisibilityCommand("view.hierarchy", "Hierarchy", EditorPaneIds.Hierarchy);
        RegisterPaneVisibilityCommand("view.content", "Content Manager 1", EditorPaneIds.Content);
        RegisterPaneVisibilityCommand("view.content-2", "Content Manager 2", EditorPaneIds.Content2);
        RegisterPaneVisibilityCommand("view.content-3", "Content Manager 3", EditorPaneIds.Content3);
        RegisterPaneVisibilityCommand("view.content-4", "Content Manager 4", EditorPaneIds.Content4);
        RegisterPaneVisibilityCommand("view.inspector", "Inspector", EditorPaneIds.Inspector);
        RegisterPaneVisibilityCommand("view.console", "Console", EditorPaneIds.Console);
        RegisterPaneVisibilityCommand("view.logs", "Logs", EditorPaneIds.Logs);
        _commands.Register(new EditorCommand("view.save-layout", "Save Layout",
            _ => InvokeUiAction("editor.dock-save-layout")));
        _commands.Register(new EditorCommand("view.reset-layout", "Reset Layout",
            _ => InvokeUiAction("editor.dock-reset-layout")));
        _commands.Register(new EditorCommand("core.play-pause", "Play / Pause",
            _ => InvokeUiAction(PlayState == EditorPlayState.Playing ? "editor.pause" : "editor.play"),
            _ => ProjectCodeAvailable,
            tooltip: "Start or pause simulation",
            getChecked: _ => PlayState == EditorPlayState.Paused));
        _commands.Register(new EditorCommand("core.stop", "Stop",
            _ => InvokeUiAction("editor.stop"),
            _ => PlayState != EditorPlayState.Stopped,
            "Stop simulation and restore edit state"));
        _commands.Register(new EditorCommand("core.viewport-move", "Move",
            _ => InvokeUiAction("editor.tool-move"),
            getChecked: _ => _viewportTool == "move"));
        _commands.Register(new EditorCommand("core.viewport-rotate", "Rotate",
            _ => InvokeUiAction("editor.tool-rotate"),
            getChecked: _ => _viewportTool == "rotate"));
        _commands.Register(new EditorCommand("core.viewport-scale", "Scale",
            _ => InvokeUiAction("editor.tool-scale"),
            getChecked: _ => _viewportTool == "scale"));

        foreach (var menu in new[]
        {
            new EditorMenu("file", "File", 100),
            new EditorMenu("edit", "Edit", 200),
            new EditorMenu("assets", "Assets", 300),
            new EditorMenu("view", "View", 400),
            new EditorMenu("window", "Window", 500),
            new EditorMenu("help", "Help", 600),
        }) _menus.RegisterBuiltInMenu(menu);

        _menus.RegisterCategory(new EditorMenuCategory("file", "project", 100));
        _menus.RegisterCategory(new EditorMenuCategory("file", "level", 200));
        _menus.RegisterCategory(new EditorMenuCategory("file", "settings", 300));
        _menus.RegisterCategory(new EditorMenuCategory("edit", "history", 50));
        _menus.RegisterCategory(new EditorMenuCategory("assets", "content", 100));
        _menus.RegisterCategory(new EditorMenuCategory("view", "panes", 100));
        _menus.RegisterCategory(new EditorMenuCategory("view", "layout", 200));
        _menus.RegisterItem(new EditorMenuItem("file", "project", "core.new-project", 100));
        _menus.RegisterItem(new EditorMenuItem("file", "project", "core.open-project", 200));
        _menus.RegisterItem(new EditorMenuItem("file", "project", "core.new-csharp", 300));
        _menus.RegisterItem(new EditorMenuItem("file", "project", "core.build-project-code", 400));
        _menus.RegisterItem(new EditorMenuItem("file", "level", "core.save-current-level", 100));
        _menus.RegisterItem(new EditorMenuItem("file", "level", "core.export-scene", 200));
        _menus.RegisterItem(new EditorMenuItem("file", "settings", "core.engine-settings", 100));
        _menus.RegisterItem(new EditorMenuItem("file", "settings", "core.project-settings", 200));
        _menus.RegisterItem(new EditorMenuItem("edit", "history", "core.undo", 100));
        _menus.RegisterItem(new EditorMenuItem("edit", "history", "core.redo", 200));
        _menus.RegisterItem(new EditorMenuItem("assets", "content", "core.browse-current-level", 100));
        _menus.RegisterItem(new EditorMenuItem("assets", "content", "core.show-project-content", 200));
        _menus.RegisterItem(new EditorMenuItem("assets", "content", "core.new-prefab", 300));
        _menus.RegisterItem(new EditorMenuItem("edit", "history", "core.new-entity", 300));
        _menus.RegisterItem(new EditorMenuItem("edit", "history", "core.step", 400));
        _menus.RegisterItem(new EditorMenuItem("view", "panes", "view.hierarchy", 100));
        _menus.RegisterItem(new EditorMenuItem("view", "panes", "view.content", 300));
        _menus.RegisterItem(new EditorMenuItem("view", "panes", "view.content-2", 310));
        _menus.RegisterItem(new EditorMenuItem("view", "panes", "view.content-3", 320));
        _menus.RegisterItem(new EditorMenuItem("view", "panes", "view.content-4", 330));
        _menus.RegisterItem(new EditorMenuItem("view", "panes", "view.inspector", 400));
        _menus.RegisterItem(new EditorMenuItem("view", "panes", "view.console", 500));
        _menus.RegisterItem(new EditorMenuItem("view", "panes", "view.logs", 510));
        _menus.RegisterItem(new EditorMenuItem("view", "layout", "view.save-layout", 100));
        _menus.RegisterItem(new EditorMenuItem("view", "layout", "view.reset-layout", 200));

        _toolbar.RegisterCategory(new EditorToolbarCategory("level", 100));
        _toolbar.RegisterCategory(new EditorToolbarCategory("playback", 100,
            EditorToolbarAlignment.Center));
        _toolbar.RegisterTool(new EditorToolbarTool("save-level", "level", "Save Current Level",
            ["core.save-current-level"], 100, "Save the current level", "save"));
        _toolbar.RegisterTool(new EditorToolbarTool("browse-level", "level", "Browse Current Level",
            ["core.browse-current-level"], 200,
            "Browse the current level in Content Manager", "folder"));
        _toolbar.RegisterTool(new EditorToolbarTool("play-pause", "playback", "Play",
            ["core.play-pause"], 100, "Play / Pause", "play",
            getLabel: _ => PlayState == EditorPlayState.Playing ? "Pause" : "Play",
            getIcon: _ => PlayState == EditorPlayState.Playing ? "pause" : "play"));
        _toolbar.RegisterTool(new EditorToolbarTool("stop", "playback", "Stop",
            ["core.stop"], 200, "Stop", "stop"));
        _commands.Register(new EditorCommand("core.mcp-settings", "MCP Settings...",
            _ => OpenMcpSettings(), tooltip: "Configure the MCP port and restart the server"));
        _toolbar.RegisterCategory(new EditorToolbarCategory("mcp", 100, EditorToolbarAlignment.Right));
        _toolbar.RegisterTool(new EditorToolbarTool("mcp", "mcp", "MCP",
            ["core.mcp-settings"], 100, "Configure the MCP port and restart the server",
            getLabel: _ => McpToolbarLabel()));
        CustomBarRegistry.Register(new ViewportTransformCustomBar());
    }

    private void RegisterPaneVisibilityCommand(string commandId, string label, string paneId)
    {
        _commands.Register(new EditorCommand(commandId, label,
            _ => InvokeUiAction($"editor.dock-toggle:{paneId}"),
            getChecked: _ => IsPaneVisible(paneId)));
    }

    private AssetDescriptor? CurrentWorldAsset()
    {
        if (_world is null || _assets is null) return null;
        var sourcePath = Path.GetFullPath(_world.SourcePath);
        return _assets.Assets.FirstOrDefault(asset =>
        {
            var resolved = _assets.ResolvePath(asset);
            return resolved is not null && string.Equals(Path.GetFullPath(resolved), sourcePath,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    private void BrowseCurrentLevel()
    {
        var asset = CurrentWorldAsset();
        if (asset is null) return;
        var manager = EnsureContentManagerForExternalBrowse();
        if (manager is null) return;
        manager.SetSearch(string.Empty);
        manager.SetType(null);
        manager.SetSource(asset.Source == AssetSource.Project
            ? ContentSourceFilter.Project : ContentSourceFilter.Engine);
        var relative = asset.RelativePath.Replace('\\', '/');
        const string contentPrefix = "Content/";
        if (relative.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase))
            relative = relative[contentPrefix.Length..];
        var slash = relative.LastIndexOf('/');
        manager.OpenFolder(slash < 0 ? string.Empty : relative[..slash]);
        manager.Select(asset.Id);
        ApplyActiveContentSelection();
        CommitContentManagerState(
            $"{ContentManagerLabel(_contentManagers!.ActivePaneId)} · selected {asset.Id}",
            rebuildDock: true);
    }

    private void RefreshExtensionUi()
    {
        if (!_nativeApi.HasDocumentUi) return;
        var menus = BuildMenuMarkup();
        var left = BuildToolbarMarkup(EditorToolbarAlignment.Left);
        var center = BuildToolbarMarkup(EditorToolbarAlignment.Center);
        var right = BuildToolbarMarkup(EditorToolbarAlignment.Right);
        var viewportCustomBar = CustomBarRegistry.BuildMarkup(
            EditorCustomBarRegistry.ViewportSurfaceId);
        var timelineCustomBar = CustomBarRegistry.BuildMarkup(
            EditorCustomBarRegistry.TimelineSurfaceId);

        PublishMarkup("topMenus", menus, ref _menuMarkup);
        PublishMarkup("toolbarLeft", left, ref _toolbarLeftMarkup);
        PublishMarkup("toolbarCenter", center, ref _toolbarCenterMarkup);
        PublishMarkup("toolbarRight", right, ref _toolbarRightMarkup);
        PublishMarkup("viewportCustomBar", viewportCustomBar, ref ViewportCustomBarMarkup);
        PublishMarkup("timelineCustomBar", timelineCustomBar, ref TimelineCustomBarMarkup);
        _extensionUiDirty = false;
    }

    private void PublishMarkup(string id, string value, ref string current)
    {
        if (!_extensionUiDirty && value == current) return;
        _nativeApi.SetUiMarkup(id, value);
        current = value;
    }

    private string BuildMenuMarkup()
    {
        var markup = new StringBuilder();
        foreach (var menu in _menus.OrderedMenus())
        {
            var popupId = $"menu-popup-{menu.Id}";
            markup.Append("<div class=\"top-menu\"><button id=\"menu-root-")
                .Append(Escape(menu.Id))
                .Append("\" class=\"menu-root\" data-popup-id=\"")
                .Append(Escape(popupId)).Append("\">").Append(Escape(menu.Label))
                .Append("</button><div id=\"").Append(Escape(popupId))
                .Append("\" class=\"menu-popup dynamic-popup\">");
            AppendMenuCategories(markup, _menus.OrderedCategories(menu.Id),
                category => _menus.OrderedItems(menu.Id, category.Id),
                category => $"menu-{menu.Id}-category-{category.Id}");
            markup.Append("</div></div>");
        }
        return markup.ToString();
    }

    private string BuildToolbarMarkup(EditorToolbarAlignment alignment)
    {
        var markup = new StringBuilder();
        var categories = _toolbar.OrderedCategories(alignment);
        for (var index = 0; index < categories.Count; index++)
        {
            var category = categories[index];
            markup.Append("<div class=\"toolbar-category")
                .Append(index == 0 && alignment != EditorToolbarAlignment.Center
                    ? " first" : string.Empty)
                .Append("\" data-category=\"")
                .Append(Escape(category.Id)).Append("\">");
            foreach (var tool in _toolbar.OrderedTools(category.Id))
                AppendToolbarTool(markup, tool);
            markup.Append("</div>");
        }
        return markup.ToString();
    }

    private void AppendToolbarTool(StringBuilder markup, EditorToolbarTool tool)
    {
        var commands = tool.CommandIds.Select(id => _commands.TryGet(id, out var command) ? command : null)
            .Where(command => command is not null).Cast<EditorCommand>().ToArray();
        if (commands.Length == 0) return;
        var enabled = commands.Any(command => _commands.CanExecute(command, _commandContext));
        var label = SafeToolLabel(tool);
        var icon = SafeToolIcon(tool);
        var popupId = $"toolbar-popup-{tool.Id}";
        markup.Append("<div class=\"toolbar-tool\"><button id=\"toolbar-tool-")
            .Append(Escape(tool.Id)).Append("\" class=\"toolbar-button")
            .Append(enabled ? "\"" : " disabled\" disabled=\"true\"");
        if (commands.Length == 1)
            markup.Append(" data-action=\"editor.command:").Append(Escape(commands[0].Id)).Append("\"");
        else
            markup.Append(" data-popup-id=\"").Append(Escape(popupId)).Append("\"");
        markup.Append(" title=\"").Append(Escape(tool.Tooltip ?? label)).Append("\">");
        if (icon is not null)
            markup.Append("<img class=\"icon\" src=\"").Append(Escape(EditorResourceUris.IconSource(icon))).Append("\" />");
        else
            markup.Append(Escape(label));
        if (commands.Length > 1) markup.Append("<span class=\"dropdown-arrow\">v</span>");
        markup.Append("<span class=\"control-tooltip\">").Append(Escape(tool.Tooltip ?? label))
            .Append("</span></button>");
        if (commands.Length > 1)
        {
            markup.Append("<div id=\"").Append(Escape(popupId))
                .Append("\" class=\"menu-popup toolbar-popup dynamic-popup\">");
            foreach (var command in commands)
                AppendCommandMenuItem(markup, command, $"toolbar-{tool.Id}");
            markup.Append("</div>");
        }
        markup.Append("</div>");
    }

    private string SafeToolLabel(EditorToolbarTool tool)
    {
        if (tool.GetLabel is null) return tool.Label;
        try { return EditorExtensionIds.RequireLabel(tool.GetLabel(_commandContext), "getLabel"); }
        catch (Exception exception)
        {
            SharpLog.Error("EditorExtension", $"Toolbar {tool.Id} GetLabel failed", exception);
            return tool.Label;
        }
    }

    private string? SafeToolIcon(EditorToolbarTool tool)
    {
        if (tool.GetIcon is null) return tool.Icon;
        try { return EditorExtensionIds.ValidateIcon(tool.GetIcon(_commandContext), "getIcon"); }
        catch (Exception exception)
        {
            SharpLog.Error("EditorExtension", $"Toolbar {tool.Id} GetIcon failed", exception);
            return tool.Icon;
        }
    }

    private void AppendMenuCategories<TCategory>(StringBuilder markup,
        IReadOnlyList<TCategory> categories,
        Func<TCategory, IReadOnlyList<EditorMenuItem>> items,
        Func<TCategory, string> identity)
    {
        var wroteCategory = false;
        foreach (var category in categories)
        {
            var categoryItems = items(category);
            var commands = categoryItems.Select(item =>
                    _commands.TryGet(item.CommandId, out var command) ? command : null)
                .Where(command => command is not null).Cast<EditorCommand>().ToArray();
            if (commands.Length == 0) continue;
            if (wroteCategory) markup.Append("<div class=\"menu-divider\" />");
            foreach (var command in commands)
                AppendCommandMenuItem(markup, command, identity(category));
            wroteCategory = true;
        }
        if (!wroteCategory) markup.Append("<div class=\"empty-menu\">No commands</div>");
    }

    private void AppendCommandMenuItem(StringBuilder markup, EditorCommand command,
                                       string surfaceId)
    {
        var enabled = _commands.CanExecute(command, _commandContext);
        var checkable = command.GetChecked is not null;
        var checkedValue = _commands.IsChecked(command, _commandContext);
        var label = command.Label;
        if (command.GetLabel is not null)
        {
            try { label = EditorExtensionIds.RequireLabel(command.GetLabel(_commandContext), "getLabel"); }
            catch (Exception exception)
            { SharpLog.Error("EditorExtension", $"Command {command.Id} GetLabel failed", exception); }
        }
        markup.Append("<button id=\"").Append(Escape(surfaceId))
            .Append("-command-").Append(Escape(command.Id))
            .Append("\" class=\"menu-command")
            .Append(enabled ? "\"" : " disabled\" disabled=\"true\"")
            .Append(" data-action=\"editor.command:").Append(Escape(command.Id)).Append("\"");
        if (command.Tooltip is not null)
            markup.Append(" title=\"").Append(Escape(command.Tooltip)).Append("\"");
        markup.Append("><span class=\"menu-check\">");
        if (checkable) markup.Append(checkedValue ? "[x]" : "[ ]");
        markup.Append("</span><span>").Append(Escape(label)).Append("</span></button>");
    }

    private void HandleContextRequest(string action)
    {
        var payload = action["editor.context:".Length..].Split(':', 6);
        if (payload.Length is not (5 or 6) || !int.TryParse(payload[0], out var x) ||
            !int.TryParse(payload[1], out var y)) return;
        var source = payload[3].Equals("engine", StringComparison.OrdinalIgnoreCase)
            ? AssetSource.Engine : AssetSource.Project;
        var hasManager = payload.Length == 6 &&
            (payload[4].Length == 0 || EditorPaneIds.IsContentManager(payload[4]));
        var managerId = hasManager ? payload[4] :
            _contentManagers?.ActivePaneId ?? EditorPaneIds.Content;
        var id = hasManager ? payload[5] : string.Join(':', payload.Skip(4));
        if (payload[2] is "asset" or "folder" &&
            EditorPaneIds.IsContentManager(managerId)) ActivateContentManager(managerId);
        EditorContextMenuKind kind;
        switch (payload[2].ToLowerInvariant())
        {
            case "object":
                kind = EditorContextMenuKind.Object;
                SelectEntity(id);
                break;
            case "asset":
                kind = EditorContextMenuKind.Asset;
                if (_assets?.Find(id) is not null) SelectAsset(id);
                else SelectContentFile(managerId, source, id);
                break;
            case "folder":
                kind = EditorContextMenuKind.Folder;
                SelectContentFolder(managerId, source, id);
                break;
            default:
                kind = EditorContextMenuKind.Scene;
                InvokeUiAction("editor.select-scene");
                break;
        }
        ShowContextMenu(kind, x, y);
    }

    private void SelectContentFolder(string managerId, AssetSource source, string path)
    {
        var manager = ActivateContentManager(managerId);
        manager?.SelectFolder(source, path);
        _selection = new EditorSelection(EditorSelectionKind.Folder, Path: path, Source: source);
        MarkExtensionUiDirty();
        RefreshHierarchy();
        CommitContentManagerState($"Selected Content/{path}");
        RefreshViewportModeUi();
        SetText("selectedObject", Path.GetFileName(path));
        SetText("selectedKind", $"Folder · {source}");
        SetText("parent", "Content");
        SetText("position", path);
        SetText("rotation", "-");
        SetText("scale", "Available");
    }

    private void SelectContentFile(string managerId, AssetSource source, string path)
    {
        var manager = ActivateContentManager(managerId);
        manager?.SelectFile(source, path);
        _selection = new EditorSelection(EditorSelectionKind.Asset, Path: path, Source: source);
        MarkExtensionUiDirty();
        RefreshHierarchy();
        CommitContentManagerState($"Selected Content/{path}");
        RefreshViewportModeUi();
    }

    private void ShowContextMenu(EditorContextMenuKind kind, int x, int y)
    {
        var markup = new StringBuilder();
        var wroteCategory = false;
        foreach (var category in _contextMenus.OrderedCategories(kind))
        {
            var commands = _contextMenus.OrderedItems(kind, category.Id)
                .Select(item => _commands.TryGet(item.CommandId, out var command) ? command : null)
                .Where(command => command is not null).Cast<EditorCommand>().ToArray();
            if (commands.Length == 0) continue;
            if (wroteCategory) markup.Append("<div class=\"menu-divider\" />");
            foreach (var command in commands)
                AppendCommandMenuItem(markup, command,
                    $"context-{kind.ToString().ToLowerInvariant()}-category-{category.Id}");
            wroteCategory = true;
        }
        if (!wroteCategory)
        {
            _nativeApi.SetUiProperty("contextMenu", "display", "none");
            return;
        }
        _nativeApi.SetUiMarkup("contextMenu", markup.ToString());
        _nativeApi.SetUiProperty("contextMenu", "left",
            $"{Math.Clamp(x, 0, Math.Max(0, (int)_windowWidth - 250))}px");
        _nativeApi.SetUiProperty("contextMenu", "top",
            $"{Math.Clamp(y, 0, Math.Max(0, (int)_windowHeight - 260))}px");
        _nativeApi.SetUiProperty("contextMenu", "display", "block");
    }
}
