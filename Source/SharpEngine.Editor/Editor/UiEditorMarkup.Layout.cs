using System.Text;

namespace SharpEngine.Editor;

internal static partial class UiEditorMarkup
{
    internal static string BuildLayoutInspector(UiEditorDocument Document)
    {
        var Element = Document.SelectedElement();
        if (Element is null)
        {
            return string.Empty;
        }
        if (Element.Name.LocalName is "ui-slot" or "ui-if" or "ui-repeat")
        {
            return "<div class=\"ui-layout-note\">This composition directive has no layout box. Select a contained control.</div>";
        }
        var Builder = new StringBuilder("<div class=\"ui-layout-editor\"><div class=\"ui-inspector-group-title\">VISUAL LAYOUT</div>");
        var Mode = Document.LayoutOf(Element);
        var Parent = Document.SlotLayoutOf(Element);
        var Style = Document.Sheet.Compute(Element);
        if (Element.Name.LocalName is "div" or "section" or "panel" or "form")
        {
            Builder.Append("<div class=\"ui-inspector-label\">Container / ").Append(Escape(Mode)).Append("</div><div class=\"ui-layout-options\">");
            foreach (var Option in new[] { "free", "horizontal", "vertical", "overlay" })
            {
                LayoutButton(Builder, "uiLayoutMode-" + Option, "editor.ui-layout:" + Option,
                    Option, Mode == Option, !Document.IsReadOnly);
            }
            Builder.Append("</div>");
            if (Mode is "horizontal" or "vertical")
            {
                Builder.Append("<div class=\"ui-inspector-label\">Spacing between children</div><div class=\"ui-layout-value\"><input id=\"uiLayoutValue-gap\" type=\"text\" value=\"")
                    .Append(Escape(Style.GetValueOrDefault("gap")?.Value ?? "0px"))
                    .Append("\" data-change-action=\"editor.ui-style:gap:\"").Append(AppendAvailability(!Document.IsReadOnly)).Append(" /></div>")
                    .Append("<div class=\"ui-inspector-label\">Main-axis distribution</div><div class=\"ui-layout-options\">");
                foreach (var Option in new[] { "flex-start", "center", "flex-end", "space-between" })
                {
                    LayoutButton(Builder, "uiJustify-" + Option, "editor.ui-style:justify-content:" + Option,
                        Option.Replace("flex-", string.Empty, StringComparison.Ordinal), Style.GetValueOrDefault("justify-content")?.Value == Option, !Document.IsReadOnly);
                }
                Builder.Append("</div>");
            }
        }
        Builder.Append("<div class=\"ui-layout-parent\">Parent layout: ").Append(Escape(Parent)).Append("</div>");
        foreach (var Horizontal in new[] { true, false })
        {
            var Axis = Horizontal ? "width" : "height";
            var AxisMode = Document.AxisMode(Element, Horizontal);
            Builder.Append("<div class=\"ui-inspector-label\">").Append(Horizontal ? "Width" : "Height")
                .Append("</div><div class=\"ui-layout-options\">");
            foreach (var Option in new[] { "fixed", "content", "fill", "percent" })
            {
                LayoutButton(Builder, "uiSize-" + Axis + "-" + Option, "editor.ui-size:" + Axis + ":" + Option,
                    Option == "percent" ? "%" : Option, AxisMode == Option, !Document.IsReadOnly && (Option != "fill" || Parent != "custom"));
            }
            Builder.Append("</div>");
            var Value = Style.GetValueOrDefault(Axis)?.Value ?? "auto";
            Builder.Append("<div class=\"ui-layout-value\"><input id=\"uiLayoutValue-").Append(Axis)
                .Append("\" type=\"text\" value=\"").Append(Escape(Value)).Append("\" data-change-action=\"editor.ui-style:")
                .Append(Axis).Append(":\"").Append(AppendAvailability(!Document.IsReadOnly && AxisMode is not ("content" or "fill")))
                .Append(" /><span class=\"ui-layout-measured\" id=\"uiLayoutPixels-").Append(Axis).Append("\"></span></div>");
            var Cross = Horizontal ? Parent == "vertical" : Parent == "horizontal";
            Builder.Append("<div class=\"ui-layout-options\">");
            foreach (var Alignment in new[] { "start", "center", "end" })
            {
                LayoutButton(Builder, "uiAlign-" + Axis + "-" + Alignment, "editor.ui-align:" + Axis + ":" + Alignment,
                    Alignment, Document.AlignmentOf(Element, Horizontal) == Alignment,
                    !Document.IsReadOnly && (Cross || Parent is "free" or "overlay"));
            }
            Builder.Append("</div>");
        }
        Builder.Append("<div class=\"ui-layout-note\">Resize changes only that axis. Fill follows the parent. Drag overlay items to edit slot spacing.</div>");
        if (Parent is "free" or "overlay")
        {
            Builder.Append("<div class=\"ui-inspector-label\">Edges / relative to positioning container</div><div class=\"ui-layout-spacing\">");
            foreach (var Side in new[] { "top", "right", "bottom", "left" })
            {
                Builder.Append("<label>").Append(Side).Append("<input type=\"text\" id=\"uiLayoutValue-").Append(Side)
                    .Append("\" value=\"").Append(Escape(Style.GetValueOrDefault(Side)?.Value ?? "auto"))
                    .Append("\" data-change-action=\"editor.ui-style:").Append(Side).Append(":\"")
                    .Append(AppendAvailability(!Document.IsReadOnly)).Append(" /></label>");
            }
            Builder.Append("</div>");
        }
        foreach (var Group in new[] { "margin", "padding" })
        {
            Builder.Append("<div class=\"ui-inspector-label\">").Append(Group == "margin" ? "Slot spacing" : "Content padding")
                .Append("</div><div class=\"ui-layout-spacing\">");
            foreach (var Side in new[] { "top", "right", "bottom", "left" })
            {
                var Name = Group + "-" + Side;
                Builder.Append("<label>").Append(Side).Append("<input type=\"text\" id=\"uiLayoutValue-").Append(Name)
                    .Append("\" value=\"").Append(Escape(Document.LayoutStyleValue(Element, Name)))
                    .Append("\" data-change-action=\"editor.ui-style:").Append(Name).Append(":\"")
                    .Append(AppendAvailability(!Document.IsReadOnly)).Append(" /></label>");
            }
            Builder.Append("</div>");
        }
        return Builder.Append("</div>").ToString();
    }

    private static void LayoutButton(StringBuilder Builder, string Id, string Action, string Label, bool Active, bool Enabled)
    {
        Builder.Append("<button id=\"").Append(Id).Append("\" class=\"ui-layout-option")
            .Append(Active ? " active" : string.Empty).Append('"').AppendAction(Action, Enabled)
            .Append(AppendAvailability(Enabled)).Append('>').Append(Escape(Label)).Append("</button>");
    }
}
