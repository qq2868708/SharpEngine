using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.Assets;
using SharpEngine.Editor;
using SharpEngine.UI;

namespace SharpEngine.EditorValidation;

internal static class UiPackagedStylesValidation
{
    internal static void Run(string Root, Action<bool, string> Check)
    {
        var Folder = Directory.CreateDirectory(Path.Combine(Root, "UiPackagedStyles"));
        var GlobalPath = Path.Combine(Folder.FullName, "Theme.sglobalcss");
        const string GlobalCss = ".card { color: #112233; }\n";
        File.WriteAllText(GlobalPath, GlobalCss);
        var ChildPath = Path.Combine(Folder.FullName, "Card.sui");
        var Child = UiDocumentAsset.Create("Card", "<template><span class='card'>Child</span></template>",
            Rcss: ".card { padding: 8px; }", GlobalStyleSheets: ["project://ui/theme"]);
        File.WriteAllText(ChildPath, Child.Serialize());
        var ScreenPath = Path.Combine(Folder.FullName, "Screen.sui");
        const string Rml = "<template><div id='root'><span id='caption' class='card'>Before</span><ui-use id='Child' asset='project://ui/card' /></div></template>";
        const string Private = "/* keep */\n.card { color: #aabbcc; }\n";
        var Package = UiDocumentAsset.Create("Screen", Rml, Rcss: Private,
            GlobalStyleSheets: ["project://ui/theme"]);
        File.WriteAllText(ScreenPath, Package.Serialize());
        UiLayoutTemplate ResolveUi(string Id)
        {
            if (Id != "project://ui/card")
            {
                throw new InvalidDataException("Unknown UI: " + Id);
            }
            return UiLayoutTemplate.FromAsset(UiDocumentAsset.Load(ChildPath), ChildPath);
        }
        string ResolveGlobal(string Id)
        {
            if (Id != "project://ui/theme")
            {
                throw new InvalidDataException("Unknown shared style: " + Id);
            }
            return GlobalPath;
        }
        var Composed = XDocument.Parse(UiScreenComposer.Compose(Package,
            new UiScreenRequest("project://ui/screen"), ResolveUi, ResolveGlobal, ScreenPath));
        Check(!Composed.Descendants("link").Any() && Composed.Descendants("style").Count() == 3,
            "Explicit shared styles deduplicate by identity while each referenced UI contributes private RCSS once");
        Check(Composed.Descendants("style").First().Value == GlobalCss &&
            Composed.Descendants("style").Skip(1).First().Value == Private,
            "Runtime uses shared styles before private styles without creating sidecar private files");
        var WithoutShared = UiDocumentAsset.Create("Standalone", "<template><span>Unrelated</span></template>", Rcss: ".local { color: #fff; }");
        var Unrelated = UiScreenComposer.Compose(WithoutShared, new UiScreenRequest("project://other"), ResolveUi, ResolveGlobal, ScreenPath);
        Check(!Unrelated.Contains("Theme.sglobalcss", StringComparison.Ordinal),
            "A registered shared stylesheet has no effect without an explicit reference");
        var Asset = new AssetDescriptor
        {
            Id = "project://ui/screen",
            Type = "UiDocument",
            Name = "Screen",
            RelativePath = "Screen.sui",
            Source = AssetSource.Project,
        };
        var Document = UiEditorDocument.Load(Asset, ScreenPath, Folder.FullName);
        Document.SetGlobalStyleResolver(ResolveGlobal);
        Document.SetAssetResolver(ResolveUi);
        Check(Document.Select("#caption") &&
            Document.Sheet.Compute(Document.SelectedElement()!)["color"].Value == "#aabbcc",
            "Design and runtime share the global-before-private cascade order");
        Check(Document.SetPrivateStyleValue(0, "color", "#556677", out _) && Document.IsDirty,
            "A private rule edit marks its owning .sui dirty");
        Document.SetText("After", out _);
        Document.Undo();
        Check(Document.PrivateRcss.Contains("#556677", StringComparison.Ordinal) &&
            Document.SelectedElement()!.Value == "Before", "One Undo reverts the structure edit while retaining the preceding private style edit");
        Document.Undo();
        Check(!Document.IsDirty && Document.PrivateRcss == Private,
            "The next Undo restores the private style and the combined document savepoint");
        Document.Redo();
        Document.Redo();
        Document.Save();
        var Reopened = UiDocumentAsset.Load(ScreenPath);
        Check(Reopened.Rcss == Private.Replace("#aabbcc", "#556677", StringComparison.Ordinal) &&
            Reopened.GlobalStyleSheets.SequenceEqual(["project://ui/theme"]) && Reopened.Rml.Contains("After", StringComparison.Ordinal),
            "One atomic .sui save round-trips RML, exact private RCSS and stable shared references together");
        Check(File.ReadAllText(GlobalPath) == GlobalCss && UiDocumentAsset.Load(ChildPath).Rcss == Child.Rcss,
            "Private edits never modify the shared stylesheet or another UI package");
        Check(Document.SetGlobalStyleReference("project://ui/theme", false, out _) && Document.IsDirty,
            "Shared reference removal is an authoring operation");
        Document.Undo();
        Check(!Document.IsDirty && Document.GlobalStyleSheets.Count == 1,
            "Undo restores the explicit dependency without dirtying the shared asset");
        File.AppendAllText(GlobalPath, ".shared-new { padding: 12px; }\n");
        Check(Document.RefreshChangedDependencies() && !Document.IsDirty && Document.DependencyRevision == 1 &&
            UiScreenComposer.Compose(UiDocumentAsset.Load(ScreenPath), new UiScreenRequest(Asset.Id), ResolveUi, ResolveGlobal, ScreenPath)
                .Contains(".shared-new", StringComparison.Ordinal),
            "Saving a shared asset refreshes dependent presentation without dirtying the UI source or using stale native stylesheet caches");
        var Revision = Document.Revision;
        Check(!Document.SetGlobalStyleReference("project://ui/unknown", true, out _) && Document.Revision == Revision,
            "Missing or wrong-type shared references cannot enter the authoring history");
        var Rejected = false;
        try
        {
            UiDocumentAsset.Parse("{\"schemaVersion\":1,\"name\":\"Bad\",\"rml\":\"<template/>\",\"globalStyleSheets\":[\"Theme.sglobalcss\"]}");
        }
        catch (InvalidDataException)
        {
            Rejected = true;
        }
        Check(Rejected, "Shared styles require stable asset URIs rather than filesystem guesses");
        var Full = UiDocumentAsset.Create("Full document", "<rml><body><span>Full</span></body></rml>", Rcss: ".full { color: #123; }");
        Check(XDocument.Parse(UiScreenComposer.Compose(Full, new UiScreenRequest("project://full"), SourcePath: ScreenPath))
            .Descendants("style").Single().Value == Full.Rcss, "Complete RML documents also receive their package-private RCSS");
        Check(UiStyleSources.RebaseUrls(".image { decorator: image(url('image.png')); }", ScreenPath)
            .Contains(Folder.FullName.Replace('\\', '/') + "/image.png", StringComparison.Ordinal),
            "Private stylesheet relative resources resolve from the owning package");
        const string Literal = "/* url(comment.png) */ .text { content: 'url(literal.png)'; }";
        Check(UiStyleSources.RebaseUrls(Literal, ScreenPath) == Literal,
            "URL rebasing preserves comments and quoted literal text");
        ValidateResolver(Folder.FullName, Check);
    }

