using System.Xml.Linq;
using SharpEngine.Assets;
using SharpEngine.Editor;

namespace SharpEngine.EditorValidation;

/// <summary>Focused validation of the formal RML UI editor document model: identity,
/// transactions, styling cascade, gestures, diagnostics and atomic save.</summary>
internal static class UiEditorValidation
{
    internal static void Run(string Root, Action<bool, string> Check)
    {
        var SelectorValid = UiSelector.TryParse(".scope .card > .value", out var Selector);
        var SelectorTree = XElement.Parse("<div class='scope'><section><div class='card'><span class='value' /></div></section></div>");
        Check(SelectorValid && Selector!.Matches(SelectorTree.Descendants("span").Single()),
            "Descendant and child selectors compose across whitespace without dropping shared RCSS rules");
        var InvalidChild = UiSelector.TryParse(".scope > > .value", out _);
        Check(!InvalidChild, "A malformed child combinator is still rejected");
        var Project = Path.Combine(Root, "UiEditorValidation");
        var UiDirectory = Directory.CreateDirectory(Path.Combine(Project, "Content", "UI"));
        var RmlPath = Path.Combine(UiDirectory.FullName, "Screen.rml");
        File.WriteAllText(RmlPath, """
            <rml data-view-model="validation.screen@1">
            <head><link type="text/rcss" href="Screen.rcss" /></head>
            <body><div id="screen" class="screen"><span id="headline" class="headline" data-bind="screen.title">Title</span><button id="continue" class="primary" data-command="screen.continue">Continue</button></div></body>
            </rml>
            """);
        File.WriteAllText(Path.Combine(UiDirectory.FullName, "Screen.rcss"), """
            .screen { display: flex; flex-direction: column; gap: 12px; }
            .headline { color: #ffffff; font-size: 22px; }
            .accent { color: #00ff00; }
            .primary { color: #101418; background-color: #6cc7af; }
            @media (min-width: 100px) { .headline { color: #ff0000; } }
            """);
        var Asset = new AssetDescriptor
        {
            Id = "project://ui/validation-screen",
            Type = "RmlDocument",
            Name = "Validation Screen",
            RelativePath = "Content/UI/Screen.rml",
            Source = AssetSource.Project,
        };
        var Document = UiEditorDocument.Load(Asset, RmlPath, Project);
        Check(!Document.IsDirty && Document.Revision == 0 &&
            Document.SelectedKey == "#screen" && Document.ElementCount == 3,
            "UI document loads clean with the first screen element selected");
        Check(Document.Diagnostics.Any(Value =>
                Value.Code == "UI_STYLE_SELECTOR_UNSUPPORTED" && Value.Severity == "warning"),
            "Unsupported stylesheet selectors are reported instead of silently ignored");
        Check(Document.Sheet.Rules.Count == 4 && Document.Sheet.Classes.Contains("headline") &&
            Document.Sheet.Classes.Contains("primary"),
            "Linked RCSS rules and class names feed the design surface");
        Check(Document.ViewModelContract == "validation.screen@1",
            "The document exposes its view model contract");

        XElement CurrentHeadline()
        {
            return Document.Source.Descendants()
                .Single(Element => (string?)Element.Attribute("id") == "headline");
        }
        var Headline = CurrentHeadline();
        Check(Document.Select(UiEditorDocument.KeyOf(Headline)) &&
            Document.SelectedKey == "#headline",
            "Elements are selected by their stable document key");
        var Computed = Document.Sheet.Compute(Headline);
        Check(Computed["color"].Value == "#ffffff" && Computed["color"].Source == ".headline",
            "Cascade resolves the winning stylesheet rule without inline declarations");

        Check(Document.SetStyle("font-size", "30px", out var StyleMessage) &&
            Document.IsDirty && Document.Revision == 1,
            $"Inline style edits commit one revision: {StyleMessage}");
        Check(Document.Sheet.Compute(Headline)["font-size"].Value == "30px",
            "Inline declarations win over stylesheet rules while typing");
        Check(!Document.SetStyle("font-size", "huge", out var InvalidMessage) &&
            InvalidMessage.Length != 0 && Document.Revision == 1,
            "Invalid style values are rejected without creating a revision");
        Document.Undo();
        var Restored = CurrentHeadline();
        Check(Document.Sheet.Compute(Restored).TryGetValue("font-size", out var Undone) &&
            Undone.Value == "22px" && !Document.IsDirty,
            "Undo restores both the value and the dirty state");
        Document.Redo();
        Check(Document.IsDirty && Document.Sheet.Compute(CurrentHeadline())["font-size"]
                .Value == "30px",
            "Redo reapplies the discarded edit");

        Check(Document.AddClass("accent", out var ClassMessage) && Document.IsDirty,
            $"Class edits participate in the same transaction history: {ClassMessage}");
        Check(UiEditorDocument.ClassList(CurrentHeadline()).Contains("accent") &&
            Document.Sheet.Compute(CurrentHeadline())["color"].Value == "#00ff00",
            "Adding a class re-resolves the cascade for the element");
        Document.RemoveClass("accent", out _);
        Check(!UiEditorDocument.ClassList(CurrentHeadline()).Contains("accent") &&
            Document.Sheet.Compute(CurrentHeadline())["color"].Value == "#ffffff",
            "Removing a class returns to the previous cascade winner");

        Check(!Document.SetAttribute("id", "continue", out var RenameMessage) &&
            RenameMessage.Length != 0,
            "Renaming to an existing id is rejected with an explicit message");
        Check(Document.SetAttribute("id", "headline-alias", out _) && Document.IsDirty &&
            Document.SelectedKey == "#headline-alias",
            "Rename commits a new stable identity and keeps the element selected");
        Document.Undo();
        Check(Document.SelectedKey == "#headline" &&
            Document.Source.Descendants().Any(Element =>
                (string?)Element.Attribute("id") == "headline"),
            "Undo restores the previous id and selection");

        Check(Document.AddElement("div", out var AddMessage) && Document.IsDirty,
            $"Adding an element selects the new node: {AddMessage}");
        Check((string?)Document.SelectedElement()!.Attribute("id") == "container",
            "New containers receive a unique default id");
        Check(Document.DuplicateSelected(out _) &&
            (string?)Document.SelectedElement()!.Attribute("id") == "container2",
            "Duplicating an element allocates a sibling with a fresh id");
        var MovedUp = Document.MoveSelected(-1, out var UpMessage);
        var MovedDown = Document.MoveSelected(1, out var DownMessage);
        Check(MovedUp && MovedDown,
            $"Sibling ordering changes are reversible: up={MovedUp} '{UpMessage}' down={MovedDown} '{DownMessage}' selected={Document.SelectedKey}");
        Check(Document.DeleteSelected(out _) && Document.ElementCount == 4,
            "Deleting removes exactly the selected subtree");
        Document.Undo();
        Check(Document.ElementCount == 5, "Undo restores the deleted subtree");

        Document.Select("#screen");
        Check(Document.SetPositionMode(true, 40, 24, out var PositionMessage) &&
            Document.IsDirty,
            $"Absolute layout records canvas-relative offsets: {PositionMessage}");
        Check((string?)Document.SelectedElement()!.Attribute("style") is { } AbsoluteStyle &&
            AbsoluteStyle.Contains("position: absolute;", StringComparison.Ordinal) &&
            AbsoluteStyle.Contains("left: 40px;", StringComparison.Ordinal) &&
            AbsoluteStyle.Contains("top: 24px;", StringComparison.Ordinal),
            "Absolute mode writes position, left and top as inline declarations");
        Check(Document.SetPositionMode(false, 0, 0, out _) &&
            (string?)Document.SelectedElement()!.Attribute("style") is null,
            "Switching back to flow removes every positioning declaration");

        Document.Select("#headline");
        var Baseline = Document.CanonicalSource;
        Check(Document.BeginGesture("move", 0, 0, 120, 22, out var GestureMessage) &&
            Document.IsGesturing,
            $"Drag gestures open one transient transaction: {GestureMessage}");
        Check(Document.ApplyGestureMove("move", 13, 7, true, out _) &&
            Document.LiveStyleProperties().Any(Pair =>
                Pair.Name == "margin-left" && Pair.Value == "10px") &&
            Document.LiveStyleProperties().Any(Pair =>
                Pair.Name == "margin-top" && Pair.Value == "10px"),
            "Flow drags write snapped margins and expose them as live properties");
        Document.CommitGesture();
        Check(!Document.IsGesturing && Document.CanUndo,
            "Committing a gesture records one Undo entry");
        Document.Undo();
        Check(Document.CanonicalSource == Baseline,
            "One Undo reverts the whole gesture instead of each pointer step");
        Check(Document.BeginGesture("move", 0, 0, 120, 22, out _) &&
            Document.ApplyGestureMove("move", 5, 5, false, out _),
            "A second gesture can start from the committed state");
        Document.CancelGesture();
        Check(!Document.IsGesturing && Document.CanonicalSource == Baseline,
            "Canceling a gesture restores the committed document without a revision");

        var SaveMessage = string.Empty;
        try
        {
            Document.Save();
        }
        catch (Exception Error)
        {
            SaveMessage = Error.Message;
        }
        var Saved = File.ReadAllText(RmlPath);
        Check(SaveMessage.Length == 0 && !Document.IsDirty &&
            Saved.Contains("id=\"container\"", StringComparison.Ordinal) &&
            Saved.Contains("data-bind=\"screen.title\"", StringComparison.Ordinal) &&
            Saved.Contains("font-size: 30px", StringComparison.Ordinal) &&
            !Saved.Contains("position: absolute", StringComparison.Ordinal),
            $"Save persists the validated document: {SaveMessage}");

        File.AppendAllText(RmlPath, Environment.NewLine);
        Document.SetStyle("font-size", "18px", out _);
        var Conflict = string.Empty;
        try
        {
            Document.Save();
        }
        catch (Exception Error)
        {
            Conflict = Error.Message;
        }
        Check(Conflict.Contains("outside this editor", StringComparison.Ordinal) &&
            !File.ReadAllText(RmlPath).Contains("font-size: 18px", StringComparison.Ordinal),
            "Save refuses to overwrite a source changed outside the editor");

        var BrokenPath = Path.Combine(UiDirectory.FullName, "Broken.rml");
        var BrokenSource = """
            <rml><body><div id="dup"></div><div id="dup"></div><span></span></body></rml>
            """;
        File.WriteAllText(BrokenPath, BrokenSource);
        var BrokenAsset = new AssetDescriptor
        {
            Id = "project://ui/broken-screen",
            Type = "RmlDocument",
            Name = "Broken Screen",
            RelativePath = "Content/UI/Broken.rml",
            Source = AssetSource.Project,
        };
        var Broken = UiEditorDocument.Load(BrokenAsset, BrokenPath, Project);

        Check(Broken.Diagnostics.Any(Value =>
                Value.Code == "UI_ELEMENT_ID_DUPLICATE" && Value.Severity == "error"),
            "Duplicate element ids are an error diagnostic");
        Check(Broken.Diagnostics.Any(Value => Value.Code == "UI_ELEMENT_ID_MISSING"),
            "Anonymous elements are reported as warnings, not silently dropped");
        var BlockedSave = string.Empty;
        try
        {
            Broken.Save();
        }
        catch (Exception Error)
        {
            BlockedSave = Error.Message;
        }
        Check(BlockedSave.Contains("Resolve UI document errors", StringComparison.Ordinal) &&
            File.ReadAllText(BrokenPath) == BrokenSource,
            "Error diagnostics block Save before any file replacement");

        var Anonymous = Broken.BodyElement!.Elements().Last();
        Check(Broken.Select(UiEditorDocument.KeyOf(Anonymous)) &&
            ReferenceEquals(Broken.SelectedElement(), Anonymous),
            "Anonymous elements remain addressable through their body path");
        Check(Broken.RenameId("stable-id", out _) &&
            (string?)Broken.SelectedElement()!.Attribute("id") == "stable-id",
            "Anonymous elements can be given a stable id");
        Broken.Undo();
        Check((string?)Broken.SelectedElement()!.Attribute("id") is null,
            "Giving an id is undoable like any other attribute edit");
        Check(Broken.Diagnostics.Any(Value => Value.Code == "UI_ELEMENT_ID_DUPLICATE"),
            "Resolved identity errors stay visible until the source itself is fixed");

        RunPackageChecks(Root, Check);
        RunInlineStyleChecks(Root, Check);
        RunTemplateChecks(Root, Check);
        RunStyleSheetRoundTripChecks(Root, Check);
        UiCompositionAuthoringValidation.Run(Root, Check);
        UiPackagedStylesValidation.Run(Root, Check);
    }

