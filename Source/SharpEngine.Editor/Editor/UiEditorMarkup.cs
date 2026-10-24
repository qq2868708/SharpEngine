using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using SharpEngine.Assets;

namespace SharpEngine.Editor;

/// <summary>Markup for the UI editor panes. Markup is presentation only: every edit is
/// applied to the authoritative document and re-rendered from it.</summary>
internal static class UiEditorMarkup
{
    public static string BuildCanvas(UiEditorDocument Document)
    {
        var Body = Document.DesignBodyElement;
        var Builder = new StringBuilder();
        Builder.Append("<div class=\"ui-design-pane\">")
            .Append("<div class=\"ui-design-ruler\"><span class=\"ui-design-ruler-label\">")
            .Append(UiEditorDocument.DesignWidth.ToString(CultureInfo.InvariantCulture))
            .Append(" x ")
            .Append(UiEditorDocument.DesignHeight.ToString(CultureInfo.InvariantCulture))
            .Append("</span><div class=\"grow\" /><span class=\"ui-design-hint\">")
            .Append(Document.IsReadOnly ? "Read only document" : "Drag to move, Shift for 10px steps")
            .Append("</span></div>")
            .Append("<div class=\"ui-design-scroll\"><div id=\"uiDesignCanvas\" class=\"ui-design-canvas\" style=\"width:")
            .Append(UiEditorDocument.DesignWidth.ToString(CultureInfo.InvariantCulture))
            .Append("px;height:")
            .Append(UiEditorDocument.DesignHeight.ToString(CultureInfo.InvariantCulture))
            .Append("px;\">");
        if (Body is null || !Body.Elements().Any())
        {
            Builder.Append("<div class=\"ui-design-empty\">The RML body has no screen elements. " +
                "Add a container from the toolbar or the Elements pane.</div>");
        }
        else
        {
            if (Document.IsTemplate)
            {
                Builder.Append("<div class=\"ui-design-template-note\">TEMPLATE · " +
                    "placeholder values are shown; slots are filled by the using screen" +
                    "</div>");
            }
            foreach (var Element in Body.Elements())
            {
                AppendNode(Builder, Document, Element);
            }
        }
        Builder.Append("</div></div></div>");
        return Builder.ToString();
    }

    public static string BuildControls(UiEditorDocument Document)
    {
        var Builder = new StringBuilder("<div class=\"ui-panel-title\">BASIC CONTROLS</div><div id=\"uiControlPalette\" class=\"ui-control-palette\">");
        Builder.Append("<div class=\"ui-control-group\">");
        foreach (var Control in UiControlCatalog.All)
        {
            Builder.Append("<button id=\"uiControlAdd-").Append(Control.Id).Append("\" class=\"ui-control-add\"")
                .AppendAction("editor.ui-control-add:" + Control.Id, !Document.IsReadOnly)
                .Append(AppendAvailability(!Document.IsReadOnly)).Append(" title=\"")
                .Append(Escape(Control.Group + " / Add " + Control.Label)).Append(" to the selected container\">")
                .Append(Escape(Control.Label)).Append("</button>");
        }
        Builder.Append("</div>");
        return Builder.Append("<div class=\"ui-control-help\">Select a parent in the tree, then add a control. Use the Inspector to edit its initial properties.</div></div>").ToString();
    }

    public static string BuildHierarchy(UiEditorDocument Document)
    {
        var Builder = new StringBuilder("<div class=\"ui-control-help\" style=\"padding: 4px 6px 0px;\">Search name, type or id</div><div class=\"ui-tree-search-bar\"><input id=\"uiTreeSearch\" type=\"text\" class=\"ui-tree-search\" value=\"");
        Builder.Append(Escape(Document.TreeSearch)).Append("\" placeholder=\"Search name, type or id\" data-change-action=\"editor.ui-tree-search:\" />")
            .Append("<button id=\"uiTreeClear\" class=\"ui-tool-button\" data-action=\"editor.ui-tree-search:\">Clear</button></div>")
            .Append("<div class=\"ui-element-tools\">")
            .Append(ToolButton("editor.ui-tree-expand", "Expand", "Expand every branch", true))
            .Append(ToolButton("editor.ui-tree-collapse", "Collapse", "Collapse every branch", true))
            .Append(ToolButton("editor.ui-select-parent", "Parent", "Select parent", Document.SelectedKey is not null))
            .Append(ToolButton("editor.ui-move-up", "Up", "Move earlier", !Document.IsReadOnly && Document.SelectedKey is not null))
            .Append(ToolButton("editor.ui-move-down", "Down", "Move later", !Document.IsReadOnly && Document.SelectedKey is not null))
            .Append(ToolButton("editor.ui-indent", "Nest", "Nest into previous sibling", !Document.IsReadOnly && Document.SelectedKey is not null))
            .Append(ToolButton("editor.ui-outdent", "Unnest", "Move out one level", !Document.IsReadOnly && Document.SelectedKey is not null))
            .Append("</div><div class=\"ui-element-hierarchy\" id=\"uiWidgetTree\" data-ui-reveal-target=\"")
            .Append(Escape(Document.SelectedElement() is { } Selected ? "uiTreeSelect-" + UiEditorDocument.PreviewNodeIdOf(Selected) : string.Empty))
            .Append("\">");
        var Visible = Document.VisibleTreeElements();
        if (Visible.Count == 0)
        {
            Builder.Append("<div class=\"ui-editor-empty\">").Append(Document.TreeSearch.Length > 0
                ? "No matching controls" : "Add a control from the Controls panel").Append("</div>");
        }
        foreach (var Element in Visible)
        {
            AppendHierarchyRow(Builder, Document, Element);
        }
        return Builder.Append("</div>").ToString();
    }

