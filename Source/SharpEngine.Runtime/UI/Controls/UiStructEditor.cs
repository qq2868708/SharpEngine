using System.Text.Json.Nodes;

namespace SharpEngine.UI;

/// <summary>A reusable expandable field group. The consumer owns its members, expansion and edits.</summary>
public static class UiStructEditor
{
    private static readonly UiLayoutTemplate Layout = UiLayoutTemplate.LoadEmbedded(
        typeof(UiStructEditor).Assembly, "SharpEngine.UI.StructEditor.rml");

    public static string Render(string Id, string Label, string TypeName,
        string Content, string ToggleAction, bool Expanded = true, bool ReadOnly = false)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("UI_STRUCT_ID: a stable group ID is required");
        }
        return Layout.Render(default, new JsonObject
        {
            ["Id"] = Id,
            ["Label"] = Label,
            ["TypeName"] = TypeName,
            ["Expanded"] = Expanded,
            ["ExpandedState"] = Expanded ? "true" : "false",
            ["StateClass"] = ReadOnly ? "se-struct-disabled" : string.Empty,
            ["ToggleAction"] = ToggleAction,
            ["ToggleIcon"] = "sharpengine-resource://engine/Resources/Editor/Icons/" +
                (Expanded ? "chevron-down" : "chevron-right") + ".tga",
        }, new Dictionary<string, IUiLayoutPart>
        {
            ["Members"] = new MembersPart(Content),
        });
    }

    private sealed record MembersPart(string Content) : IUiLayoutPart
    {
        public string BuildLayout(UiLayoutContext Context) { return Content; }
    }
}