    private static void RunStyleSheetRoundTripChecks(string Root, Action<bool, string> Check)
    {
        var Folder = Directory.CreateDirectory(Path.Combine(Root, "UiStyleSheetRoundTrip"));
        var PathValue = Path.Combine(Folder.FullName, "Screen.sui");
        File.WriteAllText(Path.Combine(Folder.FullName, "Shared.rcss"), ".root .value { color: #123456; }");
        File.WriteAllText(PathValue, UiDocumentAsset.Create("Shared styles",
            "<template><div id='root' class='root'><span id='value' class='value'>Before</span></div></template>",
            ["Shared.rcss"]).Serialize());
        var Asset = new AssetDescriptor
        {
            Id = "project://ui/shared-styles",
            Type = "UiDocument",
            Name = "Shared styles",
            RelativePath = "Screen.sui",
            Source = AssetSource.Project,
        };
        var Document = UiEditorDocument.Load(Asset, PathValue, Folder.FullName);
        Check(UiDocumentAsset.Parse(Document.SerializeForSave()).StyleSheets.SequenceEqual(["Shared.rcss"]),
            "Unsaved preview input preserves the package's stylesheet dependencies");
        Document.Select("#value");
        Document.SetText("After", out _);
        Document.Save();
        var Reopened = UiEditorDocument.Load(Asset, PathValue, Folder.FullName);
        Check(UiDocumentAsset.Load(PathValue).StyleSheets.SequenceEqual(["Shared.rcss"]) &&
            Reopened.Sheet.Compute(Reopened.Source.Descendants("span").Single())["color"].Value == "#123456",
            "Editing and saving a styled template preserves both its RCSS list and evaluated shared styles");
    }

