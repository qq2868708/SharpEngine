using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpEngine.UI;

namespace SharpEngine.Editor;

internal sealed record UiDesignSourceNode(string Key, string PreviewId, bool Derived);

internal sealed partial class UiEditorDocument
{
    private Func<string, UiLayoutTemplate>? ResolveUiAsset;
    private XElement? DesignBody;
    private readonly List<string> ReferencedStyleSheets = [];
    private readonly List<string> ReferencedUiSources = [];
    private readonly Dictionary<string, (long Modified, long Length)> DependencyVersions = new(StringComparer.OrdinalIgnoreCase);
    internal long DependencyRevision
    {
        get;
        private set;
    }

    internal XElement? DesignBodyElement => DesignBody ?? Body();

    internal bool RefreshChangedDependencies()
    {
        if (DependencyVersions.Any(Pair => FileVersion(Pair.Key) != Pair.Value))
        {
            DependencyRevision++;
            RefreshDerivedState();
            return true;
        }
        return false;
    }

    private static (long Modified, long Length) FileVersion(string PathValue)
    {
        var FileValue = new FileInfo(PathValue);
        return FileValue.Exists ? (FileValue.LastWriteTimeUtc.Ticks, FileValue.Length) : (0, 0);
    }

    private void TrackDependencies()
    {
        DependencyVersions.Clear();
        var Folder = System.IO.Path.GetDirectoryName(Path)!;
        foreach (var FileValue in StyleSheet.Files.Concat(PackageStyleSheets)
                     .Concat(ReferencedUiSources).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var FullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Folder, FileValue));
            DependencyVersions[FullPath] = FileVersion(FullPath);
        }
    }

    internal bool SetEnabled(bool Enabled, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element first";
            return false;
        }
        return Edit("enabled", () => SetOrRemove(Element, "disabled", Enabled ? null : "disabled"), out Message);
    }

    internal void SetAssetResolver(Func<string, UiLayoutTemplate>? Resolver)
    {
        ResolveUiAsset = Resolver;
        RefreshDerivedState();
    }

    internal JsonObject ParametersOf(XElement? Element)
    {
        return UiLayoutUse.ReadValues((string?)(Element ?? Document.Root)?.Attribute("values"));
    }

    internal bool SetParameter(string Name, string Value, bool Template, out string Message)
    {
        Message = string.Empty;
        var Target = Template ? Document.Root : SelectedElement();
        if (Target is null || (!Template && Target.Name.LocalName != "ui-use"))
        {
            Message = "Select a reusable control before editing its parameters";
            return false;
        }
        var Values = ParametersOf(Target);
        var Previous = Values[Name];
        JsonNode? Parsed;
        try
        {
            Parsed = Previous?.GetValueKind() == JsonValueKind.String
                ? JsonValue.Create(Value) : JsonNode.Parse(Value);
            if (Previous is not null && Parsed?.GetValueKind() != Previous.GetValueKind())
            {
                Message = "The parameter must retain its declared JSON type";
                return false;
            }
        }
        catch (JsonException Error)
        {
            Message = Error.Message;
            return false;
        }
        Values[Name] = Parsed;
        return Edit("parameter:" + Name, () => Target.SetAttributeValue("values", Values.ToJsonString()), out Message);
    }

    internal bool SetUseAsset(string AssetId, out string Message)
    {
        Message = string.Empty;
        var Target = SelectedElement();
        if (Target?.Name.LocalName != "ui-use")
        {
            Message = "Select a reusable control first";
            return false;
        }
        try
        {
            _ = ResolveUiAsset?.Invoke(AssetId) ?? throw new InvalidDataException("No UI asset resolver is available");
        }
        catch (Exception Error) when (Error is InvalidDataException or IOException)
        {
            Message = Error.Message;
            return false;
        }
        return Edit("referenced UI asset", () => Target.SetAttributeValue("asset", AssetId), out Message);
    }

    internal bool AddUse(string AssetId, out string Message)
    {
        Message = string.Empty;
        if (!IsTemplate)
        {
            Message = "Reusable controls require a template document";
            return false;
        }
        UiLayoutTemplate Child;
        try
        {
            Child = ResolveUiAsset?.Invoke(AssetId) ?? throw new InvalidDataException("No UI asset resolver is available");
        }
        catch (Exception Error) when (Error is InvalidDataException or IOException)
        {
            Message = Error.Message;
            return false;
        }
        var Selected = SelectedElement();
        var Parent = Selected is not null && CanContain(Selected) ? Selected : Selected?.Parent ?? Body();
        if (Parent is null)
        {
            Message = "The document has no content root";
            return false;
        }
        var Index = 1;
        var Id = "control" + Index;
        while (EnumerateBody().Any(Element => ElementId(Element) == Id))
        {
            Id = "control" + ++Index;
        }
        var Values = Child.DefaultValues;
        if (Values["Id"]?.GetValueKind() == JsonValueKind.String)
        {
            Values["Id"] = Id;
        }
        var Added = new XElement("ui-use", new XAttribute("id", Id),
            new XAttribute("asset", AssetId), new XAttribute("values", Values.ToJsonString()));
        return Edit("add reusable control", () =>
        {
            Parent.Add(Added);
            SelectionKey = "#" + Id;
        }, out Message);
    }

    private void BuildDesignComposition(List<UiEditorDiagnostic> Diagnostics)
    {
        ReferencedStyleSheets.Clear();
        ReferencedUiSources.Clear();
        ChildPrivateStyles.Clear();
        var BodyValue = Body();
        if (BodyValue is null)
        {
            DesignBody = null;
            return;
        }
        DesignBody = new XElement(BodyValue);
        var Original = BodyValue.Descendants().ToArray();
        var Cloned = DesignBody.Descendants().ToArray();
        for (var Index = 0; Index < Original.Length; Index++)
        {
            Cloned[Index].AddAnnotation(new UiDesignSourceNode(KeyOf(Original[Index]), PreviewNodeId(Original[Index]), false));
        }
        JsonObject Values;
        try
        {
            Values = ParametersOf(Document.Root);
        }
        catch (InvalidDataException Error)
        {
            Diagnostics.Add(new("error", "UI_TEMPLATE_VALUES_INVALID", Error.Message));
            return;
        }
        foreach (var Element in DesignBody.Descendants("ui-use").ToArray())
        {
            try
            {
                var Use = UiLayoutUse.Read(Element);
                UiLayoutTemplate Resolve(string AssetId)
                {
                    var Child = ResolveUiAsset?.Invoke(AssetId) ??
                        throw new InvalidDataException("UILAYOUT_ASSET_RESOLVER_REQUIRED: " + AssetId);
                    ReferencedStyleSheets.AddRange(Child.StyleSheets);
                    if (Child.Package is not null)
                    {
                        foreach (var GlobalId in Child.Package.GlobalStyleSheets)
                        {
                            var StylePath = ResolveGlobalStyle?.Invoke(GlobalId) ??
                                throw new InvalidDataException("UI_GLOBAL_STYLE_RESOLVER_REQUIRED: " + GlobalId);
                            ReferencedStyleSheets.Add(StylePath);
                        }
                        if (Child.Package.Rcss.Length > 0 && Child.SourcePath is not null &&
                            !ChildPrivateStyles.Any(Style => Style.SourcePath == Child.SourcePath))
                        {
                            ChildPrivateStyles.Add(new UiPrivateStyle(Child.SourcePath, Child.Package.Rcss));
                        }
                    }
                    if (Child.SourcePath is not null)
                    {
                        ReferencedUiSources.Add(Child.SourcePath);
                    }
                    return Child;
                }
                var Child = Resolve(Use.AssetId);
                var Markup = Child.Render(new UiLayoutContext(DesignWidth, DesignHeight), Use.Values, ResolveAsset: Resolve);
                var SourceNode = Element.Annotation<UiDesignSourceNode>()!;
                var Wrapper = new XElement("div", Element.Attributes().Where(Attribute =>
                    Attribute.Name.LocalName is "id" or "class" or "style"));
                Wrapper.AddAnnotation(SourceNode);
                var Fragment = XElement.Parse("<fragment>" + Markup + "</fragment>");
                Wrapper.Add(Fragment.Nodes());
                var ChildIndex = 0;
                foreach (var Derived in Wrapper.Descendants())
                {
                    Derived.AddAnnotation(new UiDesignSourceNode(SourceNode.Key,
                        SourceNode.PreviewId + "-child-" + ChildIndex++, true));
                }
                Element.ReplaceWith(Wrapper);
            }
            catch (Exception Error) when (Error is InvalidDataException or IOException or JsonException)
            {
                Diagnostics.Add(new("error", "UI_USE_INVALID", Error.Message));
            }
        }
        string Expand(string Text)
        {
            return Regex.Replace(Text, @"\$\{([A-Za-z][A-Za-z0-9_.]*)\}", Match =>
            {
                var Value = Values[Match.Groups[1].Value];
                return Value?.GetValueKind() == JsonValueKind.String ? Value.GetValue<string>() :
                    Value?.ToJsonString() ?? Match.Groups[1].Value;
            });
        }
        foreach (var Element in DesignBody.Descendants().Where(Element =>
                     Element.Annotation<UiDesignSourceNode>()?.Derived != true))
        {
            foreach (var Attribute in Element.Attributes().ToArray())
            {
                Attribute.Value = Expand(Attribute.Value);
            }
            foreach (var Text in Element.Nodes().OfType<XText>())
            {
                Text.Value = Expand(Text.Value);
            }
        }
        var Duplicate = DesignBody.Descendants().Select(Element => (string?)Element.Attribute("id"))
            .Where(Id => !string.IsNullOrWhiteSpace(Id)).GroupBy(Id => Id, StringComparer.Ordinal)
            .FirstOrDefault(Group => Group.Count() > 1);
        if (Duplicate is not null)
        {
            Diagnostics.Add(new("error", "UI_USE_ELEMENT_ID_DUPLICATE",
                "Composed UI repeats element id '" + Duplicate.Key + "'; give each use independent identity parameters"));
        }
    }
}
