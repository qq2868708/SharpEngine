using System.Globalization;
using System.Xml.Linq;

namespace SharpEngine.Editor;

internal enum UiControlPropertyKind
{
    Text,
    Number,
    Boolean,
    Content,
    Style,
}

internal sealed record UiControlProperty(string Name, string Label,
    UiControlPropertyKind Kind, string DefaultValue = "");

internal sealed record UiControlDefinition(string Id, string Label, string Group, string Icon,
    string DefaultRml, IReadOnlyList<UiControlProperty> Properties)
{
    internal XElement Create(string ElementId)
    {
        var Element = XElement.Parse(DefaultRml);
        Element.SetAttributeValue("id", ElementId);
        Element.SetAttributeValue("data-ui-name", Label);
        Element.SetAttributeValue("class", "se-control se-control-" + Id);
        var BoxStyle = (string?)Element.Attribute("style") ?? string.Empty;
        Element.SetAttributeValue("style", BoxStyle + " box-sizing: border-box;");
        if (Id is "container" or "horizontal" or "vertical")
        {
            Element.SetAttributeValue("data-ui-layout", Id == "container" ? "free" : Id);
            var Style = (string?)Element.Attribute("style") ?? string.Empty;
            Element.SetAttributeValue("style", Style + " position: relative;");
        }
        return Element;
    }
}

/// <summary>One authoring vocabulary for creation, tree labels and control properties.
/// Definitions create fresh RML; runtime controls continue to be RmlUi elements.</summary>
internal static class UiControlCatalog
{
    internal const string DefaultStyleId = "engine://ui/basic-controls-style";
    private static readonly UiControlProperty Gap = new("gap", "Spacing", UiControlPropertyKind.Style, "8px");
    private static readonly UiControlProperty Text = new("text", "Text", UiControlPropertyKind.Content);
    private static readonly UiControlProperty Value = new("value", "Value", UiControlPropertyKind.Number, "0");
    private static readonly UiControlProperty Max = new("max", "Maximum", UiControlPropertyKind.Number, "100");

    internal static IReadOnlyList<UiControlDefinition> All
    {
        get;
    } = Array.AsReadOnly(new[]
    {
        new UiControlDefinition("container", "Container", "Layout", "control-container",
            "<div style='display: block; min-width: 120px; min-height: 48px;' />", []),
        new UiControlDefinition("horizontal", "Horizontal Layout", "Layout", "control-horizontal",
            "<div style='display: flex; flex-direction: row; gap: 8px; min-height: 48px;' />", [Gap]),
        new UiControlDefinition("vertical", "Vertical Layout", "Layout", "control-vertical",
            "<div style='display: flex; flex-direction: column; gap: 8px; min-height: 48px;' />", [Gap]),
        new UiControlDefinition("text", "Text", "Content", "control-text", "<span>Text</span>", [Text]),
        new UiControlDefinition("image", "Image", "Content", "texture",
            "<img src='sharpengine-resource://engine/Resources/Editor/Icons/texture.tga' style='width: 64px; height: 64px;' />",
            [new UiControlProperty("src", "Image source", UiControlPropertyKind.Text)]),
        new UiControlDefinition("button", "Button", "Interaction", "control-button", "<button>Button</button>", [Text]),
        new UiControlDefinition("text-input", "Text Input", "Interaction", "control-input",
            "<input type='text' value='' style='width: 180px; height: 30px;' />",
            [new UiControlProperty("value", "Initial text", UiControlPropertyKind.Text)]),
        new UiControlDefinition("checkbox", "Checkbox", "Interaction", "control-checkbox",
            "<input type='checkbox' value='on' style='width: 22px; height: 22px;' />",
            [new UiControlProperty("checked", "Initially checked", UiControlPropertyKind.Boolean, "false"),
                new UiControlProperty("value", "Submitted value", UiControlPropertyKind.Text, "on")]),
        new UiControlDefinition("slider", "Slider", "Interaction", "control-slider",
            "<input type='range' min='0' max='100' step='1' value='50' style='width: 180px; height: 24px;' />",
            [Value, new UiControlProperty("min", "Minimum", UiControlPropertyKind.Number, "0"), Max,
                new UiControlProperty("step", "Step", UiControlPropertyKind.Number, "1")]),
        new UiControlDefinition("progress", "Progress Bar", "Content", "control-progress",
            "<progress value='50' max='100' style='width: 180px; height: 18px;' />",
            [Value, new UiControlProperty("max", "Maximum", UiControlPropertyKind.Number, "1")]),
    });

