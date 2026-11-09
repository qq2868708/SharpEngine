using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.Components;
using SharpEngine.UI;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private void AppendComponentCollection(StringBuilder Markup, ComponentRecord Component,
        ComponentField Field, string Path, JsonNode? Value, Type ElementType, string Name)
    {
        var Values = Value as JsonArray ?? new JsonArray();
        var Id = "component-collection-" + Component.ComponentId + "-" + Uri.EscapeDataString(Path);
        var FoldId = Component.ComponentId + "-" + Uri.EscapeDataString(Path);
        var Expanded = !_collapsedFields.Contains(FoldId);
        var Action = "editor.component-list:" + Component.ComponentId + ":" + Uri.EscapeDataString(
            Path + "|" + DefaultElement(ElementType) + "|" + CollectionEditing.Fingerprint(Value)) + ":";
        var Items = new List<UiCollectionItem>();
        for (var Index = 0; Index < Values.Count; Index++)
        {
            var Content = new StringBuilder();
            if (Expanded)
            {
                AppendComponentField(Content, Component, Field with
                {
                    DisplayName = Index.ToString(CultureInfo.InvariantCulture),
                    Minimum = null,
                    Maximum = null,
                }, Path + "/" + Index.ToString(CultureInfo.InvariantCulture), Values[Index], ElementType);
            }
            Items.Add(new UiCollectionItem(Id + "-item-" + Index.ToString(CultureInfo.InvariantCulture),
                Content.ToString(), Action + "remove," + Index, Action + "up," + Index, Action + "down," + Index));
        }
        var Collection = XElement.Parse(UiCollectionEditor.Render(Id, Name, Items,
            "editor.component-fold:" + FoldId, Action + "add", Expanded, IsNull: Value is null));
        var Guard = "editor.component-collection-value:" + Component.ComponentId + ":" +
            Uri.EscapeDataString(Path + "|" + CollectionEditing.Fingerprint(Value)) + ":";
        foreach (var Attribute in Collection.DescendantsAndSelf().Attributes().Where(Attribute =>
            Attribute.Name.LocalName is "data-action" or "data-value-commit" or "data-color-prefix"))
        {
            if (Attribute.Value.StartsWith("editor.component-", StringComparison.Ordinal) &&
                !Attribute.Value.StartsWith("editor.component-fold:", StringComparison.Ordinal))
            {
                Attribute.Value = Guard + Attribute.Value;
            }
        }
        Markup.Append(Collection.ToString(SaveOptions.DisableFormatting));
    }

    private JsonNode? ComponentCollectionValue(string ComponentId, string Path)
    {
        JsonNode? Value = _world!.Data.Entities.SelectMany(Entity => Entity.Components)
            .Single(Component => Component.ComponentId == ComponentId).State;
        foreach (var Segment in Path.Split('/'))
        {
            Value = Value is JsonArray Array ? Array[int.Parse(Segment, CultureInfo.InvariantCulture)] : Value?[Segment];
        }
        return Value;
    }
}
