using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpEngine.Assets;

namespace SharpEngine.Editor;

internal sealed record UiEditorDiagnostic(string Severity, string Code, string Message);

internal sealed class UiEditorDocument
{
    private sealed record StyleRule(string Selector,
        IReadOnlyDictionary<string, string> Declarations, int Specificity, int Order);

    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly string _ownerRoot;
    private XDocument _document;
    private string _savedState;
    private List<StyleRule> _styleRules = [];
    private List<UiEditorDiagnostic> _diagnostics = [];

    private UiEditorDocument(AssetDescriptor asset, string path, string ownerRoot,
        XDocument document)
    {
        Asset = asset;
        Path = path;
        _ownerRoot = System.IO.Path.GetFullPath(ownerRoot);
        _document = document;
        _savedState = CanonicalState(document);
        SelectedElementId = EditableElements().Select(ElementId)
            .FirstOrDefault(id => id.Length != 0);
        RefreshDerivedState();
    }

    public AssetDescriptor Asset { get; }
    public string Path { get; }
    public string? SelectedElementId { get; private set; }
    public long Revision { get; private set; }
    public bool IsReadOnly => Asset.ReadOnly || Asset.Source == AssetSource.Engine;
    public bool IsDirty => CanonicalState(_document) != _savedState;
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public IReadOnlyList<UiEditorDiagnostic> Diagnostics => _diagnostics;
    public string Title => Asset.Name + (IsDirty ? "*" : string.Empty);
    public string ViewModelContract =>
        (string?)_document.Root?.Attribute("data-view-model") ?? string.Empty;

