using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SharpEngine.UI;

/// <summary>UI-only binding metadata; the owning domain retains validation, revision checks and transactions.</summary>
public sealed record UiValueInput(string Id, string Value, string CommitAction, bool ReadOnly = false,
    string Kind = "text", IReadOnlyDictionary<string, string>? Attributes = null);

/// <summary>Reusable value layouts. Transform composes Vector, Rotator and Vector; all axes compose the same scalar input.</summary>
public static class UiValueEditors
{
    private static readonly UiLayoutTemplate FieldLayout = Load("ValueField");
    private static readonly UiLayoutTemplate VectorLayout = Load("VectorEditor");
    private static readonly UiLayoutTemplate AxisLayout = Load("ValueAxis");
    private static readonly UiLayoutTemplate TransformLayout = Load("TransformEditor");

    private static UiLayoutTemplate Load(string Name) { return UiLayoutTemplate.LoadEmbedded(typeof(UiValueEditors).Assembly, "SharpEngine.UI." + Name + ".rml"); }

    public static string Input(UiValueInput Value)
    {
        var Id = Value.Id.Length != 0 ? Value.Id : Value.CommitAction.Length == 0 ? string.Empty :
            "value-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Value.CommitAction)))[..24];
        var Input = new XElement("input", new XAttribute("type", "text"),
            new XAttribute("class", "se-value-input"), new XAttribute("value", Value.Value),
            new XAttribute("data-value-editor", Value.Kind), new XAttribute("data-original-value", Value.Value),
            new XAttribute("data-value-commit", Value.CommitAction));
        if (Id.Length != 0)
        {
            Input.SetAttributeValue("id", Id);
        }
        foreach (var Attribute in Value.Attributes ?? new Dictionary<string, string>())
        {
            Input.SetAttributeValue(Attribute.Key, Attribute.Value);
        }
        if (Value.ReadOnly)
        {
            Input.SetAttributeValue("disabled", "disabled");
            Input.SetAttributeValue("aria-disabled", "true");
            Input.SetAttributeValue("tabindex", "-1");
            Input.Attribute("data-value-commit")?.Remove();
            Input.Attribute("data-change-action")?.Remove();
            Input.Attribute("data-action")?.Remove();
            Input.Attribute("action")?.Remove();
        }
        return Input.ToString(SaveOptions.DisableFormatting);
    }

    public static string Field(string Label, string EditorMarkup, string? Hint = null, string Class = "")
    {
        return FieldCore(Label, EditorMarkup, Hint, Class);
    }

    private static string FieldCore(string Label, string EditorMarkup, string? Hint = null, string Class = "", bool Disabled = false)
    {
        return FieldLayout.Render(default, new JsonObject
        {
            ["Label"] = Label,
            ["Class"] = Class + (Disabled ? " se-value-field-disabled" : string.Empty),
            ["HasHint"] = !string.IsNullOrEmpty(Hint),
            ["Hint"] = Hint ?? string.Empty,
        }, new Dictionary<string, IUiLayoutPart>
        {
            ["Editor"] = new MarkupPart(EditorMarkup),
        });
    }

    public static string Text(string Label, UiValueInput Value, string? Hint = null, string Class = "") { return FieldCore(Label, Input(Value), Hint, Class, Value.ReadOnly); }
    public static string Multiline(string Label, UiValueInput Value, int Rows, string? Hint = null)
    {
        var Element = XElement.Parse(Input(Value));
        Element.Name = "textarea";
        Element.Attribute("type")?.Remove();
        Element.Attribute("value")?.Remove();
        Element.SetAttributeValue("class", "se-value-input se-value-multiline");
        Element.SetAttributeValue("rows", Math.Max(1, Rows));
        Element.Value = Value.Value;
        return FieldCore(Label, Element.ToString(SaveOptions.DisableFormatting), Hint, Disabled: Value.ReadOnly);
    }
    public static string Number(string Label, UiValueInput Value, string? Hint = null, string Class = "")
    {
        var Input = Value with
        {
            Kind = Value.Kind == "integer" ? "integer" : "number",
        };
        return Text(Label, Input, Hint, Class);
    }