    internal static UiControlDefinition? Find(string Id) => All.FirstOrDefault(Control => Control.Id == Id);

    internal static string CommandId(UiControlDefinition Control) => "add-" + (Control.Id switch
    {
        "container" => "div",
        "text" => "span",
        "text-input" => "input",
        "image" => "img",
        _ => Control.Id,
    });

    internal static UiControlDefinition? ForCommand(string Id) =>
        All.FirstOrDefault(Control => CommandId(Control) == Id);

    internal static UiControlDefinition? Identify(XElement Element, UiStyleSheet Sheet)
    {
        var Id = Element.Name.LocalName.ToLowerInvariant() switch
        {
            "div" or "section" or "panel" or "form" => "container",
            "span" or "label" or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => "text",
            "img" => "image",
            "button" => "button",
            "progress" => "progress",
            "input" => ((string?)Element.Attribute("type") ?? "text").ToLowerInvariant() switch
            {
                "checkbox" => "checkbox",
                "range" => "slider",
                "text" or "password" => "text-input",
                _ => string.Empty,
            },
            _ => string.Empty,
        };
        if (Id == "container")
        {
            var Style = Sheet.Compute(Element);
            var Display = Style.GetValueOrDefault("display")?.Value;
            if (Display == "flex")
            {
                var Direction = Style.GetValueOrDefault("flex-direction")?.Value ?? "row";
                Id = Direction.StartsWith("column", StringComparison.Ordinal) ? "vertical" : "horizontal";
            }
        }
        return Find(Id);
    }

    internal static string Read(XElement Element, UiControlProperty Property, UiStyleSheet Sheet)
    {
        return Property.Kind switch
        {
            UiControlPropertyKind.Content => Element.HasElements ? string.Empty : Element.Value,
            UiControlPropertyKind.Boolean => Element.Attribute(Property.Name) is null ? "false" : "true",
            UiControlPropertyKind.Style => Sheet.Compute(Element).GetValueOrDefault(Property.Name)?.Value ?? Property.DefaultValue,
            _ => (string?)Element.Attribute(Property.Name) ?? Property.DefaultValue,
        };
    }

    internal static bool ValidateNumbers(XElement Element, string ControlId, out string Message)
    {
        Message = string.Empty;
        if (ControlId is not ("slider" or "progress"))
        {
            return true;
        }
        var Values = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var Property in Find(ControlId)!.Properties.Where(Property => Property.Kind == UiControlPropertyKind.Number))
        {
            var Text = (string?)Element.Attribute(Property.Name) ?? Property.DefaultValue;
            if (UiEditorDocument.HasPlaceholder(Text))
            {
                continue;
            }
            var Parsed = float.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var Number);
            if (!Parsed || !float.IsFinite(Number))
            {
                Message = Property.Label + " must be a finite number";
                return false;
            }
            Values[Property.Name] = Number;
        }
        if ((Values.ContainsKey("step") && Values["step"] <= 0) ||
            (ControlId == "progress" && Values.ContainsKey("max") && Values["max"] <= 0) ||
            (Values.ContainsKey("min") && Values.ContainsKey("max") && Values["min"] >= Values["max"]))
        {
            Message = "Maximum must exceed minimum; maximum for progress and slider step must be positive";
            return false;
        }
        var Minimum = Values.GetValueOrDefault("min", 0);
        if (Values.ContainsKey("value") &&
            ((ControlId == "progress" || Values.ContainsKey("min")) && Values["value"] < Minimum ||
                Values.ContainsKey("max") && Values["value"] > Values["max"]))
        {
            Message = "Value must remain between minimum and maximum";
            return false;
        }
        return true;
    }
}