    public static string BuildInspector(UiEditorDocument Document)
    {
        var Element = Document.SelectedElement();
        if (Element is null)
        {
            return "<div class=\"ui-editor-empty\">Select an element to edit it</div>" +
                BuildDocumentInspector(Document);
        }
        var Builder = new StringBuilder();
        var ReadOnly = Document.IsReadOnly;
        var Id = UiEditorDocument.ElementIdOf(Element);
        Builder.Append(TextField("Display name", UiEditorDocument.AttributeOf(Element, "data-ui-name"),
            "editor.ui-field:name", ReadOnly, Document.DisplayName(Element)));
        Builder.Append(BuildControlInspector(Document, Element));
        if (Element.Name.LocalName == "ui-use")
        {
            var AssetId = UiEditorDocument.AttributeOf(Element, "asset");
            Builder.Append("<div class=\"ui-inspector-group\"><div class=\"ui-inspector-group-title\">REUSABLE CONTROL</div>")
                .Append(TextField("UI asset", AssetId, "editor.ui-use-asset", ReadOnly, "project://ui/...")).Append("<button")
                .AppendAction("editor.ui-open-use:" + AssetId, AssetId.Length != 0)
                .Append(">Edit shared definition</button>")
                .Append(BuildParameters(Document.ParametersOf(Element), "editor.ui-parameter", ReadOnly))
                .Append("</div>");
        }
        else if (ReferenceEquals(Element, Document.BodyElement?.Elements().FirstOrDefault()))
        {
            Builder.Append("<div class=\"ui-inspector-group\"><div class=\"ui-inspector-group-title\">TEMPLATE DEFAULTS</div>")
                .Append(BuildParameters(Document.ParametersOf(Document.Source.Root), "editor.ui-template-parameter", ReadOnly))
                .Append("</div>");
        }
        Builder.Append("<div class=\"ui-inspector-heading\"><span class=\"ui-tag\">&lt;")
            .Append(Escape(Element.Name.LocalName)).Append("&gt;</span><strong>")
            .Append(Escape(Id.Length == 0 ? "(no id)" : Id))
            .Append("</strong></div>")
            .Append("<div class=\"ui-inspector-section\">")
            .Append(TextField("Id", Id, "editor.ui-field:id", ReadOnly, Id.Length == 0
                ? "Add a stable element id" : "Stable element id"))
            .Append(TextField("Binding", UiEditorDocument.AttributeOf(Element, "data-bind"),
                "editor.ui-field:binding", ReadOnly, "e.g. screen.title"))
            .Append(TextField("Command", UiEditorDocument.AttributeOf(Element, "data-command"),
                "editor.ui-field:command", ReadOnly, "e.g. screen.continue"))
            .Append(TextField("Custom class", string.Empty, "editor.ui-field:class-add",
                ReadOnly, "Add a class and press Enter"))
            .Append("</div><div class=\"ui-inspector-section\"><div class=\"ui-inspector-label\">" +
                "Classes</div><div class=\"ui-class-chips\">");
        var Classes = UiEditorDocument.ClassList(Element).ToArray();
        if (Classes.Length == 0)
        {
            Builder.Append("<span class=\"ui-muted\">No class</span>");
        }
        foreach (var ClassName in Classes)
        {
            Builder.Append("<button class=\"ui-class-chip")
                .Append(Document.Sheet.Classes.Contains(ClassName)
                    ? " styled" : string.Empty)
                .Append('"')
                .AppendAction("editor.ui-class-remove:" + Uri.EscapeDataString(ClassName),
                    !ReadOnly)
                .Append(" title=\"Remove class\">").Append(Escape(ClassName))
                .Append("<span class=\"ui-class-chip-remove\">x</span></button>");
        }
        Builder.Append("</div><div class=\"ui-class-suggestions\">");
        foreach (var ClassName in Document.Sheet.Classes
                     .Where(ClassName => !Classes.Contains(ClassName, StringComparer.Ordinal) &&
                         ClassName != "se-control" && !ClassName.StartsWith("se-control-", StringComparison.Ordinal))
                     .Take(24))
        {
            Builder.Append("<button class=\"ui-class-suggestion\"")
                .AppendAction("editor.ui-class-add:" + Uri.EscapeDataString(ClassName),
                    !ReadOnly)
                .Append(">+ ").Append(Escape(ClassName)).Append("</button>");
        }
        Builder.Append("</div></div><div class=\"ui-inspector-section\"><span class=\"ui-inspector-label\">Enabled</span><button")
            .AppendAction(Element.Attribute("disabled") is null ? "editor.ui-enabled:0" : "editor.ui-enabled:1", !ReadOnly)
            .Append('>').Append(Element.Attribute("disabled") is null ? "Enabled" : "Disabled").Append("</button></div>");
        var Declarations = UiEditorDocument.DeclarationsOf(Element);
        var Computed = Document.Sheet.Compute(Element);
        var Absolute = Declarations.TryGetValue("position", out var Position) &&
            Position is "absolute" or "fixed";
        Builder.Append("<div class=\"ui-inspector-section\"><div class=\"ui-inspector-label\">" +
            "Layout mode</div><div class=\"ui-segmented\">")
            .Append("<button class=\"")
            .Append(Absolute ? string.Empty : "active")
            .AppendAction("editor.ui-position:flex", !ReadOnly)
            .Append(">Flow</button><button class=\"")
            .Append(Absolute ? "active" : string.Empty)
            .AppendAction("editor.ui-position:absolute", !ReadOnly)
            .Append(">Absolute</button></div></div>");
        foreach (var Group in UiStyleProperties.Groups)
        {
            Builder.Append("<div class=\"ui-inspector-group\"><div class=\"ui-inspector-group-title\">")
                .Append(Escape(Group.ToUpperInvariant())).Append("</div>");
            foreach (var Field in UiStyleProperties.All.Where(Field =>
                         Field.Group == Group))
            {
                Builder.Append(StyleRow(Field, Declarations, Computed, ReadOnly));
            }
            Builder.Append("</div>");
        }
        Builder.Append("<button class=\"ui-delete-button\"")
            .AppendAction("editor.ui-delete", !ReadOnly)
            .Append(">Delete element</button>");
        return Builder.ToString() + BuildDocumentInspector(Document);
    }