    public static string Vector(string Label, IReadOnlyList<UiValueInput> Values, string Axes = "XYZW", string Class = "")
    {
        if (Values.Count < 2 || Values.Count > 4 || Axes.Length < Values.Count)
        {
            throw new ArgumentException("UI_VECTOR_DIMENSION: two to four inputs and matching axis labels are required");
        }
        var Parts = new List<string>();
        for (var Index = 0; Index < Values.Count; Index++)
        {
            Parts.Add(AxisLayout.Render(default, new JsonObject
            {
                ["Axis"] = Axes[Index].ToString(),
                ["AxisClass"] = Values[Index].ReadOnly ? "se-vector-axis-disabled" : string.Empty,
            }, new Dictionary<string, IUiLayoutPart>
            {
                ["Input"] = new MarkupPart(Input(Values[Index] with
                {
                    Kind = "number",
                })),
            }));
        }
        var Vector = VectorLayout.Render(default, new JsonObject
        {
            ["Class"] = Class,
        }, new Dictionary<string, IUiLayoutPart>
        {
            ["Axes"] = new MarkupPart(string.Concat(Parts)),
        });
        return FieldCore(Label, Vector, Disabled: Values.All(Value => Value.ReadOnly));
    }

    public static string Rotator(string Label, IReadOnlyList<UiValueInput> Values) { return Vector(Label, Values, "XYZ", "se-rotator"); }

    public static string Transform(string Label, IReadOnlyList<UiValueInput> Position,
        IReadOnlyList<UiValueInput> Rotation, IReadOnlyList<UiValueInput> Scale)
    {
        return TransformLayout.Render(default, new JsonObject
        {
            ["Label"] = Label,
            ["HasLabel"] = Label.Length != 0,
        }, new Dictionary<string, IUiLayoutPart>
        {
            ["Position"] = new MarkupPart(Vector("Position", Position, "XYZ")),
            ["Rotation"] = new MarkupPart(Rotator("Rotation (degrees)", Rotation)),
            ["Scale"] = new MarkupPart(Vector("Scale", Scale, "XYZ")),
        });
    }

    public static string Choice(string Label, string Id, string Value,
        IEnumerable<(string Id, string Label)> Options, string Action, bool ReadOnly = false)
    {
        return FieldCore(Label, ChoiceInput(Id, Value, Options, Action, ReadOnly), Disabled: ReadOnly);
    }

    public static string ChoiceInput(string Id, string Value,
        IEnumerable<(string Id, string Label)> Options, string Action, bool ReadOnly = false)
    {
        var Select = new XElement("select", new XAttribute("class", "se-value-input"),
            new XAttribute("id", Id), new XAttribute("data-value-choice", "true"), new XAttribute("data-change-action", Action));
        if (ReadOnly)
        {
            Select.SetAttributeValue("disabled", "disabled");
            Select.SetAttributeValue("aria-disabled", "true");
            Select.SetAttributeValue("tabindex", "-1");
            Select.Attribute("data-change-action")?.Remove();
        }
        foreach (var Option in Options)
        {
            var Node = new XElement("option", new XAttribute("value", Option.Id), Option.Label);
            if (Option.Id == Value)
            {
                Node.SetAttributeValue("selected", "selected");
            }
            Select.Add(Node);
        }
        return Select.ToString(SaveOptions.DisableFormatting);
    }

    public static string Boolean(string Label, string Id, bool Value, string Action, bool ReadOnly = false)
    {
        return FieldCore(Label, BooleanInput(Id, Value, Action, ReadOnly), Disabled: ReadOnly);
    }

    public static string BooleanInput(string Id, bool Value, string Action, bool ReadOnly = false)
    {
        var Input = new XElement("input", new XAttribute("type", "checkbox"), new XAttribute("class", "se-value-boolean"), new XAttribute("id", Id), new XAttribute("data-action", Action));
        if (Value)
        {
            Input.SetAttributeValue("checked", "checked");
        }
        if (ReadOnly)
        {
            Input.SetAttributeValue("disabled", "disabled");
            Input.SetAttributeValue("aria-disabled", "true");
            Input.SetAttributeValue("tabindex", "-1");
            Input.Attribute("data-action")?.Remove();
        }
        return Input.ToString(SaveOptions.DisableFormatting);
    }

    public static string Format(double Value) { return Value.ToString("G6", CultureInfo.InvariantCulture); }

    private sealed record MarkupPart(string Markup) : IUiLayoutPart
    {
        public string BuildLayout(UiLayoutContext Context) { return Markup; }
    }
}
