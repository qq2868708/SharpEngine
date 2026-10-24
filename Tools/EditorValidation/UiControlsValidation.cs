using System.Xml.Linq;
using SharpEngine.Assets;
using SharpEngine.Editor;

internal static class UiControlsValidation
{
    internal static void Run(string Root, Action<bool, string> Check, string EngineRoot)
    {
        var Folder = Directory.CreateDirectory(Path.Combine(Root, "UiControls", "Content", "UI"));
        var PathValue = Path.Combine(Folder.FullName, "Controls.sui");
        File.WriteAllText(PathValue, UiDocumentAsset.Create("Controls", """
            <template><div id="root" data-ui-name="Main Panel" style="display: flex; flex-direction: column; gap: 8px;" /></template>
            """).Serialize());
        var Asset = new AssetDescriptor
        {
            Id = "project://ui/controls",
            Type = "UiDocument",
            RelativePath = "UI/Controls.sui",
            Source = AssetSource.Project,
        };
        var Resolver = SharpEngine.UI.GlobalStyleSheetAsset.CreateResolver(AssetRegistry.Load(EngineRoot));
        UiEditorDocument Load()
        {
            var Result = UiEditorDocument.Load(Asset, PathValue, Root);
            Result.SetGlobalStyleResolver(Resolver);
            return Result;
        }
        var Document = Load();
        var Initial = File.ReadAllText(PathValue);
        var Independent = Load();
        Independent.AddControl("text", out _);
        Check(Independent.GlobalStyleSheets.SequenceEqual([UiControlCatalog.DefaultStyleId]) && !Document.IsDirty,
            "Control creation explicitly adds its foundation style only to the current document instance");
        Independent.Undo();
        Check(Independent.GlobalStyleSheets.Count == 0 && !Independent.IsDirty,
            "One Undo removes both the first control and its newly added shared-style reference");
        var Ids = new Dictionary<string, string>(StringComparer.Ordinal);
        Check(UiControlCatalog.All.Count == 10 && UiControlCatalog.All.Select(Control => Control.Id).Distinct().Count() == 10,
            "One catalogue describes exactly the ten approved basic controls");
        foreach (var Definition in UiControlCatalog.All)
        {
            Document.Select("#root");
            Check(Document.AddControl(Definition.Id, out var Message), "Add " + Definition.Label + ": " + Message);
            var Element = Document.SelectedElement()!;
            Ids[Definition.Id] = UiEditorDocument.KeyOf(Element);
            Check(Document.ControlOf(Element)?.Id == Definition.Id && Element.Parent?.Attribute("id")?.Value == "root",
                "Creation and tree classification agree on the control and the chosen parent");
            Check(Element.Attribute("data-ui-name")?.Value == Definition.Label,
                "A new control stores a readable name in authoritative RML");
        }
        Check(File.ReadAllText(PathValue) == Initial, "Adding controls never saves implicitly");
        Check(Document.GlobalStyleSheets.SequenceEqual([UiControlCatalog.DefaultStyleId]),
            "Repeated control creation references the shared foundation exactly once");
        var Fresh = UiControlCatalog.Find("slider")!.Create("fresh");
        Fresh.SetAttributeValue("value", "77");
        Check(UiControlCatalog.Find("slider")!.Create("other").Attribute("value")!.Value == "50",
            "Reusing a definition creates independent instance attributes");
        Document.Select(Ids["button"]);
        var IdBefore = Document.SelectedElement()!.Attribute("id")!.Value;
        var CommandBefore = Document.SelectedElement()!.Attribute("data-command")!.Value;
        Check(Document.SetAttribute("data-ui-name", "确认 & Continue", out _) &&
            Document.SetControlProperty("text", "Confirm", out _) &&
            Document.DisplayName(Document.SelectedElement()!) == "确认 & Continue" &&
            Document.SelectedElement()!.Attribute("id")!.Value == IdBefore &&
            Document.SelectedElement()!.Attribute("data-command")!.Value == CommandBefore,
            "Display-name and text edits preserve element IDs and command bindings");
        Document.Select(Ids["text-input"]);
        Check(Document.SetControlProperty("value", "Initial <&> text", out _) &&
            Document.SelectedElement()!.Attribute("value")!.Value == "Initial <&> text",
            "Text input exposes its initial value as a saved attribute");
        Document.Select(Ids["checkbox"]);
        Check(Document.SetControlProperty("checked", "true", out _) && Document.SelectedElement()!.Attribute("checked") is not null,
            "Checkbox state uses presence of the native checked attribute");
        var Before = Document.SerializeForSave();
        Check(!Document.SetControlProperty("checked", "maybe", out _) && Document.SerializeForSave() == Before,
            "Invalid boolean input leaves the document unchanged");
        Document.Select(Ids["slider"]);
        Check(Document.SetControlProperty("value", "75", out _) && Document.SetControlProperty("min", "20", out _) &&
            Document.SetControlProperty("step", "5", out _), "Slider numeric attributes are editable together");
        Before = Document.SerializeForSave();
        var Revision = Document.Revision;
        foreach (var Pair in new[] { ("min", "80"), ("max", "70"), ("step", "0"), ("value", "NaN"), ("value", "Infinity"), ("value", "1e100") })
        {
            Check(!Document.SetControlProperty(Pair.Item1, Pair.Item2, out _) &&
                Document.SerializeForSave() == Before && Document.Revision == Revision,
                "Invalid slider constraints fail atomically without a new revision");
        }
        Check(!Document.SetControlProperty("src", "wrong.png", out _), "A control cannot accept another type's properties");
        Document.Select(Ids["progress"]);
        Check(Document.SetControlProperty("value", "0.4", out _) && Document.SetControlProperty("max", "1", out _),
            "Progress accepts finite values in the native zero-to-maximum range");
        Check(!Document.SetControlProperty("max", "0", out _), "Progress rejects a zero maximum");
        Document.Select(Ids["vertical"]);
        Check(Document.SetControlProperty("gap", "16px", out _), "Layout spacing is saved as an RML style declaration");
        Document.Select(Ids["image"]);
        Check(Document.SetControlProperty("src", "../Images/Logo.png", out _), "Image source remains relative in authored RML");
        Document.Save();
        var Saved = Document.SerializeForSave();
        Revision = Document.Revision;
        Document.ExpandTree(false);
        Check(Document.VisibleTreeElements().Count == 1 && !Document.IsDirty && !Document.CanRedo,
            "Collapsing the source tree does not create an authoring edit");
        Check(Document.Select(Ids["button"]) && Document.VisibleTreeElements().Count == 11,
            "Selecting a canvas control reveals its collapsed ancestors in the tree");
        Document.SetTreeSearch("确认");
        Check(Document.VisibleTreeElements().Count == 2 && Document.VisibleTreeElements()[0].Attribute("id")!.Value == "root",
            "Name search retains the matching control's ancestor path");
        Document.SetTreeSearch("progress bar");
        Check(Document.VisibleTreeElements().Count == 2, "Tree search also recognizes the readable control type");
        Document.Select(Ids["checkbox"]);
        Check(Document.TreeSearch == string.Empty, "Canvas selection clears a filter that would hide the selected control");
        Check(Document.Revision == Revision && Document.SerializeForSave() == Saved && !Document.IsDirty,
            "Search, collapse and selection never affect saved RML or the document revision");
        var Reopened = Load();
        Check(Reopened.SerializeForSave() == Saved && Reopened.TreeSearch.Length == 0 && !Reopened.IsDirty,
            "Every control and property survives save and reopen while tree state stays transient");
        Reopened.Select(Ids["checkbox"]);
        Check(Reopened.SetControlProperty("checked", "false", out _) && Reopened.SelectedElement()!.Attribute("checked") is null,
            "False removes checked instead of writing the truthy checked=false form");
        Reopened.Undo();
        Check(Reopened.SelectedElement()!.Attribute("checked") is not null && !Reopened.IsDirty,
            "Undo restores the saved checkbox state and savepoint");
        Reopened.Redo();
        Check(Reopened.SelectedElement()!.Attribute("checked") is null && Reopened.IsDirty,
            "Redo reapplies the control-property edit");
        var Markup = UiEditorMarkup.BuildCanvas(Document);
        Check(Markup.Contains("type=\"checkbox\"", StringComparison.Ordinal) &&
            Markup.Contains("type=\"range\"", StringComparison.Ordinal) && Markup.Contains("<progress", StringComparison.Ordinal),
            "Design markup renders native input variants and progress instead of generic text placeholders");
        var ImagePath = Path.GetFullPath(Path.Combine(Folder.FullName, "../Images/Logo.png")).Replace('\\', '/');
        Check(Markup.Contains(ImagePath, StringComparison.Ordinal),
            "Relative design images become filesystem texture paths anchored at the package, not document URL pipe paths");
        var ReadOnly = UiEditorDocument.Load(new AssetDescriptor
        {
            Id = Asset.Id,
            Type = Asset.Type,
            Source = AssetSource.Engine,
        }, PathValue, Root);
        Check(!ReadOnly.AddControl("button", out _) && !ReadOnly.SetAttribute("data-ui-name", "Changed", out _),
            "Read-only documents reject control creation and display-name changes");
        var CheckedParsed = UiSelector.TryParse(".check:checked", out var CheckedSelector);
        Check(CheckedParsed && CheckedSelector!.Matches(XElement.Parse("<input type='checkbox' class='check' checked='' />")) &&
            !CheckedSelector.Matches(XElement.Parse("<input type='checkbox' class='check' />")) &&
            !CheckedSelector.Matches(XElement.Parse("<input type='range' class='check' checked='' />")),
            "Design checked selectors follow native checkbox attribute semantics");
        var DisabledParsed = UiSelector.TryParse("button:disabled", out var DisabledSelector);
        Check(DisabledParsed && DisabledSelector!.Matches(XElement.Parse("<button disabled='' />")) &&
            !DisabledSelector.Matches(XElement.Parse("<button />")) && !UiSelector.TryParse("button:hover", out _),
            "Authored disabled state is evaluated while transient hover remains explicitly unsupported");
        var InvalidPath = Path.Combine(Folder.FullName, "Invalid.sui");
        var InvalidSource = UiDocumentAsset.Create("Invalid", "<template><input id='bad' type='range' min='10' max='1' /></template>").Serialize();
        File.WriteAllText(InvalidPath, InvalidSource);
        var Invalid = UiEditorDocument.Load(Asset, InvalidPath, Root);
        var Rejected = false;
        try
        {
            Invalid.Save();
        }
        catch (InvalidDataException)
        {
            Rejected = true;
        }
        Check(Rejected && Invalid.Diagnostics.Any(Diagnostic => Diagnostic.Code == "UI_CONTROL_VALUE_INVALID") &&
            File.ReadAllText(InvalidPath) == InvalidSource,
            "Invalid numeric source is diagnosed and cannot be silently saved or clamped");
        var NamedDirectives = SharpEngine.UI.UiLayoutTemplate.Parse("""
            <template><ui-if test="Show" data-ui-name="Conditional content"><span>visible</span></ui-if><ui-repeat items="Rows" data-ui-name="Repeated content"><span>${Text}</span></ui-repeat><ui-slot name="Extra" data-ui-name="Extra slot" /></template>
            """);
        var Rendered = NamedDirectives.Render(new SharpEngine.UI.UiLayoutContext(100, 100),
            new System.Text.Json.Nodes.JsonObject
            {
                ["Show"] = true,
                ["Rows"] = new System.Text.Json.Nodes.JsonArray
                {
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["Text"] = "repeated",
                    },
                },
            }, new Dictionary<string, SharpEngine.UI.IUiLayoutPart>
            {
                ["Extra"] = new SlotPart(),
            });
        Check(Rendered.Contains(">visible<", StringComparison.Ordinal) && Rendered.Contains(">repeated<", StringComparison.Ordinal) &&
            Rendered.Contains(">slot<", StringComparison.Ordinal),
            "Authoring names on conditional, repeat and slot directives do not change their runtime semantics");
        var LiteralName = SharpEngine.UI.UiLayoutTemplate.Parse("<template><span data-ui-name='Literal ${NotABinding}'>Text</span></template>")
            .Render(new SharpEngine.UI.UiLayoutContext(100, 100), new System.Text.Json.Nodes.JsonObject());
        Check(LiteralName.Contains("Literal ${NotABinding}", StringComparison.Ordinal),
            "An authoring display name is literal metadata and cannot introduce a required runtime binding");
        Check(Reopened.SetEnabled(false, out _) && Reopened.SelectedElement()!.Attribute("disabled") is not null &&
            Reopened.SetEnabled(true, out _) && Reopened.SelectedElement()!.Attribute("disabled") is null,
            "Disabling and re-enabling a control safely adds and removes the authored disabled attribute");
        var UnreferencedPath = Path.Combine(Folder.FullName, "Unreferenced.sui");
        File.WriteAllText(UnreferencedPath, UiDocumentAsset.Create("Unreferenced",
            "<template><button id='unstyled' class='se-control se-control-button'>Unstyled</button></template>").Serialize());
        var Unreferenced = UiEditorDocument.Load(Asset, UnreferencedPath, Root);
        Unreferenced.SetGlobalStyleResolver(Resolver);
        Check(!UiEditorMarkup.BuildCanvas(Unreferenced).Contains("se-control-button", StringComparison.Ordinal),
            "Editor foundation styles cannot leak into a source that does not reference them");
        Unreferenced.SetGlobalStyleReference(UiControlCatalog.DefaultStyleId, true, out _);
        Check(UiEditorMarkup.BuildCanvas(Unreferenced).Contains("se-control-button", StringComparison.Ordinal),
            "An explicit source reference enables the same foundation classes in Design");
        Unreferenced.Undo();
        Check(!UiEditorMarkup.BuildCanvas(Unreferenced).Contains("se-control-button", StringComparison.Ordinal),
            "Undoing the style reference removes its effect from Design without changing source classes");
    }

    private sealed class SlotPart : SharpEngine.UI.IUiLayoutPart
    {
        public string BuildLayout(SharpEngine.UI.UiLayoutContext Context) { return "<span>slot</span>"; }
    }
}
