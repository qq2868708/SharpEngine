using System.Net;
using System.Text;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

internal static class AssetEditorDockMarkup
{
    public static string Build(string HostId, DockNodeState Root,
        IReadOnlyDictionary<string, AssetEditorPaneDefinition> Panes,
        Func<string, string> BuildPaneMarkup, string PrimaryPaneId)
    {
        var Markup = new StringBuilder();
        AppendNode(Markup, Root, HostId, Panes, BuildPaneMarkup, PrimaryPaneId);
        return Markup.ToString();
    }

    private static void AppendNode(StringBuilder Markup, DockNodeState Node,
        string HostId, IReadOnlyDictionary<string, AssetEditorPaneDefinition> Panes,
        Func<string, string> BuildPaneMarkup, string PrimaryPaneId)
    {
        if (Node.Kind == DockNodeKind.Tabs)
        {
            AppendTabGroup(Markup, Node, HostId, Panes, BuildPaneMarkup,
                PrimaryPaneId);
            return;
        }

        var Horizontal = Node.Orientation == DockSplitOrientation.Horizontal;
        var Ratio = Math.Clamp(Node.Ratio ?? 0.5f, 0.05f, 0.95f);
        Markup.Append("<div class=\"dock-split ")
            .Append(Horizontal ? "dock-horizontal" : "dock-vertical")
            .Append("\" data-dock-split-node=\"").Append(Escape(Node.NodeId))
            .Append("\" data-dock-split-ratio=\"").Append(Number(Ratio))
            .Append("\"><div class=\"dock-split-first\" data-dock-split-part=\"first\" style=\"flex-grow:")
            .Append(Number(Ratio)).Append(";\">");
        AppendNode(Markup, Node.First!, HostId, Panes, BuildPaneMarkup, PrimaryPaneId);
        Markup.Append("</div><div class=\"dock-split-second\" data-dock-split-part=\"second\" style=\"flex-grow:")
            .Append(Number(1 - Ratio)).Append(";\">");
        AppendNode(Markup, Node.Second!, HostId, Panes, BuildPaneMarkup, PrimaryPaneId);
        Markup.Append("</div><div class=\"dock-splitter ")
            .Append(Horizontal ? "dock-splitter-horizontal" : "dock-splitter-vertical")
            .Append("\" data-dock-splitter-host=\"").Append(Escape(HostId))
            .Append("\" data-dock-splitter-node=\"").Append(Escape(Node.NodeId))
            .Append("\" data-dock-splitter-orientation=\"")
            .Append(Horizontal ? "horizontal" : "vertical").Append("\" style=\"");
        if (Horizontal)
        {
            Markup.Append("left:").Append(Percent(Ratio * 100))
                .Append(";top:0%;height:100%;");
        }
        else
        {
            Markup.Append("left:0%;top:").Append(Percent(Ratio * 100))
                .Append(";width:100%;");
        }
        Markup.Append("\" /></div>");
    }

    private static void AppendTabGroup(StringBuilder Markup, DockNodeState Node,
        string HostId, IReadOnlyDictionary<string, AssetEditorPaneDefinition> Panes,
        Func<string, string> BuildPaneMarkup, string PrimaryPaneId)
    {
        var Group = Node.TabGroup!;
        Markup.Append("<div class=\"dock-group asset-editor-dock-group\" data-dock-host=\"")
            .Append(Escape(HostId)).Append("\" data-dock-node=\"")
            .Append(Escape(Node.NodeId)).Append("\" data-dock-group=\"")
            .Append(Escape(Group.GroupId))
            .Append("\"><div class=\"dock-tabbar\" data-dock-tabbar=\"true\">");
        for (var Index = 0; Index < Group.Panes.Count; Index++)
        {
            var Pane = Group.Panes[Index];
            var Definition = Panes[Pane.PaneId];
            var Active = Group.ActivePaneId == Pane.PaneId;
            Markup.Append("<button class=\"dock-tab")
                .Append(Active ? " active" : string.Empty)
                .Append("\" data-action=\"editor.dock-activate:")
                .Append(Escape(HostId)).Append(':').Append(Escape(Group.GroupId))
                .Append(':').Append(Escape(Pane.PaneId))
                .Append("\" data-dock-pane=\"").Append(Escape(Pane.PaneId))
                .Append("\" data-dock-host=\"").Append(Escape(HostId))
                .Append("\" data-dock-node=\"").Append(Escape(Node.NodeId))
                .Append("\" data-dock-group=\"").Append(Escape(Group.GroupId))
                .Append("\" data-dock-index=\"").Append(Index)
                .Append("\"><span class=\"dock-tab-label\">")
                .Append(Escape(Definition.Label)).Append("</span>");
            if (Definition.Closable && Pane.PaneId != PrimaryPaneId)
            {
                Markup.Append("<span class=\"dock-tab-close\" data-action=\"editor.dock-close:")
                    .Append(Escape(Pane.PaneId)).Append("\" title=\"Close\">x</span>");
            }
            Markup.Append("</button>");
        }
        Markup.Append("<div class=\"grow\" /></div><div class=\"dock-stack\">");
        foreach (var Pane in Group.Panes)
        {
            var Active = Group.ActivePaneId == Pane.PaneId;
            Markup.Append("<div class=\"dock-pane")
                .Append(Active ? " active" : string.Empty)
                .Append("\" data-pane-id=\"").Append(Escape(Pane.PaneId))
                .Append("\" style=\"display:").Append(Active ? "flex" : "none")
                .Append(";\">").Append(BuildPaneMarkup(Pane.PaneId)).Append("</div>");
        }
        Markup.Append("</div><div class=\"dock-drop-preview\" data-dock-preview=\"result\" />")
            .Append("<div class=\"dock-tab-insert-preview\" data-dock-preview=\"tab-insert\" />")
            .Append("<div class=\"dock-candidate-guide\" data-dock-preview=\"guide\">")
            .Append("<div class=\"dock-guide-center\" />")
            .Append("<div class=\"dock-guide-line dock-guide-nw\" data-dock-guide-line=\"nw\" />")
            .Append("<div class=\"dock-guide-line dock-guide-ne\" data-dock-guide-line=\"ne\" />")
            .Append("<div class=\"dock-guide-line dock-guide-sw\" data-dock-guide-line=\"sw\" />")
            .Append("<div class=\"dock-guide-line dock-guide-se\" data-dock-guide-line=\"se\" />")
            .Append("</div></div>");
    }

    private static string Escape(string Value) => WebUtility.HtmlEncode(Value);

    private static string Number(float Value) => Value.ToString("0.######",
        System.Globalization.CultureInfo.InvariantCulture);

    private static string Percent(float Value) => Value.ToString("0.####",
        System.Globalization.CultureInfo.InvariantCulture) + "%";
}