    public static UiEditorDocument Load(AssetDescriptor asset, string path,
        string ownerRoot)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!File.Exists(path))
            throw new FileNotFoundException("RML document was not found", path);
        var document = XDocument.Load(path,
            LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        return new UiEditorDocument(asset, System.IO.Path.GetFullPath(path),
            ownerRoot, document);
    }

    public bool Select(string elementId)
    {
        if (FindElement(elementId) is null) return false;
        SelectedElementId = elementId;
        return true;
    }

    public bool SetField(string field, string value, out string message)
    {
        message = string.Empty;
        if (IsReadOnly)
        {
            message = "Engine-owned UI documents are read only";
            return false;
        }
        if (field == "view-model")
            return Mutate(() => SetOrRemove(_document.Root!, "data-view-model", value));
        var selected = SelectedElement();
        if (selected is null)
        {
            message = "Select an element before editing properties";
            return false;
        }
        switch (field)
        {
            case "class":
                return Mutate(() => SetOrRemove(selected, "class", value));
            case "binding":
                return Mutate(() => SetOrRemove(selected, "data-bind", value));
            case "command":
                return Mutate(() => SetOrRemove(selected, "data-command", value));
            case "style":
                return Mutate(() => SetOrRemove(selected, "style", value));
            case "text":
                if (selected.HasElements)
                {
                    message = "Container text cannot replace child elements";
                    return false;
                }
                return Mutate(() => selected.Value = value);
            default:
                message = $"Unknown UI field '{field}'";
                return false;
        }
    }

    public bool SetPositionMode(bool absolute, out string message)
    {
        message = string.Empty;
        var selected = SelectedElement();
        if (selected is null)
        {
            message = "Select an element before changing layout mode";
            return false;
        }
        if (IsReadOnly)
        {
            message = "Engine-owned UI documents are read only";
            return false;
        }
        return Mutate(() =>
        {
            var style = ParseDeclarations((string?)selected.Attribute("style") ?? string.Empty);
            if (absolute)
            {
                style["position"] = "absolute";
                style.TryAdd("left", "24px");
                style.TryAdd("top", "24px");
            }
            else
            {
                style.Remove("position");
                style.Remove("left");
                style.Remove("top");
                style.Remove("right");
                style.Remove("bottom");
            }
            SetOrRemove(selected, "style", SerializeDeclarations(style));
        });
    }

    public bool AddElement(string tag, out string message)
    {
        message = string.Empty;
        if (IsReadOnly)
        {
            message = "Engine-owned UI documents are read only";
            return false;
        }
        var selected = SelectedElement();
        var body = Body();
        if (body is null)
        {
            message = "RML document has no body";
            return false;
        }
        var parent = selected is null || IsLeaf(selected) ? selected?.Parent ?? body : selected;
        if (parent is null)
        {
            message = "No valid insertion parent is available";
            return false;
        }
        var prefix = tag switch
        {
            "span" => "text",
            "button" => "button",
            _ => "container",
        };
        var id = UniqueId(prefix);
        return Mutate(() =>
        {
            var element = new XElement(tag, new XAttribute("id", id));
            if (tag == "div")
                element.SetAttributeValue("style", "display: flex; flex-direction: column; gap: 8px;");
            else
                element.Value = tag == "button" ? "Button" : "Text";
            parent.Add(element);
            SelectedElementId = id;
        });
    }

    public bool DeleteSelected(out string message)
    {
        message = string.Empty;
        var selected = SelectedElement();
        if (selected is null || selected.Parent == Body())
        {
            message = "The root screen element cannot be deleted";
            return false;
        }
        if (IsReadOnly)
        {
            message = "Engine-owned UI documents are read only";
            return false;
        }
        var next = selected.Parent is XElement parent ? ElementId(parent) : string.Empty;
        return Mutate(() =>
        {
            selected.Remove();
            SelectedElementId = next.Length == 0 ? null : next;
        });
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(CanonicalState(_document));
        Restore(_undo.Pop());
        Revision++;
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(CanonicalState(_document));
        Restore(_redo.Pop());
        Revision++;
        return true;
    }

    public void Save()
    {
        if (IsReadOnly) throw new InvalidOperationException(
            "Engine-owned UI documents cannot be saved");
        RefreshDerivedState();
        if (_diagnostics.Any(value => value.Severity == "error"))
            throw new InvalidDataException("Resolve UI document errors before saving");
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory,
            $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var text = _document.ToString() + Environment.NewLine;
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            _ = XDocument.Load(temporary, LoadOptions.SetLineInfo);
            File.Move(temporary, Path, true);
            _savedState = CanonicalState(_document);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string BuildHierarchyMarkup()
    {
        var body = Body();
        if (body is null) return "<div class=\"ui-editor-empty\">Invalid RML body</div>";
        var markup = new StringBuilder();
        foreach (var element in body.Elements()) AppendHierarchy(markup, element, 0);
        return markup.Length == 0
            ? "<div class=\"ui-editor-empty\">The RML body is empty</div>"
            : markup.ToString();
    }

    public string BuildPreviewMarkup()
    {
        var body = Body();
        if (body is null) return "<div class=\"ui-editor-empty-canvas\">Invalid RML body</div>";
        var markup = new StringBuilder("<div class=\"ui-preview-screen\">");
        foreach (var element in body.Elements()) AppendPreview(markup, element);
        markup.Append("</div>");
        return markup.ToString();
    }

    public string BuildInspectorMarkup()
    {
        var selected = SelectedElement();
        if (selected is null)
            return "<div class=\"ui-editor-empty\">Select an element</div>";
        var id = ElementId(selected);
        var tag = selected.Name.LocalName;
        var text = selected.HasElements ? string.Empty : selected.Value;
        var position = ParseDeclarations((string?)selected.Attribute("style") ?? string.Empty)
            .TryGetValue("position", out var mode) && mode == "absolute" ? "Absolute" : "Flex";
        var readOnly = IsReadOnly ? " disabled=\"disabled\"" : string.Empty;
        return $"<div class=\"ui-inspector-heading\"><span class=\"ui-tag\">{Escape(tag)}</span><strong>{Escape(id)}</strong></div>" +
            Field("ViewModel", ViewModelContract, "view-model", readOnly) +
            Field("Text", text, "text", readOnly) +
            Field("Class", (string?)selected.Attribute("class") ?? string.Empty, "class", readOnly) +
            Field("Binding", (string?)selected.Attribute("data-bind") ?? string.Empty, "binding", readOnly) +
            Field("Command", (string?)selected.Attribute("data-command") ?? string.Empty, "command", readOnly) +
            Field("Inline style", (string?)selected.Attribute("style") ?? string.Empty, "style", readOnly) +
            $"<div class=\"ui-inspector-section\"><div class=\"ui-inspector-label\">Position mode</div><div class=\"ui-segmented\"><button class=\"{(position == "Flex" ? "active" : string.Empty)}\" action=\"editor.ui-position:flex\">Flex</button><button class=\"{(position == "Absolute" ? "active" : string.Empty)}\" action=\"editor.ui-position:absolute\">Absolute</button></div></div>" +
            $"<button class=\"ui-delete-button\" action=\"editor.ui-delete\"{readOnly}>Delete element</button>";
    }

    public string BuildSourceMarkup()
    {
        string source;
        try
        {
            source = File.ReadAllText(Path);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            source = $"Unable to read source: {exception.Message}";
        }
        return $"<pre class=\"ui-source-view\">{Escape(source)}</pre>";
    }

    public string BuildDiagnosticsMarkup()
    {
        if (_diagnostics.Count == 0)
            return "<div class=\"ui-editor-empty\">No diagnostics</div>";
        var markup = new StringBuilder();
        foreach (var diagnostic in _diagnostics)
        {
            markup.Append("<div class=\"ui-diagnostic ui-diagnostic-")
                .Append(Escape(diagnostic.Severity)).Append("\"><strong>")
                .Append(Escape(diagnostic.Code)).Append("</strong><span>")
                .Append(Escape(diagnostic.Message)).Append("</span></div>");
        }
        return markup.ToString();
    }

    public string BuildAssetSummaryMarkup() =>
        $"<div class=\"ui-editor-asset-summary\"><div class=\"ui-inspector-heading\"><span class=\"ui-tag\">{Escape(Asset.Type)}</span><strong>{Escape(Asset.Name)}</strong></div><div class=\"ui-inspector-section\"><span class=\"ui-inspector-label\">Asset ID</span><span>{Escape(Asset.Id)}</span></div><div class=\"ui-inspector-section\"><span class=\"ui-inspector-label\">Path</span><span>{Escape(Asset.RelativePath)}</span></div><div class=\"ui-inspector-section\"><span class=\"ui-inspector-label\">State</span><span>{(IsDirty ? "Dirty" : "Saved")} · {(IsReadOnly ? "Read only" : "Editable")}</span></div></div>";

    private static string Field(string label, string value, string action,
        string readOnly) =>
        $"<div class=\"ui-inspector-section\"><label class=\"ui-inspector-label\">{Escape(label)}</label><input value=\"{EscapeAttribute(value)}\" data-change-action=\"editor.ui-change:{action}:\"{readOnly} /></div>";

    private void AppendHierarchy(StringBuilder markup, XElement element, int depth)
    {
        var id = ElementId(element);
        var selectable = id.Length != 0;
        var selected = selectable && string.Equals(id, SelectedElementId,
            StringComparison.Ordinal);
        markup.Append("<button class=\"ui-tree-row")
            .Append(selected ? " selected" : string.Empty)
            .Append(selectable ? string.Empty : " invalid")
            .Append("\" style=\"padding-left:")
            .Append(8 + depth * 14).Append("px;\"");
        if (selectable)
            markup.Append(" action=\"editor.ui-select:").Append(EscapeAttribute(id)).Append("\"");
        markup.Append("><span class=\"ui-tree-tag\">&lt;")
            .Append(Escape(element.Name.LocalName)).Append("&gt;</span><span>")
            .Append(selectable ? Escape(id) : "Missing stable id")
            .Append("</span></button>");
        foreach (var child in element.Elements()) AppendHierarchy(markup, child, depth + 1);
    }

    private void AppendPreview(StringBuilder markup, XElement element)
    {
        var id = ElementId(element);
        var selected = id.Length != 0 && string.Equals(id, SelectedElementId,
            StringComparison.Ordinal);
        var styles = ComputedStyle(element);
        if (selected)
        {
            styles["border-color"] = "#72b7f2";
            styles["border-width"] = "2px";
        }
        var tag = element.Name.LocalName.ToLowerInvariant() switch
        {
            "span" or "label" or "p" or "h1" or "h2" or "h3" => "span",
            "button" => "button",
            _ => "div",
        };
        markup.Append('<').Append(tag).Append(" class=\"ui-authored-node\"");
        if (id.Length != 0)
            markup.Append(" action=\"editor.ui-select:").Append(EscapeAttribute(id)).Append("\"");
        var style = SerializeDeclarations(styles);
        if (style.Length != 0)
            markup.Append(" style=\"").Append(EscapeAttribute(style)).Append("\"");
        markup.Append('>');
        if (element.HasElements)
        {
            foreach (var child in element.Elements()) AppendPreview(markup, child);
        }
        else
        {
            var value = element.Name.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase)
                ? $"Image: {(string?)element.Attribute("src") ?? "unassigned"}"
                : element.Value.Length == 0
                    ? (string?)element.Attribute("placeholder") ?? element.Name.LocalName
                    : element.Value;
            markup.Append(Escape(value));
        }
        markup.Append("</").Append(tag).Append('>');
    }

    private Dictionary<string, string> ComputedStyle(XElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _styleRules.OrderBy(value => value.Specificity)
                     .ThenBy(value => value.Order))
        {
            if (!Matches(element, rule.Selector)) continue;
            foreach (var declaration in rule.Declarations)
                result[declaration.Key] = declaration.Value;
        }
        foreach (var declaration in ParseDeclarations(
                     (string?)element.Attribute("style") ?? string.Empty))
            result[declaration.Key] = declaration.Value;
        return result;
    }

    private void RefreshDerivedState()
    {
        var diagnostics = new List<UiEditorDiagnostic>();
        var root = _document.Root;
        if (root is null || root.Name.LocalName != "rml")
            diagnostics.Add(new("error", "UI_RML_ROOT", "The document root must be <rml>"));
        if (Body() is null)
            diagnostics.Add(new("error", "UI_RML_BODY", "The document requires one <body>"));
        var elements = EditableElements().ToArray();
        foreach (var element in elements.Where(value => ElementId(value).Length == 0))
            diagnostics.Add(new("warning", "UI_ELEMENT_ID_REQUIRED",
                $"<{element.Name.LocalName}> has no stable id and cannot be selected"));
        var duplicate = elements.Select(ElementId).Where(value => value.Length != 0)
            .GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            diagnostics.Add(new("error", "UI_ELEMENT_ID_DUPLICATE",
                $"Element id '{duplicate.Key}' occurs more than once"));
        _styleRules = LoadStyleRules(diagnostics);
        _diagnostics = diagnostics;
    }

    private List<StyleRule> LoadStyleRules(List<UiEditorDiagnostic> diagnostics)
    {
        var result = new List<StyleRule>();
        var order = 0;
        var documentDirectory = System.IO.Path.GetDirectoryName(Path)!;
        foreach (var link in _document.Descendants().Where(element =>
                     element.Name.LocalName == "link" &&
                     string.Equals((string?)element.Attribute("type"), "text/rcss",
                         StringComparison.OrdinalIgnoreCase)))
        {
            var href = ((string?)link.Attribute("href") ?? string.Empty).Trim();
            if (href.Length == 0) continue;
            string fullPath;
            try
            {
                fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    documentDirectory, href.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                diagnostics.Add(new("error", "UI_STYLE_PATH", exception.Message));
                continue;
            }
            var ownerPrefix = _ownerRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar) +
                System.IO.Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(ownerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new("error", "UI_STYLE_ESCAPE",
                    $"Stylesheet '{href}' escapes its owning content root"));
                continue;
            }
            if (!File.Exists(fullPath))
            {
                diagnostics.Add(new("error", "UI_STYLE_MISSING",
                    $"Stylesheet '{href}' does not exist"));
                continue;
            }
            var source = Regex.Replace(File.ReadAllText(fullPath), @"/\*.*?\*/", string.Empty,
                RegexOptions.Singleline);
            foreach (Match match in Regex.Matches(source, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}"))
            {
                var declarations = ParseDeclarations(match.Groups["body"].Value);
                foreach (var selector in match.Groups["selector"].Value.Split(','))
                {
                    var normalized = selector.Trim();
                    if (normalized.Length == 0 || !IsSimpleSelector(normalized)) continue;
                    result.Add(new(normalized, declarations, Specificity(normalized), order++));
                }
            }
        }
        return result;
    }

    private bool Mutate(Action mutation)
    {
        var before = CanonicalState(_document);
        _undo.Push(before);
        _redo.Clear();
        mutation();
        if (CanonicalState(_document) == before)
        {
            _undo.Pop();
            return false;
        }
        Revision++;
        RefreshDerivedState();
        return true;
    }

    private void Restore(string state)
    {
        _document = XDocument.Parse(state, LoadOptions.PreserveWhitespace);
        if (SelectedElementId is not null && FindElement(SelectedElementId) is null)
            SelectedElementId = EditableElements().Select(ElementId)
                .FirstOrDefault(value => value.Length != 0);
        RefreshDerivedState();
    }

    private XElement? Body() => _document.Root?.Elements()
        .FirstOrDefault(element => element.Name.LocalName == "body");
    private IEnumerable<XElement> EditableElements() =>
        Body()?.Descendants() ?? Enumerable.Empty<XElement>();
    private XElement? SelectedElement() => SelectedElementId is null
        ? null : FindElement(SelectedElementId);
    private XElement? FindElement(string id) => EditableElements().FirstOrDefault(element =>
        string.Equals(ElementId(element), id, StringComparison.Ordinal));
    private static string ElementId(XElement element) =>
        ((string?)element.Attribute("id") ?? string.Empty).Trim();
    private static bool IsLeaf(XElement element) => element.Name.LocalName.ToLowerInvariant()
        is "span" or "label" or "p" or "h1" or "h2" or "h3" or "button" or
        "input" or "img";

    private string UniqueId(string prefix)
    {
        var ids = EditableElements().Select(ElementId).ToHashSet(StringComparer.Ordinal);
        if (!ids.Contains(prefix)) return prefix;
        for (var index = 2; ; index++)
        {
            var candidate = prefix + index.ToString(CultureInfo.InvariantCulture);
            if (!ids.Contains(candidate)) return candidate;
        }
    }

    private static void SetOrRemove(XElement element, string name, string value)
    {
        var normalized = value.Trim();
        element.SetAttributeValue(name, normalized.Length == 0 ? null : normalized);
    }

    private static Dictionary<string, string> ParseDeclarations(string source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in source.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf(':');
            if (separator <= 0) continue;
            var name = entry[..separator].Trim();
            var value = entry[(separator + 1)..].Trim();
            if (name.Length != 0 && value.Length != 0) result[name] = value;
        }
        return result;
    }

    private static string SerializeDeclarations(IReadOnlyDictionary<string, string> values) =>
        string.Join(" ", values.Select(value => $"{value.Key}: {value.Value};"));

    private static bool IsSimpleSelector(string selector) =>
        !selector.Any(char.IsWhiteSpace) && selector.IndexOfAny(['>', '+', '~', '[', ':']) < 0;

    private static int Specificity(string selector) =>
        selector.Count(character => character == '#') * 100 +
        selector.Count(character => character == '.') * 10 +
        (selector[0] is '#' or '.' ? 0 : 1);

    private static bool Matches(XElement element, string selector)
    {
        var id = ElementId(element);
        var classes = ((string?)element.Attribute("class") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var index = 0;
        if (selector[0] is not '#' and not '.')
        {
            while (index < selector.Length && selector[index] is not '#' and not '.') index++;
            if (!element.Name.LocalName.Equals(selector[..index], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        while (index < selector.Length)
        {
            var marker = selector[index++];
            var start = index;
            while (index < selector.Length && selector[index] is not '#' and not '.') index++;
            var value = selector[start..index];
            if (marker == '#' && !id.Equals(value, StringComparison.Ordinal)) return false;
            if (marker == '.' && !classes.Contains(value)) return false;
        }
        return true;
    }

    private static string CanonicalState(XDocument document) =>
        document.ToString(SaveOptions.DisableFormatting);
    private static string Escape(string value) => WebUtility.HtmlEncode(value);
    private static string EscapeAttribute(string value) => WebUtility.HtmlEncode(value);
}