    public static string BuildStylePane(UiEditorDocument Document)
    {
        var Builder = new StringBuilder("<div class=\"ui-style-editor-scroll\">");
        if (Document.Format == UiDocumentFormat.Package)
        {
            Builder.Append("<div class=\"ui-private-rules\"><div class=\"ui-panel-title\">PRIVATE STYLES / SAVED WITH THIS UI</div>");
            try
            {
                var Rules = RcssSourceEditing.Read(Document.PrivateRcss);
                for (var Index = 0; Index < Rules.Count; Index++)
                {
                    var Rule = Rules[Index];
                    Builder.Append("<div class=\"ui-style-rule\"><div class=\"ui-style-selector\">")
                        .Append(Escape(Rule.Selector)).Append("</div>");
                    foreach (var Field in Rule.Declarations.DistinctBy(Field => Field.Name))
                    {
                        Builder.Append(TextField(Field.Name, Field.Value,
                            "editor.ui-private-style:" + Index + ":" + Uri.EscapeDataString(Field.Name),
                            Document.IsReadOnly, "Private RCSS declaration"));
                    }
                    Builder.Append("</div>");
                }
            }
            catch (InvalidDataException Error)
            {
                Builder.Append(Escape(Error.Message));
            }
            Builder.Append(TextField("Add private rule", string.Empty, "editor.ui-private-rule-add",
                Document.IsReadOnly, ".my-control"));
            Builder.Append("<div class=\"ui-panel-title\">EXPLICIT SHARED STYLES</div>");
            foreach (var AssetId in Document.GlobalStyleSheets)
            {
                Builder.Append("<div class=\"ui-style-rule\"><span>").Append(Escape(AssetId))
                    .Append("</span><button").AppendAction("editor.ui-global-style-remove:" + AssetId, !Document.IsReadOnly)
                    .Append(">Remove reference</button></div>");
            }
            Builder.Append(TextField("Add shared style", string.Empty, "editor.ui-global-style-add",
                Document.IsReadOnly, "project://ui/styles/theme")).Append("</div>");
        }
        Builder.Append("<div class=\"ui-computed-style-rules\"><div class=\"ui-style-summary\">")
            .Append("<div><span class=\"ui-inspector-label\">View model contract</span><span>")
            .Append(Escape(Document.ViewModelContract.Length == 0
                ? "None (data-view-model is empty)" : Document.ViewModelContract))
            .Append("</span></div><div><span class=\"ui-inspector-label\">Stylesheets</span><span>")
            .Append(Escape(StyleSources(Document)))
            .Append("</span></div></div>");
        if (Document.Sheet.Rules.Count == 0)
        {
            Builder.Append("<div class=\"ui-editor-empty\">No style rules</div></div></div>");
            return Builder.ToString();
        }
        Builder.Append("<div class=\"ui-style-groups\">");
        foreach (var Group in Document.Sheet.Rules.GroupBy(Rule => Rule.SelectorText,
                     StringComparer.Ordinal))
        {
            var First = Group.First();
            Builder.Append("<div class=\"ui-style-rule\"><div class=\"ui-style-selector\">")
                .Append(Escape(First.SelectorText))
                .Append("<span class=\"ui-style-specificity\">")
                .Append(First.Specificity.ToString(CultureInfo.InvariantCulture))
                .Append("</span></div><div class=\"ui-style-declarations\">");
            foreach (var Declaration in First.Declarations)
            {
                Builder.Append("<span class=\"ui-style-declaration\"><span class=\"ui-style-name\">")
                    .Append(Escape(Declaration.Name)).Append("</span><span class=\"ui-style-value\">")
                    .Append(Escape(Declaration.Value)).Append("</span></span>");
            }
            Builder.Append("</div></div>");
        }
        Builder.Append("</div></div></div>");
        return Builder.ToString();
    }

