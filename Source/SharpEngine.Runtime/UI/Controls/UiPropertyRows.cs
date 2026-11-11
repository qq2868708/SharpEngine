using System.Globalization;
using System.Xml.Linq;

namespace SharpEngine.UI;

// Nest names within a shared column; containers never inset their value column.
internal static class UiPropertyRows
{
    internal static string Indent(string Markup)
    {
        if (Markup.Length == 0)
        {
            return Markup;
        }
        var Root = XElement.Parse("<div>" + Markup + "</div>");
        foreach (var Element in Root.Descendants())
        {
            var IsName = Element.Attribute("class")?.Value.Split(' ').Contains("se-property-name") == true;
            if (!IsName)
            {
                continue;
            }
            var Depth = int.Parse(Element.Attribute("data-property-depth")?.Value ?? "0", CultureInfo.InvariantCulture) + 1;
            Element.SetAttributeValue("data-property-depth", Depth);
            Element.SetAttributeValue("style", "padding-left:" + (8 + Depth * 12).ToString(CultureInfo.InvariantCulture) + "px;");
        }
        return string.Concat(Root.Nodes().Select(Node => Node.ToString(SaveOptions.DisableFormatting)));
    }
}
