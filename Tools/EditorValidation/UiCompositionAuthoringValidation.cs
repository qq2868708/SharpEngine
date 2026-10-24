using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.Assets;
using SharpEngine.Editor;
using SharpEngine.UI;

namespace SharpEngine.EditorValidation;

internal static class UiCompositionAuthoringValidation
{
    internal static void Run(string Root, Action<bool, string> Check)
    {
        var Folder = Directory.CreateDirectory(Path.Combine(Root, "UiCompositionAuthoring"));
        var ChildPath = Path.Combine(Folder.FullName, "Card.sui");
        const string ChildSource = """
            <template values='{"Id":"card","Title":"Default"}'><div class="card"><span id="${Id}-title">${Title}</span></div></template>
            """;
        File.WriteAllText(Path.Combine(Folder.FullName, "Card.rcss"), ".card span { color: #123456; }");
        File.WriteAllText(ChildPath, UiDocumentAsset.Create("Card", ChildSource, ["Card.rcss"]).Serialize());
        var ChildBytes = File.ReadAllBytes(ChildPath);
        var SourcePath = Path.Combine(Folder.FullName, "Screen.sui");
        const string Source = """
            <template values='{"Caption":"Initial"}'><div id="screen"><span id="caption">${Caption}</span>
            <ui-use id="First" asset="project://ui/card" values='{"Id":"a","Title":"A"}' />
            <ui-use id="Second" asset="project://ui/card" values='{"Id":"b","Title":"B"}' /></div></template>
            """;
        File.WriteAllText(SourcePath, UiDocumentAsset.Create("Screen", Source).Serialize());
        UiLayoutTemplate Resolve(string AssetId)
        {
            if (AssetId != "project://ui/card")
            {
                throw new InvalidDataException("Unknown UI asset: " + AssetId);
            }
            return UiLayoutTemplate.FromAsset(UiDocumentAsset.Load(ChildPath), ChildPath);
        }
        var Asset = new AssetDescriptor
        {
            Id = "project://ui/screen",
            Type = "UiDocument",
            Name = "Screen",
            RelativePath = "Screen.sui",
            Source = AssetSource.Project,
        };
        var Document = UiEditorDocument.Load(Asset, SourcePath, Folder.FullName);
        Document.SetAssetResolver(Resolve);
        Check(!Document.Diagnostics.Any(Diagnostic => Diagnostic.Severity == "error"),
            "Serialized control references resolve without executing project behavior");
        Check(Document.DesignBodyElement!.Descendants("span").Any(Element => Element.Value == "A") &&
            UiEditorMarkup.BuildCanvas(Document).Contains("#123456", StringComparison.Ordinal),
            "The authoring canvas expands child definitions and their RCSS");
        Document.Select("#First");
        Check(Document.SetParameter("Title", "Edited ${Literal} <&>", false, out _),
            "Inspector edits update the current use's serialized parameter");
        var BeforeUndo = Document.CanonicalSource;
        Document.Undo();
        Check(Document.ParametersOf(Document.SelectedElement())["Title"]!.GetValue<string>() == "A",
            "A parameter edit is one reversible authoring transaction");
        Document.Redo();
        Check(Document.CanonicalSource == BeforeUndo &&
            Document.ParametersOf(Document.Source.Descendants("ui-use").Last())["Title"]!.GetValue<string>() == "B",
            "Redo restores one use without mutating the sibling or shared definition");
        Check(Document.SetStyle("width", "320px", out _) &&
            Document.SetParameter("Caption", "Saved caption", true, out _),
            "Use geometry and template defaults edit the same authoritative RML document");
        var Revision = Document.Revision;
        Check(!Document.SetUseAsset("project://ui/missing", out _) && Document.Revision == Revision,
            "An invalid reference never enters the document or Undo history");
        Document.Save();
        var Reopened = UiEditorDocument.Load(Asset, SourcePath, Folder.FullName);
        Reopened.SetAssetResolver(Resolve);
        var Package = UiDocumentAsset.Load(SourcePath);
        var Composed = UiScreenComposer.Compose(Package, new UiScreenRequest(Asset.Id), Resolve);
        var Rendered = XElement.Parse(Composed);
        Check(Rendered.Descendants("span").Single(Element => Element.Attribute("id")?.Value == "a-title").Value ==
            "Edited ${Literal} <&>" && Composed.Contains("Saved caption", StringComparison.Ordinal),
            "Save and reopen render edited source values, preserving literal text across composition boundaries");
        Check(Rendered.Descendants("div").Single(Element => Element.Attribute("id")?.Value == "First")
                .Attribute("style")!.Value.Contains("320px", StringComparison.Ordinal) &&
            Reopened.Source.Descendants("ui-use").Count() == 2,
            "Saving retains UI references and instance geometry rather than baking expanded preview nodes");
        Check(ChildBytes.SequenceEqual(File.ReadAllBytes(ChildPath)) &&
            Composed.Contains(Path.Combine(Folder.FullName, "Card.rcss"), StringComparison.Ordinal),
            "Per-use editing preserves the reusable definition and discovers its stylesheet dependency");
        var Cycle = UiLayoutTemplate.Parse("<template><ui-use id='again' asset='project://cycle' /></template>");
        var RejectedCycle = false;
        try
        {
            Cycle.Render(new UiLayoutContext(1, 1), new JsonObject(), ResolveAsset: _ => Cycle);
        }
        catch (InvalidDataException Error)
        {
            RejectedCycle = Error.Message.Contains("UILAYOUT_ASSET_CYCLE", StringComparison.Ordinal);
        }
        Check(RejectedCycle, "Cyclic serialized UI references fail explicitly");
        ValidateStyleSheetEditing(Folder.FullName, Check);
    }

    private static void ValidateStyleSheetEditing(string Folder, Action<bool, string> Check)
    {
        const string Source = "/* keep */\r\n.card:hover, .card:focus { color: #ffffff; image: url('a;b.png'); --custom: strange; }\r\n@media (min-width: 100px) { .card { width: 20px; } }\r\n";
        var Edited = RcssSourceEditing.SetValue(Source, 0, "color", "#123456");
        Check(Edited == Source.Replace("#ffffff", "#123456", StringComparison.Ordinal),
            "A structured RCSS value edit preserves comments, selectors, functions, unknown declarations and nested rules");
        var PathValue = Path.Combine(Folder, "Authoring.rcss");
        File.WriteAllText(PathValue, Source);
        var Asset = new AssetDescriptor
        {
            Id = "project://ui/styles",
            Type = "StyleSheet",
            Name = "Styles",
            RelativePath = "Authoring.rcss",
            Source = AssetSource.Project,
        };
        var Document = new TextAssetAuthoringDocument(Asset, PathValue, _ => []);
        Document.Replace(Edited);
        Document.Undo();
        Check(Document.Source == Source, "Structured RCSS edits use the text document's Undo boundary");
        Document.Redo();
        Document.Save();
        Check(File.ReadAllText(PathValue) == Edited, "RCSS Redo and Save persist exactly the intended declaration change");
    }
}