    public static string BuildDiagnostics(UiEditorDocument Document)
    {
        if (Document.Diagnostics.Count == 0)
        {
            return "<div class=\"ui-diagnostics-list\"><div class=\"ui-editor-empty\">" +
                "No diagnostics</div></div>";
        }
        var Builder = new StringBuilder("<div class=\"ui-diagnostics-list\">");
        foreach (var Diagnostic in Document.Diagnostics)
        {
            Builder.Append("<div class=\"ui-diagnostic ui-diagnostic-")
                .Append(Escape(Diagnostic.Severity)).Append("\"><strong>")
                .Append(Escape(Diagnostic.Code)).Append("</strong><span>")
                .Append(Escape(Diagnostic.Message)).Append("</span></div>");
        }
        Builder.Append("</div>");
        return Builder.ToString();
    }

    public static string BuildSource(UiEditorDocument Document)
    {
        // Show what Save would write, including unsaved edits, instead of the last
        // bytes on disk. A package therefore shows its JSON envelope with inline RML.
        string Source;
        try
        {
            Source = Document.SerializeForSave();
        }
        catch (Exception Exception) when (Exception is IOException or
            UnauthorizedAccessException or InvalidOperationException or
            InvalidDataException)
        {
            Source = "Unable to render source: " + Exception.Message;
        }
        var Label = Document.Format == UiDocumentFormat.Package
            ? "PACKAGE (" + UiDocumentAsset.FileSuffix + ", RML inside)" : "RML";
        return "<div class=\"ui-source-pane\"><div class=\"ui-source-label\">" +
            Escape(Label) + "</div><pre class=\"ui-source-view\">" +
            Escape(Source) + "</pre></div>";
    }

    /// <summary>Lists every contributing style source so an inline-only document is never
    /// presented as having no styles at all.</summary>
    private static string StyleSources(UiEditorDocument Document)
    {
        var Parts = Document.Sheet.Files.Select(File => File).ToList();
        if (Document.Sheet.InlineBlocks != 0)
        {
            Parts.Add(Document.Sheet.InlineBlocks == 1
                ? "inline <style>"
                : $"{Document.Sheet.InlineBlocks} inline <style> blocks");
        }
        return Parts.Count == 0 ? "No style source" : string.Join(", ", Parts);
    }

