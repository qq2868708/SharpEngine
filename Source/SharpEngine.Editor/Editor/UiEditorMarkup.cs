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
        var Body = Document.BodyElement;
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
            foreach (var Element in Body.Elements())
            {
                AppendNode(Builder, Document, Element);
            }
        }
        Builder.Append("</div></div></div>");
        return Builder.ToString();
    }

    public static string BuildHierarchy(UiEditorDocument Document)
    {
        var Body = Document.BodyElement;
        if (Body is null)
        {
            return "<div class=\"ui-editor-empty\">Invalid RML body</div>";
        }
        var Elements = Body.Elements().ToArray();
        var Builder = new StringBuilder();
        Builder.Append("<div class=\"ui-element-tools\">")
            .Append(ToolButton("editor.ui-select-parent", "Parent", "Select the parent element",
                Document.SelectedKey is not null))
            .Append(ToolButton("editor.ui-move-up", "Up", "Move the element earlier",
                Document.SelectedKey is not null))
            .Append(ToolButton("editor.ui-move-down", "Down", "Move the element later",
                Document.SelectedKey is not null))
            .Append(ToolButton("editor.ui-indent", "In", "Nest into the previous sibling",
                Document.SelectedKey is not null))
            .Append(ToolButton("editor.ui-outdent", "Out", "Move one level out",
                Document.SelectedKey is not null))
            .Append(ToolButton("editor.ui-duplicate", "Dup", "Duplicate the element",
                Document.SelectedKey is not null))
            .Append(ToolButton("editor.ui-delete", "Del", "Delete the element",
                Document.SelectedKey is not null))
            .Append("</div><div class=\"ui-element-hierarchy\">");
        if (Elements.Length == 0)
        {
            Builder.Append("<div class=\"ui-editor-empty\">The screen is empty</div>");
        }
        foreach (var Element in Elements)
        {
            AppendHierarchyRow(Builder, Document, Element);
        }
        Builder.Append("</div>");
        return Builder.ToString();
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
                     .Where(ClassName => !Classes.Contains(ClassName, StringComparer.Ordinal))
                     .Take(24))
        {
            Builder.Append("<button class=\"ui-class-suggestion\"")
                .AppendAction("editor.ui-class-add:" + Uri.EscapeDataString(ClassName),
                    !ReadOnly)
                .Append(">+ ").Append(Escape(ClassName)).Append("</button>");
        }
        Builder.Append("</div></div>");
        if (!Element.HasElements)
        {
            Builder.Append(TextField("Text", Element.Value, "editor.ui-field:text", ReadOnly,
                "Element text"));
        }
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
        var Builder = new StringBuilder();
        Builder.Append("<div class=\"ui-style-pane\"><div class=\"ui-style-summary\">")
            .Append("<div><span class=\"ui-inspector-label\">View model contract</span><span>")
            .Append(Escape(Document.ViewModelContract.Length == 0
                ? "None (data-view-model is empty)" : Document.ViewModelContract))
            .Append("</span></div><div><span class=\"ui-inspector-label\">Stylesheets</span><span>")
            .Append(Escape(StyleSources(Document)))
            .Append("</span></div></div>");
        if (Document.Sheet.Rules.Count == 0)
        {
            Builder.Append("<div class=\"ui-editor-empty\">No style rules</div></div>");
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
        Builder.Append("</div></div>");
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
        Builder.Append("<input class=\"ui-style-input\" type=\"text\" style=\"display: block; width: 150px;\" id=\"uiStyleField-")
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
        return "<div class=\"ui-inspector-section\" style=\"display: block; width: 220px;\"><label class=\"ui-inspector-label\">" +
            Escape(Label) + "</label><input class=\"ui-inspector-input\" type=\"text\" style=\"width: 206px; max-width: none;\" id=\"" +
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
        var Key = UiEditorDocument.KeyOf(Element);
        var Selected = string.Equals(Key, Document.SelectedKey, StringComparison.Ordinal);
        var AuthoredTag = Element.Name.LocalName.ToLowerInvariant();
        var Tag = MapTag(AuthoredTag);
        var Classes = new List<string>
        {
            "ui-design-node",
        };
        if (Selected)
        {
            Classes.Add("ui-design-selected");
        }
        if (AuthoredTag is "input" or "img")
        {
            Classes.Add("ui-design-replaced");
        }
        foreach (var ClassName in UiEditorDocument.ClassList(Element))
        {
            Classes.Add(ClassName);
        }
        var Style = BuildNodeStyle(Document, Element, Selected);
        Builder.Append('<').Append(Tag)
            .Append(" id=\"").Append(Escape(UiEditorDocument.PreviewNodeIdOf(Element)))
            .Append("\" class=\"").Append(Escape(string.Join(' ', Classes)))
            .Append("\" style=\"").Append(Escape(Style)).Append('"');
        Builder.Append(" data-ui-node=\"").Append(Escape(Key)).Append('"');
        Builder.Append(" data-action=\"editor.ui-select:").Append(Escape(Key)).Append('"');
        if (!Document.IsReadOnly)
        {
            Builder.Append(" data-ui-gesture=\"move\"");
        }
        if (Selected)
        {
            Builder.Append(" data-ui-selected=\"true\"");
        }
        Builder.Append('>');
        if (Element.HasElements)
        {
            foreach (var Child in Element.Elements())
            {
                AppendNode(Builder, Document, Child);
            }
        }
        else if (AuthoredTag == "input")
        {
            var Placeholder = UiEditorDocument.AttributeOf(Element, "placeholder");
            var Value = UiEditorDocument.AttributeOf(Element, "value");
            Builder.Append("<span class=\"ui-design-replaced-text\">")
                .Append(Escape(Value.Length != 0 || Placeholder.Length != 0
                    ? Value + Placeholder : "input")).Append("</span>");
        }
        else if (AuthoredTag == "img")
        {
            var Source = UiEditorDocument.AttributeOf(Element, "src");
            Builder.Append("<img class=\"ui-design-image\" src=\"")
                .Append(Escape(Source.Length == 0
                    ? "sharpengine-resource://engine/Resources/Editor/Icons/image.tga"
                    : Source))
                .Append("\" />");
        }
        else
        {
            Builder.Append(Escape(Element.Value));
        }
        if (Selected && !Document.IsReadOnly)
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

    private static string MapTag(string Tag) => Tag.ToLowerInvariant() switch
    {
        "span" or "label" or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
            "button" => Tag.ToLowerInvariant(),
        _ => "div",
    };

    private static void AppendHierarchyRow(StringBuilder Builder,
        UiEditorDocument Document, XElement Element)
    {
        var Key = UiEditorDocument.KeyOf(Element);
        var Id = UiEditorDocument.ElementIdOf(Element);
        var Depth = UiEditorDocument.DepthOf(Element);
        var Selected = string.Equals(Key, Document.SelectedKey, StringComparison.Ordinal);
        Builder.Append("<button class=\"ui-tree-row")
            .Append(Selected ? " selected" : string.Empty)
            .Append(Id.Length == 0 ? " invalid" : string.Empty)
            .Append("\" style=\"padding-left:")
            .Append(8 + Depth * 12).Append("px;\"").AppendAction("editor.ui-select:" + Key, true)
            .Append("><span class=\"ui-tree-tag\">&lt;")
            .Append(Escape(Element.Name.LocalName)).Append("&gt;</span><span class=\"ui-tree-label\">")
            .Append(Escape(Id.Length == 0 ? "(no id)" : Id))
            .Append("</span></button>");
        foreach (var Child in Element.Elements())
        {
            AppendHierarchyRow(Builder, Document, Child);
        }
    }

    private static string AppendAvailability(bool Enabled) =>
        Enabled ? string.Empty : " disabled=\"disabled\"";

    private static string AppendAction(string Action, bool Enabled) =>
        Enabled ? " action=\"" + Escape(Action) + "\"" : string.Empty;

    private static string Escape(string Value) => WebUtility.HtmlEncode(Value);
}
