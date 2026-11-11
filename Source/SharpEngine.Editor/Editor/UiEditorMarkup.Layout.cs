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
            var ContainerControls = new StringBuilder("<div class=\"ui-layout-options\">");
            foreach (var Option in new[]
                {
                    "free", "horizontal", "vertical", "overlay"
                })
            {
                LayoutButton(ContainerControls, "uiLayoutMode-" + Option, "editor.ui-layout:" + Option,
                    Option, Mode == Option, !Document.IsReadOnly);
            }
            ContainerControls.Append("</div>");
            Builder.Append(SharpEngine.UI.UiValueEditors.Field("Container / " + Mode, ContainerControls.ToString()));
            if (Mode is "horizontal" or "vertical")
            {
                Builder.Append(SharpEngine.UI.UiValueEditors.Text("Spacing between children",
                    new SharpEngine.UI.UiValueInput("uiLayoutValue-gap", Style.GetValueOrDefault("gap")?.Value ?? "0px", "editor.ui-style:gap:", Document.IsReadOnly)));
                var DistributionControls = new StringBuilder("<div class=\"ui-layout-options\">");
                foreach (var Option in new[]
                {
                    "flex-start", "center", "flex-end", "space-between"
                })
                {
                    LayoutButton(DistributionControls, "uiJustify-" + Option, "editor.ui-style:justify-content:" + Option,
                        Option.Replace("flex-", string.Empty, StringComparison.Ordinal), Style.GetValueOrDefault("justify-content")?.Value == Option, !Document.IsReadOnly);
                }
                DistributionControls.Append("</div>");
                Builder.Append(SharpEngine.UI.UiValueEditors.Field("Main-axis distribution", DistributionControls.ToString()));
            }
        }
        Builder.Append("<div class=\"ui-layout-parent\">Parent layout: ").Append(Escape(Parent)).Append("</div>");
        foreach (var Horizontal in new[]
                {
                    true, false
                })
        {
            var Axis = Horizontal ? "width" : "height";
            var AxisMode = Document.AxisMode(Element, Horizontal);
            var AxisControls = new StringBuilder("<div class=\"ui-layout-options\">");
            foreach (var Option in new[]
                {
                    "fixed", "content", "fill", "percent"
                })
            {
                LayoutButton(AxisControls, "uiSize-" + Axis + "-" + Option, "editor.ui-size:" + Axis + ":" + Option,
                    Option == "percent" ? "%" : Option, AxisMode == Option, !Document.IsReadOnly && (Option != "fill" || Parent != "custom"));
            }
            AxisControls.Append("</div>");
            var Value = Style.GetValueOrDefault(Axis)?.Value ?? "auto";
            AxisControls.Append("<div class=\"ui-layout-value\">")
                .Append(SharpEngine.UI.UiValueEditors.Input(new SharpEngine.UI.UiValueInput("uiLayoutValue-" + Axis, Value,
                    "editor.ui-style:" + Axis + ":", Document.IsReadOnly || AxisMode is "content" or "fill")))
                .Append("<span class=\"ui-layout-measured\" id=\"uiLayoutPixels-").Append(Axis).Append("\"></span></div>");
            var Cross = Horizontal ? Parent == "vertical" : Parent == "horizontal";
            AxisControls.Append("<div class=\"ui-layout-options\">");
            foreach (var Alignment in new[]
                {
                    "start", "center", "end"
                })
            {
                LayoutButton(AxisControls, "uiAlign-" + Axis + "-" + Alignment, "editor.ui-align:" + Axis + ":" + Alignment,
                    Alignment, Document.AlignmentOf(Element, Horizontal) == Alignment,
                    !Document.IsReadOnly && (Cross || Parent is "free" or "overlay"));
            }
            AxisControls.Append("</div>");
            Builder.Append(SharpEngine.UI.UiValueEditors.Field(Horizontal ? "Width" : "Height", AxisControls.ToString()));
        }
        Builder.Append("<div class=\"ui-layout-note\">Resize changes only that axis. Fill follows the parent. Drag overlay items to edit slot spacing.</div>");
        if (Parent is "free" or "overlay")
        {
            Builder.Append("<div class=\"ui-inspector-label\">Edges / relative to positioning container</div>");
            foreach (var Side in new[]
                {
                    "top", "right", "bottom", "left"
                })
            {
                Builder.Append(SharpEngine.UI.UiValueEditors.Text(Side, new SharpEngine.UI.UiValueInput(
                    "uiLayoutValue-" + Side, Style.GetValueOrDefault(Side)?.Value ?? "auto", "editor.ui-style:" + Side + ":", Document.IsReadOnly)));
            }
        }
        foreach (var Group in new[]
                {
                    "margin", "padding"
                })
        {
            Builder.Append("<div class=\"ui-inspector-label\">").Append(Group == "margin" ? "Slot spacing" : "Content padding")
                .Append("</div>");
            foreach (var Side in new[]
                {
                    "top", "right", "bottom", "left"
                })
            {
                var Name = Group + "-" + Side;
                Builder.Append(SharpEngine.UI.UiValueEditors.Text(Side, new SharpEngine.UI.UiValueInput(
                    "uiLayoutValue-" + Name, Document.LayoutStyleValue(Element, Name), "editor.ui-style:" + Name + ":", Document.IsReadOnly)));
            }
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