    private static string BuildDocumentInspector(UiEditorDocument Document)
    {
        var Builder = new StringBuilder();
        Builder.Append("<div class=\"ui-inspector-group\"><div class=\"ui-inspector-group-title\">" +
            "DOCUMENT</div>")
            .Append(TextField("View model", Document.ViewModelContract,
                "editor.ui-field:view-model", Document.IsReadOnly,
                "e.g. validation.main@1"))
            .Append("<div class=\"ui-inspector-section\"><span class=\"ui-inspector-label\">" +
                "Asset</span><span class=\"ui-muted\">").Append(Escape(Document.Asset.Id))
            .Append("</span></div><div class=\"ui-inspector-section\"><span class=\"ui-inspector-label\">" +
                "Source</span><span class=\"ui-muted\">").Append(Escape(Document.Asset.RelativePath))
            .Append("</span></div><div class=\"ui-inspector-section\"><span class=\"ui-inspector-label\">" +
                "State</span><span class=\"ui-muted\">")
            .Append(Document.IsDirty ? "Unsaved changes" : "Saved")
            .Append(" / ").Append(Document.ElementCount.ToString(CultureInfo.InvariantCulture))
            .Append(" elements / revision ")
            .Append(Document.Revision.ToString(CultureInfo.InvariantCulture))
            .Append("</span></div></div>");
        if (Document.IsTemplate)
        {
            Builder.Append("<div class=\"ui-inspector-group\"><div class=\"ui-inspector-group-title\">TEMPLATE DEFAULTS</div>")
                .Append(BuildParameters(Document.ParametersOf(Document.Source.Root), "editor.ui-template-parameter", Document.IsReadOnly))
                .Append(TextField("Add UI asset", string.Empty, "editor.ui-add-use", Document.IsReadOnly, "project://ui/..."))
                .Append("</div>");
        }
        return Builder.ToString();
    }

    private static string BuildParameters(System.Text.Json.Nodes.JsonObject Values, string Prefix, bool ReadOnly)
    {
        var Builder = new StringBuilder();
        foreach (var Pair in Values)
        {
            var Value = Pair.Value?.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? Pair.Value.GetValue<string>() : Pair.Value?.ToJsonString() ?? "null";
            Builder.Append(TextField(Pair.Key, Value, Prefix + ":" + Uri.EscapeDataString(Pair.Key),
                ReadOnly, "Serialized parameter"));
        }
        return Builder.ToString();
    }

    private static string StyleRow(UiStyleField Field, Dictionary<string, string> Declarations,
        IReadOnlyDictionary<string, UiComputedValue> Computed, bool ReadOnly)
    {
        Declarations.TryGetValue(Field.Name, out var Inline);
        Computed.TryGetValue(Field.Name, out var Resolved);
        var HasInline = Inline is not null;
        var Hint = Resolved is null
            ? Field.Placeholder
            : Resolved.Value + " (" + (Resolved.Source == "inline"
                ? "inline" : Resolved.Source) + ")";
        var Builder = new StringBuilder();
        Builder.Append("<div class=\"ui-style-row\"><label class=\"ui-style-label\">")
            .Append(Escape(Field.Label)).Append("</label>");
        var OptionsHint = Field.Options.Count == 0
            ? string.Empty
            : " (" + string.Join(" | ", Field.Options) + ")";
        Builder.Append("<input class=\"ui-style-input\" type=\"text\" id=\"uiStyleField-")
            .Append(Escape(Field.Name)).Append("\" value=\"")
            .Append(Escape(Inline ?? string.Empty))
            .Append("\" placeholder=\"").Append(Escape(Hint + OptionsHint))
            .Append("\" data-change-action=\"editor.ui-style:")
            .Append(Field.Name).Append(":\"");
        if (ReadOnly)
        {
            Builder.Append(" disabled=\"disabled\"");
        }
        Builder.Append(" />");
        Builder.Append("<button class=\"ui-style-clear\" title=\"Remove inline value\"")
            .AppendAction("editor.ui-style-clear:" + Field.Name, !ReadOnly && HasInline)
            .Append(">x</button></div>");
        return Builder.ToString();
    }