    private static void ValidateResolver(string Folder, Action<bool, string> Check)
    {
        var Content = Directory.CreateDirectory(Path.Combine(Folder, "Content"));
        File.Copy(Path.Combine(Folder, "Theme.sglobalcss"), Path.Combine(Content.FullName, "Theme.sglobalcss"));
        File.Copy(Path.Combine(Folder, "Card.sui"), Path.Combine(Content.FullName, "Card.sui"));
        var Engine = Directory.CreateDirectory(Path.Combine(Folder, "Engine"));
        File.WriteAllText(Path.Combine(Engine.FullName, "engine.json"), "{\"schemaVersion\":2,\"assets\":{\"manifest\":\"assets.json\"}}");
        File.WriteAllText(Path.Combine(Engine.FullName, "assets.json"), "{\"schemaVersion\":2,\"assets\":[]}");
        var Manifest = Path.Combine(Folder, "Sample.sharpproject");
        File.WriteAllText(Manifest, "{\"schemaVersion\":3,\"assets\":{\"manifest\":\"assets.json\"}}");
        File.WriteAllText(Path.Combine(Folder, "assets.json"), """
            {"schemaVersion":2,"assets":[
            {"id":"ui/theme","type":"GlobalStyleSheet","path":"Theme.sglobalcss"},
            {"id":"ui/wrong","type":"UiDocument","path":"Card.sui"}]}
            """);
        var Assets = AssetRegistry.LoadFromManifests(Path.Combine(Engine.FullName, "engine.json"), Manifest);
        var Resolve = GlobalStyleSheetAsset.CreateResolver(Assets);
        Check(Resolve("project://ui/theme") == Path.Combine(Content.FullName, "Theme.sglobalcss"),
            "Shared stylesheet resolution follows asset identity and source ownership");
        var Rejected = false;
        try
        {
            Resolve("project://ui/wrong");
        }
        catch (InvalidDataException)
        {
            Rejected = true;
        }
        Check(Rejected, "An asset with the wrong type cannot be used as shared CSS");
    }
}
