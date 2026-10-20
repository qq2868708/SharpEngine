using System.Net;
using System.Text;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

internal static class AssetEditorInstanceUtilities
{
    public static EditorLayoutDocument CreatePreviewLayout(string PreviewPaneId,
        string DetailsPaneId, params string[] BottomPaneIds)
    {
        return new EditorLayoutDocument
        {
            MainHost = new DockHostState
            {
                HostId = "main",
                Root = new DockNodeState
                {
                    NodeId = "asset.root",
                    Kind = DockNodeKind.Split,
                    Orientation = DockSplitOrientation.Horizontal,
                    Ratio = 0.76f,
                    First = new DockNodeState
                    {
                        NodeId = "asset.center",
                        Kind = DockNodeKind.Split,
                        Orientation = DockSplitOrientation.Vertical,
                        Ratio = 0.74f,
                        First = Tabs("asset.preview", "asset.preview.tabs",
                            PreviewPaneId),
                        Second = Tabs("asset.bottom", "asset.bottom.tabs",
                            BottomPaneIds),
                    },
                    Second = Tabs("asset.details", "asset.details.tabs",
                        DetailsPaneId),
                },
            },
            HiddenPaneIds = [],
            ActiveContentManagerId = string.Empty,
            ContentManagers = [],
        };
    }

    public static EditorLayoutDocument CreateDocumentLayout(string PrimaryPaneId,
        string DetailsPaneId, params string[] BottomPaneIds)
    {
        var Layout = CreatePreviewLayout(PrimaryPaneId, DetailsPaneId, BottomPaneIds);
        Layout.MainHost.Root.First!.Ratio = 0.62f;
        return Layout;
    }

    public static DockNodeState Tabs(string NodeId, string GroupId,
        params string[] PaneIds)
    {
        if (PaneIds.Length == 0)
        {
            throw new ArgumentException("A Dock tab group requires at least one pane",
                nameof(PaneIds));
        }

        return new DockNodeState
        {
            NodeId = NodeId,
            Kind = DockNodeKind.Tabs,
            TabGroup = new DockTabGroupState
            {
                GroupId = GroupId,
                ActivePaneId = PaneIds[0],
                Panes = PaneIds.Select(PaneId => new DockPaneState
                {
                    PaneId = PaneId,
                }).ToList(),
            },
        };
    }

    public static string Field(string Label, string Value, string Action,
        bool ReadOnly, string? Hint = null)
    {
        var Disabled = ReadOnly ? " disabled=\"disabled\"" : string.Empty;
        var Markup = new StringBuilder("<div class=\"asset-field\"><label>")
            .Append(Escape(Label)).Append("</label><input value=\"")
            .Append(EscapeAttribute(Value)).Append("\" data-change-action=\"")
            .Append(EscapeAttribute(Action)).Append("\"").Append(Disabled)
            .Append(" />");
        if (!string.IsNullOrWhiteSpace(Hint))
        {
            Markup.Append("<span class=\"asset-field-hint\">")
                .Append(Escape(Hint)).Append("</span>");
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }

    public static string ReadOnlyField(string Label, string Value) =>
        $"<div class=\"asset-field\"><label>{Escape(Label)}</label><span>{Escape(Value)}</span></div>";

    public static string Source(string Value)
    {
        var Markup = new StringBuilder("<div class=\"asset-source-view readonly\">");
        var Lines = Value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        const int MaximumRenderedLines = 120;
        var RenderedLineCount = Math.Min(Lines.Length, MaximumRenderedLines);
        for (var Index = 0; Index < RenderedLineCount; Index++)
        {
            var DisplayLine = Lines[Index].Length <= 180 ? Lines[Index] :
                Lines[Index][..180] + " ...";
            Markup.Append("<div class=\"asset-source-page asset-source-readonly-line\">")
                .Append((Index + 1).ToString().PadLeft(4)).Append("  ")
                .Append(Escape(DisplayLine)).Append("</div>");
        }
        if (Lines.Length > RenderedLineCount)
        {
            Markup.Append("<div class=\"asset-source-truncated\">")
                .Append((Lines.Length - RenderedLineCount).ToString())
                .Append(" additional lines are omitted from the UI preview</div>");
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }

    public static string Diagnostics(IReadOnlyList<AssetDocumentDiagnostic> Values)
    {
        if (Values.Count == 0)
        {
            return "<div class=\"ui-editor-empty\">No diagnostics</div>";
        }

        var Markup = new StringBuilder("<div class=\"asset-diagnostics\">");
        foreach (var Value in Values)
        {
            Markup.Append("<div class=\"ui-diagnostic ui-diagnostic-")
                .Append(Escape(Value.Severity)).Append("\"><strong>")
                .Append(Escape(Value.Code)).Append("</strong><span>")
                .Append(Escape(Value.Message)).Append("</span></div>");
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }

    public static string Escape(string Value) => WebUtility.HtmlEncode(Value);

    public static string EscapeAttribute(string Value) => WebUtility.HtmlEncode(Value);
}
