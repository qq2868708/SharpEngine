using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    public int OpenUiDocumentCount => AssetOpeners.DescribeEditors("ui-editor").Count;

    public string? ActiveUiDocumentId
    {
        get
        {
            var Editors = AssetOpeners.DescribeEditors("ui-editor");
            return Editors.Count == 1 ? Editors[0]["assetId"]?.GetValue<string>() : null;
        }
    }

    public string DescribeUiEditorJson() => DescribeUiEditorState().ToJsonString(
        new JsonSerializerOptions
        {
            WriteIndented = true,
        });

    private JsonObject DescribeUiEditorState()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 3,
            ["editors"] = new JsonArray(AssetOpeners.DescribeEditors("ui-editor")
                .Select(Editor => (JsonNode)Editor).ToArray()),
        };
    }

    private bool HandleUiEditorAction(string Action) => false;

    private void RefreshUiEditor()
    {
    }
}
