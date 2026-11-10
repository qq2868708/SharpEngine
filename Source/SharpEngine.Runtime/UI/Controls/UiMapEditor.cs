using System.Globalization;

namespace SharpEngine.UI;

/// <summary>Presentation and action bindings only; map values and view order belong to each consumer.</summary>
public sealed record UiMapEntry(string Id, string KeyMarkup, string ValueMarkup,
    string RemoveAction, string MoveUpAction = "", string MoveDownAction = "");

public static class UiMapEditor
{
    private static readonly UiLayoutTemplate Layout = UiLayoutTemplate.LoadEmbedded(
        typeof(UiMapEditor).Assembly, "SharpEngine.UI.MapEditor.rml");
    private static readonly UiLayoutTemplate EntryLayout = UiLayoutTemplate.LoadEmbedded(
        typeof(UiMapEditor).Assembly, "SharpEngine.UI.MapEntry.rml");

    public static string Render(string Id, string Label, IReadOnlyList<UiMapEntry> Entries,
        string ToggleAction, string AddAction, bool Expanded = true, bool ReadOnly = false,
        bool IsNull = false)
    {
        if (string.IsNullOrWhiteSpace(Id) || Entries.Any(Entry => string.IsNullOrWhiteSpace(Entry.Id)) ||
            Entries.Select(Entry => Entry.Id).Distinct(StringComparer.Ordinal).Count() != Entries.Count)
        {
            throw new ArgumentException("UI_MAP_ID: unique stable entry IDs are required");
        }
        var Rows = new List<string>();
        if (Expanded)
        {
            for (var Index = 0; Index < Entries.Count; Index++)
            {
                var Entry = Entries[Index];
                var RemoveDisabled = ReadOnly || Entry.RemoveAction.Length == 0;
                var UpDisabled = Index == 0 || Entry.MoveUpAction.Length == 0;
                var DownDisabled = Index + 1 == Entries.Count || Entry.MoveDownAction.Length == 0;
                Rows.Add(EntryLayout.Render(default, new System.Text.Json.Nodes.JsonObject
                {
                    ["Id"] = Entry.Id,
                    ["RemoveAction"] = RemoveDisabled ? string.Empty : Entry.RemoveAction,
                    ["UpAction"] = UpDisabled ? string.Empty : Entry.MoveUpAction,
                    ["DownAction"] = DownDisabled ? string.Empty : Entry.MoveDownAction,
                    ["RemoveDisabled"] = RemoveDisabled,
                    ["UpDisabled"] = UpDisabled,
                    ["DownDisabled"] = DownDisabled,
                }, new Dictionary<string, IUiLayoutPart>
                {
                    ["Key"] = new MarkupPart(DisableMarkup(Entry.KeyMarkup, ReadOnly)),
                    ["Value"] = new MarkupPart(DisableMarkup(Entry.ValueMarkup, ReadOnly)),
                }));
            }
        }
        var AddDisabled = ReadOnly || AddAction.Length == 0;
        return Layout.Render(default, new System.Text.Json.Nodes.JsonObject
        {
            ["Id"] = Id,
            ["Label"] = Label,
            ["CountText"] = IsNull ? "null" : Entries.Count.ToString(CultureInfo.InvariantCulture),
            ["StateClass"] = ReadOnly ? "se-map-disabled" : string.Empty,
            ["ToggleAction"] = ToggleAction,
            ["Expanded"] = Expanded,
            ["ExpandedState"] = Expanded ? "true" : "false",
            ["ToggleIcon"] = "sharpengine-resource://engine/Resources/Editor/Icons/" +
                (Expanded ? "chevron-down" : "chevron-right") + ".tga",
            ["AddAction"] = AddDisabled ? string.Empty : AddAction,
            ["AddDisabled"] = AddDisabled,
            ["Empty"] = Entries.Count == 0,
            ["EmptyText"] = IsNull ? "Null map" : "Empty map",
        }, new Dictionary<string, IUiLayoutPart>
        {
            ["Entries"] = new MarkupPart(string.Concat(Rows)),
        });
    }

    private static string DisableMarkup(string Markup, bool Disabled)
    {
        if (!Disabled || Markup.Length == 0)
        {
            return Markup;
        }
        var Root = System.Xml.Linq.XElement.Parse("<div>" + Markup + "</div>");
        foreach (var Element in Root.Descendants())
        {
            if (Element.Ancestors().Any(Ancestor =>
                    Ancestor.Attribute("class")?.Value.Split(' ').Contains("se-map") == true))
            {
                continue;
            }
            if (Element.Attribute("data-color-picker") is not null)
            {
                Element.SetAttributeValue("data-color-readonly", "true");
                Element.SetAttributeValue("data-color-prefix", string.Empty);
            }
            if (Element.Name.LocalName is "input" or "select" or "textarea" or "button")
            {
                Element.SetAttributeValue("disabled", "disabled");
                Element.SetAttributeValue("aria-disabled", "true");
                Element.SetAttributeValue("tabindex", "-1");
                foreach (var Attribute in new[]
                {
                    "data-action", "action", "data-change-action", "data-value-commit", "data-color-command",
                })
                {
                    Element.Attribute(Attribute)?.Remove();
                }
            }
        }
        return string.Concat(Root.Nodes().Select(Node => Node.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)));
    }

    private sealed record MarkupPart(string Markup) : IUiLayoutPart
    {
        public string BuildLayout(UiLayoutContext Context) { return Markup; }
    }
}