    /// <summary>A reusable <c>&lt;template&gt;</c> package is valid UI source: the editor
    /// must present it instead of reporting a missing &lt;rml&gt; root and body.</summary>
    private static void RunTemplateChecks(string Root, Action<bool, string> Check)
    {
        var Project = Path.Combine(Root, "UiTemplateValidation");
        var UiDirectory = Directory.CreateDirectory(Path.Combine(Project, "Content", "UI"));
        File.WriteAllText(Path.Combine(UiDirectory.FullName, "Card.rcss"),
            ".card { padding: 10px; border: 2px #3aa78f; }");
        var PackagePath = Path.Combine(UiDirectory.FullName, "Card.sui");
        File.WriteAllText(PackagePath, UiDocumentAsset.Create("Card", """
            <template>
            <div id="card-${Id}" class="card">
            <img id="card-${Id}-icon" src="${Icon}" />
            <span id="card-${Id}-title">${Title}</span>
            <ui-slot name="Extra" />
            </div>
            </template>
            """, ["Card.rcss"]).Serialize());
        var Asset = new AssetDescriptor
        {
            Id = "project://ui/card",
            Type = "UiDocument",
            Name = "Card",
            RelativePath = "Content/UI/Card.sui",
            Source = AssetSource.Project,
        };
        var Template = UiEditorDocument.Load(Asset, PackagePath, Project);
        var DefaultStylePath = Path.Combine(UiDirectory.FullName, "Default.sglobalcss");
        File.WriteAllText(DefaultStylePath, ".se-control { color: #ffffff; }");
        Template.SetGlobalStyleResolver(Id => Id == UiControlCatalog.DefaultStyleId
            ? DefaultStylePath : throw new InvalidDataException("Unexpected style: " + Id));
        Check(Template.IsTemplate,
            "A <template> package is recognized as a reusable definition");
        Check(Template.Diagnostics.Count == 0,
            $"A valid template opens without diagnostics: " +
            string.Join("; ", Template.Diagnostics.Select(Value => Value.Code)));
        Check(Template.BodyElement?.Name.LocalName == "template" &&
            Template.ElementCount == 4,
            "A template's own element is its content root");
        Check(Template.SelectedKey is not null &&
            Template.SelectedKey.StartsWith("path:", StringComparison.Ordinal),
            "Template elements are addressed by path because ids hold placeholders");
        Check(UiEditorDocument.SampleText("card-${Id}") == "card-Id" &&
            UiEditorDocument.HasPlaceholder("${Title}"),
            "Preview values substitute placeholders by name");
        Check(Template.Sheet.Files.Count == 1 && Template.Sheet.Rules.Count == 1,
            "A package styleSheets entry feeds the template preview cascade");

        var Slot = Template.Source.Descendants()
            .Single(Element => Element.Name.LocalName == "ui-slot");
        Check(Template.Select(UiEditorDocument.KeyOf(Slot)) &&
            Template.SelectedElement()?.Name.LocalName == "ui-slot",
            "A composition slot is selectable inside its template");
        Check(Template.AddElement("span", out var Message) && Template.IsDirty,
            $"Template content participates in document editing: {Message}");
        Template.Undo();

        var InvalidPath = Path.Combine(UiDirectory.FullName, "Bad.sui");
        File.WriteAllText(InvalidPath, UiDocumentAsset.Create("Bad", """
            <template><div><ui-slot /></div></template>
            """).Serialize());
        var Invalid = UiEditorDocument.Load(new AssetDescriptor
        {
            Id = "project://ui/bad-template",
            Type = "UiDocument",
            Name = "Bad",
            RelativePath = "Content/UI/Bad.sui",
            Source = AssetSource.Project,
        }, InvalidPath, Project);
        Check(Invalid.Diagnostics.Any(Value => Value.Code == "UI_TEMPLATE_SLOT_NAME" &&
                Value.Severity == "error"),
            "A <ui-slot> without a name is an error diagnostic");
    }

