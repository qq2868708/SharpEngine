using System.Net;
using System.Text;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

internal static class EditorDockMarkup
{
    public static string Build(DockHostState host, string viewportTitle)
    {
        ArgumentNullException.ThrowIfNull(host);
        var markup = new StringBuilder("<div class=\"dock-canvas\">");
        AppendNode(markup, host.Root, host.HostId, viewportTitle);
        markup.Append("</div>");
        return markup.ToString();
    }

    public static string PaneLabel(string paneId, string viewportTitle) => paneId switch
    {
        EditorPaneIds.Viewport => viewportTitle,
        EditorPaneIds.UiEditor => "UI Editor",
        EditorPaneIds.Hierarchy => "Hierarchy",
        EditorPaneIds.Content => "Content Manager",
        EditorPaneIds.Inspector => "Inspector",
        EditorPaneIds.Console => "Console",
        _ => paneId,
    };

    private static void AppendNode(StringBuilder markup, DockNodeState node, string hostId,
        string viewportTitle)
    {
        if (node.Kind == DockNodeKind.Tabs)
        {
            AppendTabGroup(markup, node, hostId, viewportTitle);
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
        AppendNode(markup, node.First!, hostId, viewportTitle);
        markup.Append("</div><div class=\"dock-split-second\" data-dock-split-part=\"second\" style=\"flex-grow:")
            .Append(Number(1 - ratio)).Append(";\">");
        AppendNode(markup, node.Second!, hostId, viewportTitle);
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
        string viewportTitle)
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
                .Append("\" data-action=\"editor.dock-activate:")
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
            AppendPane(markup, pane.PaneId);
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

    private static void AppendPane(StringBuilder markup, string paneId)
    {
        switch (paneId)
        {
            case EditorPaneIds.Hierarchy:
                markup.Append("<div class=\"pane-toolbar\"><div class=\"search-field\"><img class=\"icon\" src=\"Icons/search.tga\" /><input type=\"text\" id=\"hierarchySearch\" placeholder=\"Search hierarchy\" /></div><button class=\"small icon-button\" action=\"editor.project-menu\" title=\"Add entity\"><img class=\"icon\" src=\"Icons/add.tga\" /><span class=\"control-tooltip tooltip-right\">Add entity</span></button></div><div id=\"hierarchyItems\" class=\"hierarchy-items\" data-context-kind=\"scene\" data-hierarchy-root=\"true\" />");
                break;
            case EditorPaneIds.Content:
                markup.Append("<div class=\"pane-toolbar\"><div class=\"search-field\"><img class=\"icon\" src=\"Icons/search.tga\" /><input type=\"text\" id=\"projectSearch\" placeholder=\"Search Content\" /></div><button class=\"small icon-button\" action=\"editor.content.clear\" title=\"Reset to Content root\"><img class=\"icon\" src=\"Icons/reset.tga\" /><span class=\"control-tooltip tooltip-right\">Reset to Content root</span></button></div><div class=\"pane-content content-pane\"><div class=\"content-navigation\"><button id=\"content-up\" class=\"icon-button\" action=\"editor.content.up\" title=\"Parent folder\"><img class=\"icon\" src=\"Icons/up.tga\" /><span class=\"control-tooltip\">Parent folder</span></button><img class=\"icon breadcrumb-icon\" src=\"Icons/folder.tga\" /><span id=\"contentBreadcrumb\">Content</span></div><div class=\"content-toolbar\"><button id=\"content-source-all\" class=\"icon-label\" action=\"editor.content.source:all\"><img class=\"icon\" src=\"Icons/all.tga\" />All</button><button id=\"content-source-project\" class=\"icon-label\" action=\"editor.content.source:project\"><img class=\"icon\" src=\"Icons/folder.tga\" />Project</button><button id=\"content-source-engine\" class=\"icon-label\" action=\"editor.content.source:engine\"><img class=\"icon\" src=\"Icons/engine.tga\" />Engine</button></div><div class=\"content-toolbar content-types\"><button id=\"content-type-all\" class=\"content-type icon-label\" action=\"editor.content.type:All\"><img class=\"icon\" src=\"Icons/filter.tga\" />All types</button><button id=\"content-type-mesh\" class=\"content-type icon-button\" action=\"editor.content.type:Mesh\" title=\"Meshes\"><img class=\"icon\" src=\"Icons/mesh.tga\" /></button><button id=\"content-type-material\" class=\"content-type icon-button\" action=\"editor.content.type:Material\" title=\"Materials\"><img class=\"icon\" src=\"Icons/material.tga\" /></button><button id=\"content-type-world\" class=\"content-type icon-button\" action=\"editor.content.type:World\" title=\"Worlds\"><img class=\"icon\" src=\"Icons/world.tga\" /></button></div><div id=\"contentSummary\" class=\"content-summary\">Content</div><div id=\"projectSection\" class=\"content-section\"><div class=\"section-label\">PROJECT CONTENT</div><div id=\"projectItems\" data-context-kind=\"folder\" data-context-source=\"project\" /></div><div id=\"engineSection\" class=\"content-section\"><div class=\"section-label\">ENGINE CONTENT</div><div id=\"engineItems\" data-context-kind=\"folder\" data-context-source=\"engine\" /></div><div id=\"projectPath\" class=\"path\">Content</div></div>");
                break;
            case EditorPaneIds.Inspector:
                markup.Append("<div class=\"pane-content\"><div id=\"selectedObject\">Main Scene</div><div class=\"object-kind\" id=\"selectedKind\">World</div><div class=\"component\"><div class=\"component-title\">Transform</div><div class=\"field\"><label>Parent</label><span id=\"parent\">None</span></div><div class=\"field\"><label>Position</label><span id=\"position\">0 / 0 / 0</span></div><div class=\"field\"><label>Rotation</label><span id=\"rotation\">0 / 0 / 0</span></div><div class=\"field\"><label>Scale</label><span id=\"scale\">1 / 1 / 1</span></div></div><div class=\"component\"><div class=\"component-title\">Properties</div><div class=\"field\"><label>State</label><span id=\"status\">Ready</span></div><div class=\"field\"><label>Source</label><span id=\"assetSource\">Engine</span></div></div></div>");
                break;
            case EditorPaneIds.Console:
                markup.Append("<div class=\"console-header\"><span class=\"good\">0 errors</span></div><div class=\"console-line\"><span class=\"log-time\">[Editor]</span><span id=\"detailsText\">Engine initialized.</span></div><div class=\"console-line muted\"><span class=\"log-time\">[UI]</span><span>RmlUi 6.1 / OpenGL renderer</span></div>");
                break;
            case EditorPaneIds.Viewport:
                markup.Append("<div class=\"viewport-pane\"><viewport id=\"viewportCanvas\" tabindex=\"0\" /><div id=\"viewportOverlay\"><button id=\"moveTool\" class=\"viewport-tool icon-button active\" action=\"editor.tool-move\" title=\"Move\"><img class=\"icon\" src=\"Icons/move.tga\" /></button><button id=\"rotateTool\" class=\"viewport-tool icon-button\" action=\"editor.tool-rotate\" title=\"Rotate\"><img class=\"icon\" src=\"Icons/rotate.tga\" /></button><button id=\"scaleTool\" class=\"viewport-tool icon-button\" action=\"editor.tool-scale\" title=\"Scale\"><img class=\"icon\" src=\"Icons/scale.tga\" /></button><button id=\"viewportProjection\" class=\"viewport-projection\">Perspective</button></div><div class=\"viewport-footer\"><span id=\"viewportStats\">Scene ready</span><div class=\"grow\" /><span id=\"viewportHelp\">WASD/QE Move - RMB Orbit - MMB Pan - Wheel Zoom</span></div></div>");
                break;
            case EditorPaneIds.UiEditor:
                markup.Append("<div class=\"ui-editor-pane\"><div id=\"uiDocumentTabs\" class=\"ui-document-tabs\"><div class=\"ui-editor-empty-tab\">Open an RML asset from Content Manager</div></div><div class=\"ui-editor-toolbar\"><button class=\"icon-label\" action=\"editor.ui-save\" title=\"Save UI document\"><img class=\"icon\" src=\"Icons/save.tga\" />Save</button><button action=\"editor.ui-undo\" title=\"Undo UI edit\">Undo</button><button action=\"editor.ui-redo\" title=\"Redo UI edit\">Redo</button><span class=\"ui-toolbar-separator\" /><button class=\"icon-label\" action=\"editor.ui-add:div\">+ Container</button><button class=\"icon-label\" action=\"editor.ui-add:span\">+ Text</button><button class=\"icon-label\" action=\"editor.ui-add:button\">+ Button</button><div class=\"grow\" /><span id=\"uiEditorResolution\" class=\"ui-resolution\">1280 x 720</span></div><div class=\"ui-editor-workspace\"><div class=\"ui-editor-hierarchy-panel\"><div class=\"ui-panel-title\">ELEMENTS</div><div id=\"uiElementHierarchy\" class=\"ui-element-hierarchy\"><div class=\"ui-editor-empty\">No UI document open</div></div></div><div class=\"ui-editor-canvas-shell\"><div class=\"ui-canvas-rulers\"><span>SCREEN PREVIEW</span><div class=\"grow\" /><span>Flex + Absolute</span></div><div class=\"ui-editor-canvas-scroll\"><div id=\"uiEditorCanvas\" class=\"ui-editor-canvas\"><div class=\"ui-editor-empty-canvas\"><div class=\"ui-empty-icon\">UI</div><div>Open a project RML asset to begin</div></div></div></div><div id=\"uiEditorDiagnostics\" class=\"ui-editor-diagnostics\">No document loaded</div></div><div class=\"ui-editor-inspector-panel\"><div class=\"ui-panel-title\">UI INSPECTOR</div><div id=\"uiElementInspector\" class=\"ui-element-inspector\"><div class=\"ui-editor-empty\">Select an element</div></div></div></div></div>");
                break;
        }
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
    private static string Number(float value) =>
        value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    private static string Percent(float value) =>
        value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "%";
}
