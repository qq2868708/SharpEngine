using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Collections.ObjectModel;

namespace SharpEngine.UI;

public sealed record UiTreeRow(string Key, string Name, string Detail, string Icon, int Depth,
    bool Selected, bool HasChildren, bool Expanded, string SelectAction, string ToggleAction,
    string ElementId, string ToggleId, bool Enabled = true, bool ToggleEnabled = true,
    IReadOnlyDictionary<string, string>? Attributes = null, string SelectClass = "", string RowClass = "");

/// <summary>Domain-independent tree presentation and projection, shared by all editor trees.</summary>
public sealed class UiTreeView : IUiLayoutPart
{
    private static readonly UiLayoutTemplate Layout = UiLayoutTemplate.LoadEmbedded(typeof(UiTreeView).Assembly, "SharpEngine.UI.TreeView.rml");
    private IReadOnlyList<UiTreeRow> Rows;
    private string? CachedMarkup;

    public UiTreeView(IReadOnlyList<UiTreeRow> Rows) { this.Rows = Snapshot(Rows); }

    private static IReadOnlyList<UiTreeRow> Snapshot(IReadOnlyList<UiTreeRow> Values)
    {
        return Values.Select(Row => Row with
        {
            Attributes = Row.Attributes is null ? null : new ReadOnlyDictionary<string, string>(
                Row.Attributes.ToDictionary(Pair => Pair.Key, Pair => Pair.Value, StringComparer.Ordinal)),
        }).ToArray();
    }

    public void SetRows(IReadOnlyList<UiTreeRow> Values)
    {
        var Equal = Values.Count == Rows.Count;
        for (var Index = 0; Equal && Index < Values.Count; Index++)
        {
            var Left = Rows[Index];
            var Right = Values[Index];
            Equal = Left.Key == Right.Key && Left.Name == Right.Name && Left.Detail == Right.Detail && Left.Icon == Right.Icon &&
                Left.Depth == Right.Depth && Left.Selected == Right.Selected && Left.HasChildren == Right.HasChildren && Left.Expanded == Right.Expanded &&
                Left.SelectAction == Right.SelectAction && Left.ToggleAction == Right.ToggleAction && Left.ElementId == Right.ElementId &&
                Left.ToggleId == Right.ToggleId && Left.Enabled == Right.Enabled && Left.ToggleEnabled == Right.ToggleEnabled &&
                Left.SelectClass == Right.SelectClass && Left.RowClass == Right.RowClass &&
                (Left.Attributes is null && Right.Attributes is null || Left.Attributes is not null && Right.Attributes is not null &&
                    Left.Attributes.Count == Right.Attributes.Count && Left.Attributes.All(Pair => Right.Attributes.GetValueOrDefault(Pair.Key) == Pair.Value));
        }
        if (!Equal)
        {
            Rows = Snapshot(Values);
            CachedMarkup = null;
        }
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["rows"] = new JsonArray(Rows.Select(Row => (JsonNode)new JsonObject
            {
                ["key"] = Row.Key,
                ["depth"] = Row.Depth,
                ["selected"] = Row.Selected,
                ["hasChildren"] = Row.HasChildren,
                ["expanded"] = Row.Expanded,
                ["enabled"] = Row.Enabled,
                ["elementId"] = Row.ElementId,
                ["selectAction"] = Row.SelectAction,
                ["toggleAction"] = Row.ToggleAction,
            }).ToArray()),
        };
    }

    public string BuildLayout(UiLayoutContext Context)
    {
        if (CachedMarkup is not null)
        {
            return CachedMarkup;
        }
        var Values = new JsonArray();
        foreach (var Row in Rows)
        {
            var Depth = Math.Max(0, Row.Depth);
            var Guides = new JsonArray();
            for (var Level = Math.Max(0, Depth - 32); Level < Depth; Level++)
            {
                Guides.Add(new JsonObject
                {
                    ["Left"] = 13 + Level * 18,
                });
            }
            Values.Add(new JsonObject
            {
                ["Name"] = Row.Name,
                ["Detail"] = Row.Detail,
                ["Icon"] = Row.Icon,
                ["RowClass"] = (Row.Selected ? "selected " : string.Empty) + Row.RowClass,
                ["SelectClass"] = (Row.Selected ? "selected " : string.Empty) + Row.SelectClass,
                ["Guides"] = Guides,
                ["Indent"] = 4 + Depth * 18,
                ["IconLeft"] = 26 + Depth * 18,
                ["TextLeft"] = 48 + Depth * 18,
                ["Level"] = Depth + 1,
                ["HasChildren"] = Row.HasChildren,
                ["ToggleId"] = Row.ToggleId,
                ["ToggleAction"] = Row.ToggleAction,
                ["ToggleDisabled"] = !Row.ToggleEnabled,
                ["ToggleTitle"] = Row.Expanded ? "Collapse branch" : "Expand branch",
                ["ExpandedState"] = Row.Expanded ? "true" : "false",
                ["ToggleIcon"] = "sharpengine-resource://engine/Resources/Editor/Icons/" + (Row.Expanded ? "chevron-down" : "chevron-right") + ".tga",
                ["ElementId"] = Row.ElementId,
                ["SelectAction"] = Row.SelectAction,
                ["Disabled"] = !Row.Enabled,
                ["Tooltip"] = Row.Name + " | " + Row.Detail,
            });
        }
        var Markup = Layout.Render(Context, new JsonObject
        {
            ["MinimumWidth"] = 184 + (Rows.Count == 0 ? 0 : Rows.Max(Row => Row.Depth)) * 18,
            ["Rows"] = Values,
        });
        // Adapters may attach UI hit-test/context/drag metadata without changing the shared row layout.
        var Root = XElement.Parse(Markup);
        var ById = Rows.ToDictionary(Row => Row.ElementId, StringComparer.Ordinal);
        foreach (var Element in Root.Descendants("button"))
        {
            var Id = Element.Attribute("id")?.Value;
            if (Id is null || !ById.ContainsKey(Id))
            {
                continue;
            }
            foreach (var Attribute in ById[Id].Attributes ?? new Dictionary<string, string>())
            {
                Element.SetAttributeValue(Attribute.Key, Attribute.Value);
            }
        }
        CachedMarkup = Root.ToString(SaveOptions.DisableFormatting);
        return CachedMarkup;
    }
}