    /// <summary>RmlUi merges &lt;link&gt; and inline &lt;style&gt; into one ordered style
    /// list. The preview must resolve exactly that order, and must report the inline
    /// cases it does not use instead of silently dropping them.</summary>
    private static void RunInlineStyleChecks(string Root, Action<bool, string> Check)
    {
        var Project = Path.Combine(Root, "UiInlineStyleValidation");
        var UiDirectory = Directory.CreateDirectory(Path.Combine(Project, "Content", "UI"));
        File.WriteAllText(Path.Combine(UiDirectory.FullName, "Base.rcss"),
            ".row { color: #111111; gap: 4px; }");
        var InlinePath = Path.Combine(UiDirectory.FullName, "Inline.rml");
        File.WriteAllText(InlinePath, """
            <rml>
            <head>
              <link type="text/css" href="Base.rcss" />
              <style>.row { color: #222222; } .badge { font-size: 9px; }</style>
            </head>
            <body><div id="row" class="row"><span id="badge" class="badge">ok</span></div></body>
            </rml>
            """);
        var InlineAsset = new AssetDescriptor
        {
            Id = "project://ui/inline-style",
            Type = "RmlDocument",
            Name = "Inline Style",
            RelativePath = "Content/UI/Inline.rml",
            Source = AssetSource.Project,
        };
        var Inline = UiEditorDocument.Load(InlineAsset, InlinePath, Project);
        Check(Inline.Sheet.Files.Count == 1 && Inline.Sheet.InlineBlocks == 1 &&
            Inline.Sheet.Rules.Count == 3,
            "text/css links and inline <style> blocks both feed the cascade");
        var RowElement = Inline.Source.Descendants()
            .Single(Element => (string?)Element.Attribute("id") == "row");
        Check(Inline.Sheet.Compute(RowElement)["color"].Value == "#222222",
            "A later inline <style> wins over an earlier link at equal specificity");
        Check(Inline.Sheet.Compute(RowElement)["gap"].Value == "4px",
            "An earlier link still applies declarations the inline block omits");
        var BadgeElement = Inline.Source.Descendants()
            .Single(Element => (string?)Element.Attribute("id") == "badge");
        Check(Inline.Sheet.Compute(BadgeElement)["font-size"].Value == "9px" &&
            Inline.Sheet.Classes.Contains("badge"),
            "Inline-only selectors and classes are visible to the design surface");
        Check(Inline.Diagnostics.Count == 0,
            "Valid inline styles produce no diagnostics");

        var OrderPath = Path.Combine(UiDirectory.FullName, "Order.rml");
        File.WriteAllText(OrderPath, """
            <rml>
            <head>
              <style>.row { color: #333333; }</style>
              <link type="text/rcss" href="Base.rcss" />
            </head>
            <body><div id="row" class="row" /></body>
            </rml>
            """);
        var LaterLink = UiEditorDocument.Load(new AssetDescriptor
        {
            Id = "project://ui/style-order",
            Type = "RmlDocument",
            Name = "Style Order",
            RelativePath = "Content/UI/Order.rml",
            Source = AssetSource.Project,
        }, OrderPath, Project);
        Check(LaterLink.Sheet.Compute(LaterLink.Source.Descendants()
                .Single(Element => (string?)Element.Attribute("id") == "row"))["color"]
                .Value == "#111111",
            "A link after an inline <style> wins, matching document order");

        var BodyPath = Path.Combine(UiDirectory.FullName, "BodyStyle.rml");
        File.WriteAllText(BodyPath, """
            <rml>
            <head><link type="text/rcss" href="Base.rcss" /></head>
            <body><style>.row { color: #444444; }</style><div id="row" class="row" /></body>
            </rml>
            """);
        var BodyStyle = UiEditorDocument.Load(new AssetDescriptor
        {
            Id = "project://ui/body-style",
            Type = "RmlDocument",
            Name = "Body Style",
            RelativePath = "Content/UI/BodyStyle.rml",
            Source = AssetSource.Project,
        }, BodyPath, Project);
        Check(BodyStyle.Sheet.InlineBlocks == 0 &&
            BodyStyle.Diagnostics.Any(Value => Value.Code == "UI_STYLE_OUTSIDE_HEAD" &&
                Value.Severity == "warning"),
            "A <style> outside <head> is reported instead of previewed");
        Check(BodyStyle.Sheet.Compute(BodyStyle.Source.Descendants()
                .Single(Element => (string?)Element.Attribute("id") == "row"))["color"]
                .Value == "#111111",
            "An ignored body <style> does not change the cascade");

        var NoStylePath = Path.Combine(UiDirectory.FullName, "NoStyle.rml");
        File.WriteAllText(NoStylePath, "<rml><body><div id=\"root\" /></body></rml>");
        var NoStyle = UiEditorDocument.Load(new AssetDescriptor
        {
            Id = "project://ui/no-style",
            Type = "RmlDocument",
            Name = "No Style",
            RelativePath = "Content/UI/NoStyle.rml",
            Source = AssetSource.Project,
        }, NoStylePath, Project);
        Check(NoStyle.Sheet.Rules.Count == 0 && NoStyle.Sheet.InlineBlocks == 0 &&
            NoStyle.Diagnostics.Count == 0,
            "A document with no style source stays diagnostic-free");
    }

