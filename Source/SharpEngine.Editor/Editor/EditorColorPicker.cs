using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    // Picker drafts are transient UI state. Apply uses the same revision-checked
    // component command as RGBA fields and swatches; Cancel has no document edit.
    private void AppendInspectorColorPicker(StringBuilder Markup, string ComponentId, string Path, JsonArray Value)
    {
        var Id = "color-picker-" + ComponentId + "-" + Uri.EscapeDataString(Path);
        var Prefix = "editor.component-field:" + ComponentId + ":" + Uri.EscapeDataString(
            ComponentRevision + "|json|" + Path + "|" + Value.ToJsonString()) + ":";
        var Channels = Value.Select(Channel => Channel!.GetValue<float>()).ToArray();
        var Preview = "rgb(" + string.Join(",", Channels.Take(3).Select(Channel =>
            (int)(Math.Clamp(Channel, 0, 1) * 255))) + ")";
        Markup.Append("<div class=\"color-picker\" id=\"").Append(Id)
            .Append("\" data-color-picker=\"true\" data-color-prefix=\"").Append(Escape(Prefix))
            .Append("\" data-color-value=\"").Append(Escape(Value.ToJsonString())).Append('"');
        for (var Index = 0; Index < 4; Index++)
        {
            Markup.Append(" data-color-").Append("rgba"[Index]).Append("=\"")
                .Append(Channels[Index].ToString("R", CultureInfo.InvariantCulture)).Append('"');
        }
        Markup.Append("><button id=\"").Append(Id).Append("-open\" class=\"color-picker-toggle\" data-color-command=\"open\" title=\"Open color picker\">")
            .Append("<span class=\"color-picker-chip\" id=\"").Append(Id).Append("-chip\" style=\"background-color:").Append(Preview)
            .Append(";\" /><span>Color picker</span>").Append(InspectorIcon("chevron-down")).Append("</button>")
            .Append("<div class=\"color-picker-panel\" id=\"").Append(Id).Append("-panel\">")
            .Append("<div class=\"color-picker-sv\" id=\"").Append(Id).Append("-sv\" data-color-control=\"sv\" title=\"Saturation and brightness\">")
            .Append("<div class=\"color-picker-marker\" id=\"").Append(Id).Append("-marker\" /></div>")
            .Append("<label>Hue</label><div class=\"color-picker-hue\" id=\"").Append(Id).Append("-hue\" data-color-control=\"hue\" title=\"Hue\">")
            .Append("<div class=\"color-picker-track-marker\" id=\"").Append(Id).Append("-hue-marker\" /></div>")
            .Append("<label>Opacity</label><input id=\"").Append(Id).Append("-alpha\" type=\"range\" min=\"0\" max=\"1\" step=\"0.001\" value=\"")
            .Append(Channels[3].ToString("R", CultureInfo.InvariantCulture)).Append("\" data-color-control=\"alpha\" />")
            .Append("<span class=\"color-picker-preview\" id=\"").Append(Id).Append("-preview\" />")
            .Append("<div class=\"color-picker-buttons\"><button id=\"").Append(Id).Append("-cancel\" data-color-command=\"cancel\">Cancel</button>")
            .Append("<button id=\"").Append(Id).Append("-apply\" data-color-command=\"apply\" class=\"color-picker-apply\">Apply</button></div></div></div>");
    }
}
