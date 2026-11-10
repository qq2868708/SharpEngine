using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace SharpEngine.UI;

/// <summary>Presentation and action bindings only. Consumers own values, folding, permissions and transactions.</summary>
public sealed record UiCollectionItem(string Id, string ValueMarkup, string RemoveAction,
    string MoveUpAction = "", string MoveDownAction = "");

public static class UiCollectionEditor
{
    private static readonly UiLayoutTemplate Layout = UiLayoutTemplate.LoadEmbedded(
        typeof(UiCollectionEditor).Assembly, "SharpEngine.UI.CollectionEditor.rml");
    private static readonly UiLayoutTemplate ItemLayout = UiLayoutTemplate.LoadEmbedded(
        typeof(UiCollectionEditor).Assembly, "SharpEngine.UI.CollectionItem.rml");

    public static string Render(string Id, string Label, IReadOnlyList<UiCollectionItem> Items,
        string ToggleAction, string AddAction, bool Expanded = true, bool ReadOnly = false,
        bool IsNull = false)
    {
        if (string.IsNullOrWhiteSpace(Id) || Items.Any(Item => string.IsNullOrWhiteSpace(Item.Id)) ||
            Items.Select(Item => Item.Id).Distinct(StringComparer.Ordinal).Count() != Items.Count)
        {
            throw new ArgumentException("UI_COLLECTION_ID: unique stable control IDs are required");
        }
        var Rows = new List<string>();
        if (Expanded)
        {
            for (var Index = 0; Index < Items.Count; Index++)
            {
                var Item = Items[Index];
                var UpDisabled = ReadOnly || Index == 0 || Item.MoveUpAction.Length == 0;
                var DownDisabled = ReadOnly || Index + 1 == Items.Count || Item.MoveDownAction.Length == 0;
                var RemoveDisabled = ReadOnly || Item.RemoveAction.Length == 0;
                Rows.Add(ItemLayout.Render(default, new JsonObject
                {
                    ["Id"] = Item.Id,
                    ["Index"] = Index,
                    ["UpAction"] = UpDisabled ? string.Empty : Item.MoveUpAction,
                    ["DownAction"] = DownDisabled ? string.Empty : Item.MoveDownAction,
                    ["RemoveAction"] = RemoveDisabled ? string.Empty : Item.RemoveAction,
                    ["UpDisabled"] = UpDisabled,
                    ["DownDisabled"] = DownDisabled,
                    ["RemoveDisabled"] = RemoveDisabled,
                }, new Dictionary<string, IUiLayoutPart>
                {
                    ["Value"] = new MarkupPart(ReadOnly ? DisableValues(Item.ValueMarkup) : Item.ValueMarkup),
                }));
            }
        }
        var AddDisabled = ReadOnly || AddAction.Length == 0;
        return Layout.Render(default, new JsonObject
        {
            ["Id"] = Id,
            ["Label"] = Label,
            ["CountText"] = IsNull ? "null" : Items.Count.ToString(CultureInfo.InvariantCulture),
            ["StateClass"] = ReadOnly ? "se-collection-disabled" : string.Empty,
            ["ToggleAction"] = ToggleAction,
            ["Expanded"] = Expanded,
            ["ExpandedState"] = Expanded ? "true" : "false",
            ["ToggleIcon"] = "sharpengine-resource://engine/Resources/Editor/Icons/" +
                (Expanded ? "chevron-down" : "chevron-right") + ".tga",
            ["AddAction"] = AddDisabled ? string.Empty : AddAction,
            ["AddDisabled"] = AddDisabled,
            ["Empty"] = Items.Count == 0,
            ["EmptyText"] = IsNull ? "Null collection" : "Empty collection",
        }, new Dictionary<string, IUiLayoutPart>
        {
            ["Items"] = new MarkupPart(string.Concat(Rows)),
        });
    }

    private static string DisableValues(string Markup)
    {
        var Root = XElement.Parse("<div>" + Markup + "</div>");
        foreach (var Element in Root.Descendants())
        {
            if (Element.Attribute("data-value-view")?.Value == "true")
            {
                continue;
            }
            if (Element.Attribute("data-color-picker") is not null)
            {
                Element.SetAttributeValue("data-color-readonly", "true");
                Element.SetAttributeValue("data-color-prefix", string.Empty);
            }
            var IsFold = Element.Attribute("class")?.Value.Split(' ').Contains("se-collection-toggle") == true;
            if (Element.Name.LocalName is "input" or "select" or "textarea" or "button" && !IsFold)
            {
                Element.SetAttributeValue("disabled", "disabled");
                Element.SetAttributeValue("aria-disabled", "true");
                Element.SetAttributeValue("tabindex", "-1");
                foreach (var Name in new[]
                {
                    "data-action", "action", "data-change-action", "data-value-commit", "data-color-command",
                })
                {
                    Element.Attribute(Name)?.Remove();
                }
            }
        }
        return string.Concat(Root.Nodes().Select(Node => Node.ToString(SaveOptions.DisableFormatting)));
    }

    private sealed record MarkupPart(string Markup) : IUiLayoutPart
    {
        public string BuildLayout(UiLayoutContext Context) { return Markup; }
    }
}