    /// <summary>The <c>.sui</c> package is a versioned JSON envelope whose RML member is
    /// authoritative. It is one asset with one identity, so Save must replace exactly one
    /// file and keep the envelope reproducible.</summary>
    private static void RunPackageChecks(string Root, Action<bool, string> Check)
    {
        var Project = Path.Combine(Root, "UiPackageValidation");
        var UiDirectory = Directory.CreateDirectory(Path.Combine(Project, "Content", "UI"));
        const string PackageRml = """
            <rml data-view-model="validation.package@1">
            <head><link type="text/rcss" href="Package.rcss" /></head>
            <body><div id="root" class="root"><span id="caption" class="caption">Package screen</span></div></body>
            </rml>
            """;
        File.WriteAllText(Path.Combine(UiDirectory.FullName, "Package.rcss"),
            ".root { display: flex; flex-direction: column; } .caption { color: #abcdef; }");
        var PackagePath = Path.Combine(UiDirectory.FullName, "Package.sui");
        File.WriteAllText(PackagePath,
            UiDocumentAsset.Create("Package Screen", PackageRml).Serialize());

        var Parsed = UiDocumentAsset.Load(PackagePath);
        Check(Parsed.Name == "Package Screen" && Parsed.Rml == PackageRml,
            "A .sui package round-trips its name and inline RML");
        Check(Parsed.ToJson()["schemaVersion"]!.GetValue<int>() ==
                UiDocumentAsset.CurrentSchemaVersion,
            "The package reports its schema version");

        var Asset = new AssetDescriptor
        {
            Id = "project://ui/package-screen",
            Type = "UiDocument",
            Name = "Package Screen",
            RelativePath = "Content/UI/Package.sui",
            Source = AssetSource.Project,
        };
        var Document = UiEditorDocument.Load(Asset, PackagePath, Project);
        Check(Document.Format == UiDocumentFormat.Package &&
            Document.PackageName == "Package Screen" &&
            Document.ElementCount == 2 &&
            Document.SelectedKey == "#root",
            "The editor opens a package as one document with the authored elements");
        Check(Document.Diagnostics.Count == 0,
            "A valid package produces no diagnostics");
        Check(Document.Sheet.Files.Count == 1 && Document.Sheet.Rules.Count == 2,
            "Package RML resolves its linked RCSS relative to the package file");

        var TextMessage = string.Empty;
        Check(Document.Select("#caption") &&
            Document.SetText("Edited inside the package", out TextMessage) &&
            Document.IsDirty,
            $"Package edits are ordinary document transactions: {TextMessage}");
        var Described = Document.Describe();
        Check(Described["format"]!.GetValue<string>() == "package" &&
            Described["packageName"]!.GetValue<string>() == "Package Screen",
            "The editor snapshot reports the package source shape");
        Document.Save();
        Check(!Document.IsDirty, "Package Save clears the dirty state");

        var Saved = UiDocumentAsset.Load(PackagePath);
        Check(Saved.Name == "Package Screen" &&
            Saved.Rml.Contains("Edited inside the package", StringComparison.Ordinal) &&
            Saved.Rml.Contains("data-view-model=\"validation.package@1\"",
                StringComparison.Ordinal) &&
            Saved.Rml.Contains("href=\"Package.rcss\"", StringComparison.Ordinal),
            "Save rewrites the inline RML and preserves the package envelope");
        using var SavedJson = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(PackagePath));
        Check(SavedJson.RootElement.EnumerateObject().Select(Property => Property.Name)
                .OrderBy(Name => Name, StringComparer.Ordinal)
                .SequenceEqual(new[] { "$schema", "name", "rml", "schemaVersion" }),
            "The saved package has no extra or missing top-level fields");

