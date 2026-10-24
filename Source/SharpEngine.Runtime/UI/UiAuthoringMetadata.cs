using System.Xml.Linq;

namespace SharpEngine.UI;

/// <summary>Literal authoring intent carried by RML. Layout is still evaluated from RCSS.</summary>
public static class UiAuthoringMetadata
{
    public static bool IsAttribute(string Name) => Name is
        "data-ui-name" or "data-ui-layout" or "data-ui-align-x" or "data-ui-align-y";

    public static void Validate(XElement Element)
    {
        var Layout = (string?)Element.Attribute("data-ui-layout");
        if (Layout is not null && Layout is not ("free" or "horizontal" or "vertical" or "overlay"))
        {
            throw new InvalidDataException("UI_LAYOUT_MODE_INVALID: " + Layout);
        }
        foreach (var Axis in new[] { "x", "y" })
        {
            var Alignment = (string?)Element.Attribute("data-ui-align-" + Axis);
            if (Alignment is not null && Alignment is not ("start" or "center" or "end"))
            {
                throw new InvalidDataException("UI_LAYOUT_ALIGNMENT_INVALID: " + Alignment);
            }
        }
    }
}
