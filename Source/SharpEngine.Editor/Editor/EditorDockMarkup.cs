using System.Net;
using System.Text;
using SharpEngine.Editor.Layout;
using SharpEngine.Logging;

namespace SharpEngine.Editor;

internal static class EditorDockMarkup
{
    public static string Build(DockHostState host, string viewportTitle,
        string activeContentManagerId = EditorPaneIds.Content,
        IReadOnlyList<ContentManagerLayoutState>? contentManagers = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        var markup = new StringBuilder("<div class=\"dock-canvas\">");
        var states = (contentManagers ?? EditorLayoutDocument.CreateDefaultContentManagers())
            .ToDictionary(state => state.PaneId, StringComparer.Ordinal);
        AppendNode(markup, host.Root, host.HostId, viewportTitle, activeContentManagerId,
            states);
        markup.Append("</div>");
        return markup.ToString();
    }

    public static string PaneLabel(string paneId, string viewportTitle) =>
        EditorPaneIds.IsContentManager(paneId)
            ? $"Content Manager {EditorPaneIds.ContentManagerNumber(paneId)}"
            : paneId switch
            {
                EditorPaneIds.Viewport => viewportTitle,
                EditorPaneIds.Timeline => "Animation Timeline",
                EditorPaneIds.UiEditor => "UI Editor",
                EditorPaneIds.Hierarchy => "Hierarchy",
                EditorPaneIds.Inspector => "Inspector",
                EditorPaneIds.Console => "Console",
                EditorPaneIds.Logs => "Logs",
                _ => paneId,
            };

    internal static string LogEntriesId(string hostId) => $"logs-{hostId}";