        File.AppendAllText(PackagePath, " ");
        Document.SetText("Conflict probe", out _);
        var Conflict = string.Empty;
        try
        {
            Document.Save();
        }
        catch (Exception Error)
        {
            Conflict = Error.Message;
        }
        Check(Conflict.Contains("outside this editor", StringComparison.Ordinal),
            "Package Save refuses to overwrite an external modification");

        CheckReject("""{"schemaVersion":2,"name":"Future","rml":"<rml><body/></rml>"}""",
            "SEUI_SCHEMA_UNSUPPORTED", Check);
        CheckReject("""{"schemaVersion":1,"name":"Extra","rml":"<rml><body/></rml>","parts":[]}""",
            "SEUI_FIELD_UNKNOWN", Check);
        CheckReject("""{"schemaVersion":1,"name":"Missing RML"}""",
            "SEUI_FIELD_INVALID", Check);
        CheckReject("""{"schemaVersion":1,"name":"Bad RML","rml":"<ui><body/></ui>"}""",
            "SEUI_RML_INVALID", Check);
        CheckReject("""{"schemaVersion":1,"name":"No Body","rml":"<rml><head/></rml>"}""",
            "SEUI_RML_INVALID", Check);
        CheckReject("not json", "SEUI_JSON_INVALID", Check);

        static void CheckReject(string Source, string ExpectedCode,
            Action<bool, string> Check)
        {
            var Message = string.Empty;
            try
            {
                _ = UiDocumentAsset.Parse(Source, "package-test");
            }
            catch (Exception Error)
            {
                Message = Error.Message;
            }
            Check(Message.Contains(ExpectedCode, StringComparison.Ordinal),
                $"Invalid package input reports {ExpectedCode}: {Message}");
        }
    }
}
