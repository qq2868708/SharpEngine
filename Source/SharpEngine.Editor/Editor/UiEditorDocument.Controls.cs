using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace SharpEngine.Editor;

internal sealed partial class UiEditorDocument
{
    private readonly Dictionary<XElement, UiControlDefinition?> ControlTypes = [];
    internal bool BasicControlStylesActive
    {
        get;
        private set;
    }

    private void RefreshBasicControlStyles()
    {
        BasicControlStylesActive = false;
        try
        {
            var StylePath = ResolveGlobalStyle?.Invoke(UiControlCatalog.DefaultStyleId);
            if (StylePath is null)
            {
                return;
            }
            var Folder = System.IO.Path.GetDirectoryName(Path)!;
            var ResolvedStylePath = System.IO.Path.GetFullPath(StylePath);
            BasicControlStylesActive = Sheet.Files.Any(FileValue => string.Equals(
                System.IO.Path.GetFullPath(System.IO.Path.Combine(Folder, FileValue)),
                ResolvedStylePath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception Error) when (Error is InvalidDataException or IOException)
        {
            // An unreferenced optional style is not a document dependency. Explicit
            // references are validated separately by ResolvedGlobalStyles.
        }
    }

    internal IReadOnlyList<string> DesignClasses(XElement Element)
    {
        return ClassList(Element).Where(ClassName => BasicControlStylesActive ||
            ClassName != "se-control" && !ClassName.StartsWith("se-control-", StringComparison.Ordinal)).ToArray();
    }

    internal UiControlDefinition? ControlOf(XElement Element)
    {
        var Found = ControlTypes.TryGetValue(Element, out var Control);
        if (!Found)
        {
            Control = UiControlCatalog.Identify(Element, Sheet);
            ControlTypes[Element] = Control;
        }
        return Control;
    }

    internal bool AddControl(string ControlId, out string Message)
    {
        Message = string.Empty;
        if (IsReadOnly)
        {
            Message = ReadOnlyMessage;
            return false;
        }
        var Definition = UiControlCatalog.Find(ControlId);
        if (Definition is null)
        {
            Message = "Unknown basic control: " + ControlId;
            return false;
        }
        var Root = Body();
        if (Root is null)
        {
            Message = "RML document has no content root";
            return false;
        }
        if (Format == UiDocumentFormat.Package)
        {
            try
            {
                _ = ResolveGlobalStyle?.Invoke(UiControlCatalog.DefaultStyleId) ??
                    throw new InvalidDataException("UI_GLOBAL_STYLE_RESOLVER_REQUIRED: " + UiControlCatalog.DefaultStyleId);
            }
            catch (Exception Error) when (Error is InvalidDataException or IOException)
            {
                Message = Error.Message;
                return false;
            }
        }
        var Selected = SelectedElement();
        var Parent = Selected is null ? Root : CanContain(Selected) ? Selected : Selected.Parent ?? Root;
        var Prefix = ControlId switch
        {
            "container" => "container",
            "text-input" => "input",
            "image" => "image",
            _ => ControlId,
        };
        var Id = UniqueId(Prefix);
        var Created = Definition.Create(Id);
        if (ControlId == "button")
        {
            Created.SetAttributeValue("data-command", Id);
        }
        return Edit("add " + Definition.Label, () =>
        {
            if (Format == UiDocumentFormat.Package && !GlobalStyleSheets.Contains(UiControlCatalog.DefaultStyleId, StringComparer.Ordinal))
            {
                GlobalStyleSheets = GlobalStyleSheets.Append(UiControlCatalog.DefaultStyleId).ToArray();
            }
            Parent.Add(Created);
            SelectionKey = KeyOf(Created);
            RevealInTree(Created);
        }, out Message);
    }

    internal bool SetControlProperty(string Name, string Value, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        var Definition = Element is null ? null : ControlOf(Element);
        var Property = Definition?.Properties.FirstOrDefault(Property => Property.Name == Name);
        if (Element is null || Definition is null || Property is null)
        {
            Message = "This property is not available on the selected control";
            return false;
        }
        if (Property.Kind == UiControlPropertyKind.Content)
        {
            return SetText(Value, out Message);
        }
        if (Property.Kind == UiControlPropertyKind.Style)
        {
            return SetStyle(Name, Value, out Message);
        }
        var Candidate = new XElement(Element);
        if (Property.Kind == UiControlPropertyKind.Boolean)
        {
            var Valid = bool.TryParse(Value, out var Enabled);
            if (!Valid)
            {
                Message = "The property requires true or false";
                return false;
            }
            Candidate.SetAttributeValue(Name, Enabled ? string.Empty : null);
        }
        else
        {
            Candidate.SetAttributeValue(Name, Value);
        }
        if (!UiControlCatalog.ValidateNumbers(Candidate, Definition.Id, out Message))
        {
            return false;
        }
        return Edit("control property:" + Name, () =>
            Element.SetAttributeValue(Name, (string?)Candidate.Attribute(Name)), out Message);
    }

    private JsonArray DescribeControlProperties(XElement Element)
    {
        return new JsonArray((ControlOf(Element)?.Properties ?? []).Select(Property => (JsonNode)new JsonObject
        {
            ["name"] = Property.Name,
            ["label"] = Property.Label,
            ["kind"] = Property.Kind.ToString(),
            ["value"] = UiControlCatalog.Read(Element, Property, Sheet),
        }).ToArray());
    }
}