    internal static string BuildLogEntriesMarkup(IEnumerable<SharpLogEvent> entries)
    {
        var markup = new StringBuilder();
        foreach (var entry in entries)
        {
            markup.Append("<div class=\"log-entry log-level-")
                .Append(Escape(entry.LevelName)).Append("\">")
                .Append("<span class=\"log-time\">")
                .Append(Escape(entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff")))
                .Append("</span><span class=\"log-level\">")
                .Append(Escape(entry.LevelName.ToUpperInvariant()))
                .Append("</span><span class=\"log-source\">")
                .Append(Escape(entry.Source)).Append("</span><span class=\"log-category\">")
                .Append(Escape(entry.Category)).Append("</span><span class=\"log-message\">")
                .Append(Escape(entry.Message));
            if (!string.IsNullOrWhiteSpace(entry.Exception))
                markup.Append("<span class=\"log-exception\">")
                    .Append(Escape(entry.Exception!)).Append("</span>");
            markup.Append("</span></div>");
        }
        return markup.Length == 0
            ? "<div class=\"logs-empty\">No log records yet.</div>"
            : markup.ToString();
    }

    private static void AppendNode(StringBuilder markup, DockNodeState node, string hostId,
        string viewportTitle, string activeContentManagerId,
        IReadOnlyDictionary<string, ContentManagerLayoutState> contentManagers)
    {
        if (node.Kind == DockNodeKind.Tabs)
        {
            AppendTabGroup(markup, node, hostId, viewportTitle, activeContentManagerId,
                contentManagers);
            return;
        }

        var horizontal = node.Orientation == DockSplitOrientation.Horizontal;
        var ratio = Math.Clamp(node.Ratio ?? 0.5f, 0.05f, 0.95f);
        markup.Append("<div class=\"dock-split ")
            .Append(horizontal ? "dock-horizontal" : "dock-vertical")
            .Append("\" data-dock-split-node=\"").Append(Escape(node.NodeId))
            .Append("\" data-dock-split-ratio=\"").Append(Number(ratio)).Append("\">")
            .Append("<div class=\"dock-split-first\" data-dock-split-part=\"first\" style=\"flex-grow:")
            .Append(Number(ratio)).Append(";\">");
        AppendNode(markup, node.First!, hostId, viewportTitle, activeContentManagerId,
            contentManagers);
        markup.Append("</div><div class=\"dock-split-second\" data-dock-split-part=\"second\" style=\"flex-grow:")
            .Append(Number(1 - ratio)).Append(";\">");
        AppendNode(markup, node.Second!, hostId, viewportTitle, activeContentManagerId,
            contentManagers);
        markup.Append("</div><div class=\"dock-splitter ")
            .Append(horizontal ? "dock-splitter-horizontal" : "dock-splitter-vertical")
            .Append("\" data-dock-splitter-host=\"").Append(Escape(hostId))
            .Append("\" data-dock-splitter-node=\"").Append(Escape(node.NodeId))
            .Append("\" data-dock-splitter-orientation=\"")
            .Append(horizontal ? "horizontal" : "vertical")
            .Append("\" style=\"");
        if (horizontal)
            markup.Append("left:").Append(Percent(ratio * 100)).Append(";top:0%;height:100%;");
        else
            markup.Append("left:0%;top:").Append(Percent(ratio * 100)).Append(";width:100%;");
        markup.Append("\" /></div>");
    }

    private static void AppendTabGroup(StringBuilder markup, DockNodeState node, string hostId,
        string viewportTitle, string activeContentManagerId,
        IReadOnlyDictionary<string, ContentManagerLayoutState> contentManagers)
    {
        var group = node.TabGroup!;
        markup.Append("<div class=\"dock-group")
            .Append(group.Panes.Any(pane => pane.PaneId == EditorPaneIds.Viewport)
                ? " viewport-group" : string.Empty)
            .Append("\" data-dock-host=\"")
            .Append(Escape(hostId)).Append("\" data-dock-node=\"")
            .Append(Escape(node.NodeId)).Append("\" data-dock-group=\"")
            .Append(Escape(group.GroupId))
            .Append("\"><div class=\"dock-tabbar\" data-dock-tabbar=\"true\">");
        for (var index = 0; index < group.Panes.Count; index++)
        {
            var pane = group.Panes[index];
            var active = string.Equals(group.ActivePaneId, pane.PaneId, StringComparison.Ordinal);
            markup.Append("<button class=\"dock-tab")
                .Append(active ? " active" : string.Empty)
                .Append(EditorPaneIds.IsContentManager(pane.PaneId) ? " content-manager-tab" : string.Empty)
                .Append(pane.PaneId == activeContentManagerId
                    ? " active-content-manager-tab" : string.Empty)
                .Append("\"")
                .Append(EditorPaneIds.IsContentManager(pane.PaneId)
                    ? $" id=\"dock-tab-{Escape(pane.PaneId)}\"" : string.Empty)
                .Append(" data-action=\"editor.dock-activate:")
                .Append(Escape(hostId)).Append(':').Append(Escape(group.GroupId)).Append(':')
                .Append(Escape(pane.PaneId)).Append("\" data-dock-pane=\"")
                .Append(Escape(pane.PaneId)).Append("\" data-dock-host=\"")
                .Append(Escape(hostId)).Append("\" data-dock-node=\"")
                .Append(Escape(node.NodeId)).Append("\" data-dock-group=\"")
                .Append(Escape(group.GroupId)).Append("\" data-dock-index=\"")
                .Append(index).Append("\"><span class=\"dock-tab-label\"")
                .Append(pane.PaneId == EditorPaneIds.Viewport
                    ? " id=\"dock-tab-label-viewport\"" : string.Empty)
                .Append(">")
                .Append(Escape(PaneLabel(pane.PaneId, viewportTitle))).Append("</span>");
            if (pane.PaneId != EditorPaneIds.Viewport)
                markup.Append("<span class=\"dock-tab-close\" data-action=\"editor.dock-close:")
                    .Append(Escape(pane.PaneId)).Append("\" title=\"Close\">x</span>");
            markup.Append("</button>");
        }
        markup.Append("<div class=\"grow\" /></div><div class=\"dock-stack\">");
        foreach (var pane in group.Panes)
        {
            var active = string.Equals(group.ActivePaneId, pane.PaneId, StringComparison.Ordinal);
            markup.Append("<div class=\"dock-pane")
                .Append(active ? " active" : string.Empty).Append("\" data-pane-id=\"")
                .Append(Escape(pane.PaneId)).Append("\" style=\"display:")
                .Append(active ? "flex" : "none").Append(";\">");
            AppendPane(markup, hostId, pane.PaneId, activeContentManagerId, contentManagers);
            markup.Append("</div>");
        }
        markup.Append("</div><div class=\"dock-drop-preview\" data-dock-preview=\"result\" />")
            .Append("<div class=\"dock-tab-insert-preview\" data-dock-preview=\"tab-insert\" />")
            .Append("<div class=\"dock-candidate-guide\" data-dock-preview=\"guide\">")
            .Append("<div class=\"dock-guide-center\" />")
            .Append("<div class=\"dock-guide-line dock-guide-nw\" data-dock-guide-line=\"nw\" />")
            .Append("<div class=\"dock-guide-line dock-guide-ne\" data-dock-guide-line=\"ne\" />")
            .Append("<div class=\"dock-guide-line dock-guide-sw\" data-dock-guide-line=\"sw\" />")
            .Append("<div class=\"dock-guide-line dock-guide-se\" data-dock-guide-line=\"se\" />")
            .Append("</div>")
            .Append("</div>");
    }

    private static void AppendPane(StringBuilder markup, string hostId, string paneId,
        string activeContentManagerId,
        IReadOnlyDictionary<string, ContentManagerLayoutState> contentManagers)
    {
        if (EditorPaneIds.IsContentManager(paneId))
        {
            AppendContentManager(markup, paneId, paneId == activeContentManagerId,
                contentManagers[paneId]);
            return;
        }
        switch (paneId)
        {
            case EditorPaneIds.Hierarchy:
                markup.Append("<div class=\"pane-toolbar\"><div class=\"search-field\"><img class=\"icon\" src=\"sharpengine-resource://engine/Resources/Editor/Icons/search.tga\" /><input type=\"text\" id=\"hierarchySearch\" placeholder=\"Search hierarchy\" /></div><button class=\"small icon-button\" action=\"editor.component-create-entity\" title=\"Add entity\"><img class=\"icon\" src=\"sharpengine-resource://engine/Resources/Editor/Icons/add.tga\" /><span class=\"control-tooltip tooltip-right\">Add entity</span></button></div><div id=\"hierarchyItems\" class=\"hierarchy-items\" data-context-kind=\"scene\" data-hierarchy-root=\"true\" />");
                break;
            case EditorPaneIds.Inspector:
                markup.Append("<div id=\"inspectorContent\" class=\"pane-content\"><div id=\"selectedObject\">Main Scene</div><div class=\"object-kind\" id=\"selectedKind\">World</div><div id=\"componentInspector\"></div><div class=\"legacy-inspector-details\"><div class=\"component-title\">Details</div><div class=\"field\"><label>Parent</label><span id=\"parent\">None</span></div><div class=\"field\"><label>Position</label><span id=\"position\">0 / 0 / 0</span></div><div class=\"field\"><label>Rotation</label><span id=\"rotation\">0 / 0 / 0</span></div><div class=\"field\"><label>Scale</label><span id=\"scale\">1 / 1 / 1</span></div></div><div class=\"component\"><div class=\"component-title\">Properties</div><div class=\"field\"><label>State</label><span id=\"status\">Ready</span></div><div class=\"field\"><label>Source</label><span id=\"assetSource\">Engine</span></div></div></div>");
                break;
            case EditorPaneIds.Console:
                markup.Append("<div class=\"console-header\"><span class=\"good\">Editor status</span></div><div class=\"console-line\"><span class=\"log-time\">[Editor]</span><span id=\"detailsText\">Engine initialized.</span></div><div class=\"console-line muted\"><span class=\"log-time\">[UI]</span><span>RmlUi 6.1</span></div>");
                break;
            case EditorPaneIds.Logs:
                markup.Append("<div class=\"logs-pane\"><div class=\"logs-header\"><span class=\"logs-title\">Unified Log</span><span class=\"logs-hint\">time 路 level 路 source 路 category 路 message</span></div><div id=\"")
                    .Append(Escape(LogEntriesId(hostId)))
                    .Append("\" class=\"log-entries\">")
                    .Append(BuildLogEntriesMarkup([]))
                    .Append("</div></div>");
                break;
            case EditorPaneIds.Viewport:
                markup.Append("<div class=\"viewport-pane\"><viewport id=\"viewportCanvas\" tabindex=\"0\" /><div id=\"viewportOverlay\"><div id=\"viewportCustomBar\" class=\"viewport-custom-bar\" /><button id=\"viewportProjection\" class=\"viewport-projection\">Perspective</button></div><div class=\"viewport-footer\"><span id=\"viewportStats\">Scene ready</span><div class=\"grow\" /><span id=\"viewportHelp\">WASD/QE Move - RMB Orbit - MMB Pan - Wheel Zoom</span></div></div>");
                break;
            case EditorPaneIds.Timeline:
                markup.Append("<div class=\"timeline-pane\"><div id=\"timelineTabs\" class=\"timeline-editor-tabs\" /><div id=\"timelinePreview\" class=\"timeline-preview-host\"><div class=\"timeline-empty\">Open an animation, Timeline, or AnimationStateMachine asset to preview it</div></div><div class=\"timeline-toolbar\"><div id=\"timelineCustomBar\" class=\"timeline-custom-bar\" /></div><div id=\"timelineView\" class=\"timeline-view-host\"><div class=\"timeline-empty\">Double-click an animation, Timeline, or AnimationStateMachine asset in Content Manager</div></div><div class=\"timeline-notify-inspector\"><span id=\"timelineInspectorLabel\" class=\"timeline-inspector-label\">NOTIFY</span><span id=\"timelineSelectedNotify\">Select a clip or add a notify</span></div></div>");
                break;
            case EditorPaneIds.UiEditor:
                markup.Append("<div class=\"ui-editor-pane\"><div id=\"uiEditorTabs\" class=\"ui-editor-tabs\"><div class=\"ui-editor-empty-tab\">Open an RML asset from Content Manager</div></div><div class=\"ui-editor-toolbar\"><button class=\"icon-label\" action=\"editor.ui-save\" title=\"Save UI document\"><img class=\"icon\" src=\"sharpengine-resource://engine/Resources/Editor/Icons/save.tga\" />Save</button><button action=\"editor.ui-undo\" title=\"Undo UI edit\">Undo</button><button action=\"editor.ui-redo\" title=\"Redo UI edit\">Redo</button><span class=\"ui-toolbar-separator\" /><button class=\"icon-label\" action=\"editor.ui-add:div\">+ Container</button><button class=\"icon-label\" action=\"editor.ui-add:span\">+ Text</button><button class=\"icon-label\" action=\"editor.ui-add:button\">+ Button</button><div class=\"grow\" /><span id=\"uiEditorResolution\" class=\"ui-resolution\">1280 x 720</span></div><div class=\"ui-editor-workspace\"><div class=\"ui-editor-hierarchy-panel\"><div class=\"ui-panel-title\">ELEMENTS</div><div id=\"uiElementHierarchy\" class=\"ui-element-hierarchy\"><div class=\"ui-editor-empty\">No UI document open</div></div></div><div class=\"ui-editor-canvas-shell\"><div class=\"ui-canvas-rulers\"><span>SCREEN PREVIEW</span><div class=\"grow\" /><span>Flex + Absolute</span></div><div class=\"ui-editor-canvas-scroll\"><div id=\"uiEditorCanvas\" class=\"ui-editor-canvas\"><div class=\"ui-editor-empty-canvas\"><div class=\"ui-empty-icon\">UI</div><div>Open a project RML asset to begin</div></div></div></div><div id=\"uiEditorDiagnostics\" class=\"ui-editor-diagnostics\">No document loaded</div></div><div class=\"ui-editor-inspector-panel\"><div class=\"ui-panel-title\">UI INSPECTOR</div><div id=\"uiElementInspector\" class=\"ui-element-inspector\"><div class=\"ui-editor-empty\">Select an element</div></div></div></div></div>");
                break;
        }
    }

    private static void AppendContentManager(StringBuilder markup, string paneId, bool active,
        ContentManagerLayoutState state)
    {
        string Id(string local) => $"{paneId}-{local}";
        string Action(string operation, string? value = null) =>
            $"editor.content.{operation}:{paneId}{(value is null ? string.Empty : $":{value}")}";
        markup.Append("<div id=\"").Append(Id("pane")).Append("\" class=\"content-manager-pane")
            .Append(active ? " active-content-manager" : string.Empty)
            .Append("\" data-ui-context-scope=\"").Append(ContentManagerView.ScopeId(paneId))
            .Append("\" data-action=\"").Append(Action("activate")).Append("\">")
            .Append("<div class=\"pane-toolbar content-pane-toolbar\"><button id=\"").Append(Id("add"))
            .Append("\" class=\"icon-label\" data-ui-context-trigger=\"true\" data-ui-context=\"")
            .Append(ContentManagerView.ContextAttribute("content", "", null, true))
            .Append("\">+ Add</button><div class=\"search-field\"><img class=\"icon\" src=\"sharpengine-resource://engine/Resources/Editor/Icons/search.tga\" /><input type=\"text\" id=\"")
            .Append(Id("search")).Append("\" value=\"").Append(Escape(state.Search))
            .Append("\" data-change-action=\"")
            .Append(Action("search") + ":").Append("\" placeholder=\"Search Content\" /></div>")
            .Append("<button class=\"small icon-button\" action=\"").Append(Action("clear"))
            .Append("\" title=\"Reset to Content root\"><img class=\"icon\" src=\"sharpengine-resource://engine/Resources/Editor/Icons/reset.tga\" /><span class=\"control-tooltip tooltip-right\">Reset to Content root</span></button>")
            .Append("<button id=\"").Append(Id("view-tile")).Append("\" class=\"small icon-button\" action=\"")
            .Append(Action("view", "tile")).Append("\" title=\"Tile view\"><img class=\"icon\" src=\"sharpengine-resource://engine/Resources/Editor/Icons/all.tga\" /><span class=\"control-tooltip tooltip-right\">Tile view</span></button>")
            .Append("<button id=\"").Append(Id("view-list")).Append("\" class=\"small icon-button\" action=\"")
            .Append(Action("view", "list")).Append("\" title=\"List view\"><img class=\"icon\" src=\"sharpengine-resource://engine/Resources/Editor/Icons/list.tga\" /><span class=\"control-tooltip tooltip-right\">List view</span></button></div>")
            .Append("<div class=\"pane-content content-pane\"><div class=\"content-controls\"><div class=\"content-navigation\"><button id=\"")
            .Append(Id("up")).Append("\" class=\"icon-button\" action=\"").Append(Action("up"))
            .Append("\" title=\"Parent folder\"><img class=\"icon\" src=\"sharpengine-resource://engine/Resources/Editor/Icons/up.tga\" /><span class=\"control-tooltip\">Parent folder</span></button><img class=\"icon breadcrumb-icon\" src=\"sharpengine-resource://engine/Resources/Editor/Icons/folder.tga\" /><span id=\"")
            .Append(Id("breadcrumb")).Append("\">Content</span></div><div class=\"content-toolbar\">")
            .Append(ContentButton(Id("source-all"), Action("source", "all"), "all", "All"))
            .Append(ContentButton(Id("source-project"), Action("source", "project"), "folder", "Project"))
            .Append(ContentButton(Id("source-engine"), Action("source", "engine"), "engine", "Engine"))
            .Append("</div><div class=\"content-toolbar content-types\">")
            .Append(ContentButton(Id("type-all"), Action("type", "All"), "filter", "All types"))
            .Append(ContentIconButton(Id("type-mesh"), Action("type", "Mesh"), "mesh", "Meshes"))
            .Append(ContentIconButton(Id("type-material"), Action("type", "Material"), "material", "Materials"))
            .Append(ContentIconButton(Id("type-world"), Action("type", "World"), "world", "Worlds"))
            .Append("</div><div id=\"").Append(Id("summary"))
            .Append("\" class=\"content-summary\">Content</div></div><div class=\"content-results-slot\"><div id=\"")
            .Append(Id("results")).Append("\" class=\"content-results\" data-ui-context=\"")
            .Append(ContentManagerView.ContextAttribute("content", "", null)).Append("\"><div id=\"")
            .Append(Id("project-section")).Append("\" class=\"content-section\"><div id=\"").Append(Id("project-label")).Append("\" class=\"section-label\">PROJECT CONTENT</div><div id=\"")
            .Append(Id("project-items")).Append("\" class=\"content-items\" data-ui-context=\"")
            .Append(ContentManagerView.ContextAttribute("content", "project", null)).Append("\" /></div><div id=\"")
            .Append(Id("engine-section")).Append("\" class=\"content-section\"><div id=\"").Append(Id("engine-label")).Append("\" class=\"section-label\">ENGINE CONTENT</div><div id=\"")
            .Append(Id("engine-items")).Append("\" class=\"content-items\" data-ui-context=\"")
            .Append(ContentManagerView.ContextAttribute("content", "engine", null)).Append("\" /></div></div></div><div id=\"")
            .Append(Id("path")).Append("\" class=\"path content-path\">Content</div></div></div>");
    }

    private static string ContentButton(string id, string action, string icon, string label) =>
        $"<button id=\"{Escape(id)}\" class=\"icon-label\" action=\"{Escape(action)}\"><img class=\"icon\" src=\"{Escape(EditorResourceUris.IconSource(icon))}\" />{Escape(label)}</button>";

    private static string ContentIconButton(string id, string action, string icon, string title) =>
        $"<button id=\"{Escape(id)}\" class=\"content-type icon-button\" action=\"{Escape(action)}\" title=\"{Escape(title)}\"><img class=\"icon\" src=\"{Escape(EditorResourceUris.IconSource(icon))}\" /></button>";

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
    private static string Number(float value) =>
        value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    private static string Percent(float value) =>
        value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "%";
}