    private static string TextField(string Label, string Value, string Action,
        bool ReadOnly, string Placeholder)
    {
        // A stable element id keeps the field observable and focusable even though the
        // same field is reused for whatever element is selected.
        var Separator = Action.LastIndexOf(':');
        var FieldName = Separator < 0 ? Action : Action[(Separator + 1)..];
        var FieldId = "uiInspectorField-" + FieldName;
        if (Action.StartsWith("editor.ui-control-property:", StringComparison.Ordinal))
        {
            FieldId = "uiControlField-" + FieldName;
        }
        if (Action.StartsWith("editor.ui-private-style:", StringComparison.Ordinal))
        {
            FieldId = "uiPrivateStyle-" + Action["editor.ui-private-style:".Length..].Replace(':', '-');
        }
        return "<div class=\"ui-inspector-section\" style=\"display: block; width: 220px;\"><label class=\"ui-inspector-label\">" +
            Escape(Label) + "</label><input class=\"ui-inspector-input\" type=\"text\" style=\"width: 206px; box-sizing: border-box; max-width: none;\" id=\"" +
            Escape(FieldId) + "\" value=\"" +
            Escape(Value) + "\" placeholder=\"" + Escape(Placeholder) +
            "\" data-change-action=\"" + Escape(Action) + ":\"" +
            (ReadOnly ? " disabled=\"disabled\"" : string.Empty) + " /></div>";
    }

    private static string ToolButton(string Action, string Label, string Tooltip,
        bool Enabled)
    {
        return "<button class=\"ui-tool-button\"" + AppendAvailability(Enabled) +
            AppendAction(Action, Enabled) + " title=\"" + Escape(Tooltip) + "\">" +
            Escape(Label) + "</button>";
    }

    private static void AppendNode(StringBuilder Builder, UiEditorDocument Document,
        XElement Element)
    {
        var AuthoredTag = Element.Name.LocalName.ToLowerInvariant();
        // Condition and repeat blocks are resolved when the screen is presented, so the
        // canvas shows one representative expansion instead of the directive element.
        if (AuthoredTag is "ui-if" or "ui-repeat")
        {
            foreach (var Child in Element.Elements())
            {
                AppendNode(Builder, Document, Child);
            }
            return;
        }
        var SourceNode = Element.Annotation<UiDesignSourceNode>();
        var Key = SourceNode?.Key ?? UiEditorDocument.KeyOf(Element);
        var Derived = SourceNode?.Derived == true;
        var Selected = !Derived && string.Equals(Key, Document.SelectedKey, StringComparison.Ordinal);
        var Tag = MapTag(AuthoredTag);
        var Classes = new List<string>
        {
            "ui-design-node",
        };
        if (Selected)
        {
            Classes.Add("ui-design-selected");
        }
        if (AuthoredTag is "input" or "img" or "progress")
        {
            Classes.Add("ui-design-control");
        }
        if (AuthoredTag == "ui-slot")
        {
            Classes.Add("ui-design-slot-node");
        }
        if (AuthoredTag is not ("input" or "img" or "progress"))
        {
            Classes.AddRange(Document.DesignClasses(Element));
        }
        var Style = BuildNodeStyle(Document, Element, Selected);
        Builder.Append('<').Append(Tag)
            .Append(" id=\"").Append(Escape(SourceNode?.PreviewId ?? UiEditorDocument.PreviewNodeIdOf(Element)))
            .Append("\" class=\"").Append(Escape(string.Join(' ', Classes)))
            .Append("\" style=\"").Append(Escape(Style)).Append('"');
        Builder.Append(" data-ui-node=\"").Append(Escape(Key)).Append('"');
        Builder.Append(" data-action=\"editor.ui-select:").Append(Escape(Key)).Append('"');
        // A slot has no authored geometry to drag; the using screen decides its size.
        if (!Document.IsReadOnly && !Derived && AuthoredTag != "ui-slot")
        {
            Builder.Append(" data-ui-gesture=\"move\"");
        }
        if (Selected)
        {
            Builder.Append(" data-ui-selected=\"true\"");
        }
        Builder.Append('>');
        // A template slot is a composition point, not an element: show it as a labelled
        // placeholder so authors can see where the using screen injects a part.
        if (AuthoredTag == "ui-slot")
        {
            Builder.Append("<div class=\"ui-design-slot\">slot: ")
                .Append(Escape(UiEditorDocument.AttributeOf(Element, "name")))
                .Append("</div>");
        }
        else if (Element.HasElements)
        {
            foreach (var Child in Element.Nodes())
            {
                var ChildElement = Child as XElement;
                var Text = Child as XText;
                if (ChildElement is not null)
                {
                    AppendNode(Builder, Document, ChildElement);
                }
                else if (Text is not null)
                {
                    Builder.Append(Escape(Text.Value));
                }
            }
        }
        else if (AuthoredTag is "input" or "progress")
        {
            Builder.Append('<').Append(AuthoredTag).Append(" class=\"ui-control-native-preview ")
                .Append(Escape(string.Join(' ', Document.DesignClasses(Element))))
                .Append("\" style=\"").Append(Escape(BuildNativeControlStyle(Document, Element))).Append('"');
            foreach (var Attribute in Element.Attributes().Where(Attribute => Attribute.Name.LocalName is
                         "type" or "value" or "placeholder" or "min" or "max" or "step" or "checked" or "disabled"))
            {
                Builder.Append(' ').Append(Attribute.Name.LocalName).Append("=\"")
                    .Append(Escape(UiEditorDocument.SampleText(Attribute.Value))).Append('"');
            }
            Builder.Append(" />");
        }
        else if (AuthoredTag == "img")
        {
            var Source = UiEditorDocument.SampleText(
                UiEditorDocument.AttributeOf(Element, "src"));
            Builder.Append("<img class=\"ui-control-native-preview ")
                .Append(Escape(string.Join(' ', Document.DesignClasses(Element))))
                .Append("\" style=\"").Append(Escape(BuildNativeControlStyle(Document, Element))).Append("\" src=\"")
                .Append(Escape(Source.Length == 0 || UiEditorDocument.HasPlaceholder(Source)
                    ? "sharpengine-resource://engine/Resources/Editor/Icons/texture.tga"
                    : DesignImageSource(Document, Source)))
                .Append("\" />");
        }
        else
        {
            Builder.Append(Escape(SourceNode is null ? UiEditorDocument.SampleText(Element.Value) : Element.Value));
        }
        if (Selected && !Document.IsReadOnly && AuthoredTag != "ui-slot")
        {
            AppendSelectionChrome(Builder);
        }
        Builder.Append("</").Append(Tag).Append('>');
    }

