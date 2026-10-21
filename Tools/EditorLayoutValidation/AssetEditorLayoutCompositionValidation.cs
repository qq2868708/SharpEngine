using System.Text.Json.Nodes;
using SharpEngine.Editor.Layout;
using SharpEngine.UI;

internal static class AssetEditorLayoutCompositionValidation
{
    internal static void Run(string Root, Action<bool, string> Check)
    {
        var Source = File.ReadAllText(Path.Combine(Root, "Content/UI/Rml/SkeletalAnimationEditor.rml"));
        var Definition = AssetEditorLayoutDefinition.Parse(Source);
        Check(Definition.PrimaryPaneId == "preview" && Definition.Panes.Count == 3 &&
            !Definition.Panes.Single(Pane => Pane.PaneId == "preview").Closable,
            "The dedicated editor's three panes and primary boundary come from its layout file");
        var First = Definition.CreateDockLayout();
        var Second = Definition.CreateDockLayout();
        First.MainHost.Root.Ratio = 0.3f;
        Check(Second.MainHost.Root.Ratio == 0.74f && Definition.CreateDockLayout().MainHost.Root.Ratio == 0.74f,
            "Two editors share a definition but never a mutable Dock instance");
        var Values = new JsonObject
        {
            ["AnimationName"] = "Animation <&>",
            ["TimelineWidth"] = 640,
            ["TimelineHeight"] = 260,
        };
        var Parts = new Dictionary<string, IUiLayoutPart>
        {
            ["Viewport"] = new ChildLayout("viewport"),
            ["Timeline"] = new ChildLayout("timeline"),
        };
        Check(Definition.RenderPane("preview", new(640, 480), Values, Parts).Contains("viewport") &&
            Definition.RenderPane("timeline", new(640, 480), new JsonObject
            {
                ["TimelineWidth"] = 640,
                ["TimelineHeight"] = 260,
                ["SelectedNotify"] = "Notify",
            }, Parts).Contains("timeline"), "The editor layout embeds the referenced UI capabilities");
        void Reject(string Invalid, string Code)
        {
            var Rejected = false;
            try
            {
                AssetEditorLayoutDefinition.Parse(Invalid);
            }
            catch (InvalidDataException Error)
            {
                Rejected = Error.Message.Contains(Code, StringComparison.Ordinal);
            }
            Check(Rejected, Code + " is explicit");
        }
        Reject(Source.Replace("schema-version=\"1\"", "schema-version=\"2\"", StringComparison.Ordinal), "ASSET_LAYOUT_VERSION_INVALID");
        Reject(Source.Replace("ratio=\"0.74\"", "ratio=\"NaN\"", StringComparison.Ordinal), "ASSET_LAYOUT_SPLIT_INVALID");
        Reject(Source.Replace("<tab pane=\"details\" />", "<tab pane=\"preview\" />", StringComparison.Ordinal), "ASSET_LAYOUT_TAB_INVALID");
    }

    private sealed class ChildLayout(string Name) : IUiLayoutPart
    {
        public string BuildLayout(UiLayoutContext Context) { return "<span>" + Name + "</span>"; }
    }
}