    private static void AppendSelectionChrome(StringBuilder Builder)
    {
        Builder.Append("<div class=\"ui-design-outline\" />");
        foreach (var (Handle, Label) in new[]
                 {
                     ("resize-nw", "nw"), ("resize-ne", "ne"), ("resize-sw", "sw"),
                     ("resize-se", "se"), ("resize-e", "e"), ("resize-s", "s"),
                 })
        {
            Builder.Append("<div class=\"ui-design-handle ui-handle-")
                .Append(Label).Append("\" data-ui-gesture=\"")
                .Append(Handle).Append("\" title=\"").Append(Handle)
                .Append("\"></div>");
        }
    }

    private static string DesignImageSource(UiEditorDocument Document, string Source)
    {
        if (Source.Contains("://", StringComparison.Ordinal) || Source.StartsWith("data:", StringComparison.Ordinal))
        {
            return Source;
        }
        try
        {
            var Full = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Document.Path)!, Source)).Replace('\\', '/');
            // Texture sources are passed to SystemInterface.JoinPath, which accepts
            // Windows absolute paths directly. The pipe encoding is for document URLs.
            return Full;
        }
        catch (Exception Error) when (Error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "sharpengine-resource://engine/Resources/Editor/Icons/texture.tga";
        }
    }

    private static string BuildNodeStyle(UiEditorDocument Document, XElement Element,
        bool Selected)
    {
        var Declarations = new List<string>();
        var HasPosition = false;
        foreach (var Pair in Document.Sheet.Compute(Element)
                     .OrderBy(Pair => Pair.Key, StringComparer.Ordinal))
        {
            if (Pair.Key.Equals("transform", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (Element.Name.LocalName is "input" or "img" or "progress" && !ControlLayoutProperty(Pair.Key))
            {
                continue;
            }
            Declarations.Add(Pair.Key + ": " + Pair.Value.Value + ";");
            if (Pair.Key.Equals("position", StringComparison.OrdinalIgnoreCase))
            {
                HasPosition = true;
            }
        }
        if (!HasPosition && Selected)
        {
            Declarations.Add("position: relative;");
        }
        return string.Join(' ', Declarations);
    }

    private static bool ControlLayoutProperty(string Name) =>
        Name is "display" or "position" or "left" or "top" or "right" or "bottom" or "width" or "height" or
            "min-width" or "max-width" or "min-height" or "max-height" or "box-sizing" or "vertical-align" or
            "align-self" or "flex-grow" or "flex-shrink" or "flex-basis" or "order" ||
        Name.StartsWith("margin", StringComparison.Ordinal);

    private static string BuildNativeControlStyle(UiEditorDocument Document, XElement Element)
    {
        var Style = string.Join(' ', Document.Sheet.Compute(Element)
            .Where(Pair => !ControlLayoutProperty(Pair.Key) && Pair.Key != "transform")
            .Select(Pair => Pair.Key + ": " + Pair.Value.Value + ";"));
        return Style + " pointer-events: none; width: 100%; height: 100%; margin: 0px; box-sizing: border-box;";
    }

    private static string MapTag(string Tag) => Tag.ToLowerInvariant() switch
    {
        "span" or "label" or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
            "button" or "br" => Tag.ToLowerInvariant(),
        _ => "div",
    };

    private static string BuildControlInspector(UiEditorDocument Document, XElement Element)
    {
        var Control = Document.ControlOf(Element);
        var Builder = new StringBuilder("<div class=\"ui-inspector-group\"><div class=\"ui-inspector-group-title\">");
        Builder.Append(Escape(Document.ControlLabel(Element))).Append(" PROPERTIES</div>");
        foreach (var Property in Control?.Properties ?? [])
        {
            var Value = UiControlCatalog.Read(Element, Property, Document.Sheet);
            var Action = "editor.ui-control-property:" + Property.Name;
            if (Property.Kind == UiControlPropertyKind.Boolean)
            {
                Builder.Append("<div class=\"ui-inspector-section\"><label class=\"ui-inspector-label\">")
                    .Append(Escape(Property.Label)).Append("</label><button class=\"ui-control-property-toggle\" id=\"uiControlField-")
                    .Append(Property.Name).Append('"').AppendAction(Action + ":" + (Value == "true" ? "false" : "true"), !Document.IsReadOnly)
                    .Append(AppendAvailability(!Document.IsReadOnly)).Append('>').Append(Value == "true" ? "On" : "Off").Append("</button></div>");
            }
            else
            {
                Builder.Append(TextField(Property.Label, Value, Action, Document.IsReadOnly, Property.DefaultValue));
            }
        }
        return Builder.Append("</div>").ToString();
    }

    private static void AppendHierarchyRow(StringBuilder Builder, UiEditorDocument Document, XElement Element)
    {
        var Key = UiEditorDocument.KeyOf(Element);
        var Id = UiEditorDocument.ElementIdOf(Element);
        var NodeId = UiEditorDocument.PreviewNodeIdOf(Element);
        var Selected = Key == Document.SelectedKey;
        var Expanded = Document.TreeExpanded(Element);
        var Reference = Element.Name.LocalName == "ui-use";
        var Type = Document.ControlLabel(Element);
        var Name = Document.DisplayName(Element);
        var Asset = UiEditorDocument.AttributeOf(Element, "asset");
        Builder.Append("<div class=\"ui-widget-row").Append(Selected ? " selected" : string.Empty)
            .Append(Reference ? " reference" : string.Empty).Append("\" style=\"padding-left:")
            .Append(4 + UiEditorDocument.DepthOf(Element) * 12).Append("px;\">");
        if (Element.HasElements)
        {
            Builder.Append("<button class=\"ui-tree-toggle\" id=\"uiTreeToggle-").Append(NodeId).Append('"')
                .AppendAction("editor.ui-tree-toggle:" + Key, Document.TreeSearch.Length == 0)
                .Append(" title=\"").Append(Expanded ? "Collapse branch" : "Expand branch").Append("\">")
                .Append(Expanded ? "-" : "+").Append("</button>");
        }
        else
        {
            Builder.Append("<span class=\"ui-tree-toggle-spacer\"></span>");
        }
        Builder.Append("<button class=\"ui-tree-select\" id=\"uiTreeSelect-").Append(NodeId).Append('"')
            .AppendAction("editor.ui-select:" + Key, true).Append(" title=\"")
            .Append(Escape(Type + " | " + Name + " | " + (Id.Length == 0 ? Key : "#" + Id) + (Reference ? " | " + Asset : string.Empty)))
            .Append("\"><span class=\"ui-tree-name\">").Append(Escape(Name)).Append("</span><span class=\"ui-tree-detail\">")
            .Append(Reference ? "REF / " : string.Empty).Append(Escape(Type))
            .Append(" / ").Append(Escape(Id.Length == 0 ? "anonymous" : "#" + Id)).Append("</span></button></div>");
    }

    private static string AppendAvailability(bool Enabled) =>
        Enabled ? string.Empty : " disabled=\"disabled\"";

    private static string AppendAction(string Action, bool Enabled) =>
        Enabled ? " action=\"" + Escape(Action) + "\"" : string.Empty;

    private static string Escape(string Value) => WebUtility.HtmlEncode(Value);
}
