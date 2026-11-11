using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SharpEngine.Assets;

namespace SharpEngine.UI;

public readonly record struct UiLayoutContext(float Width, float Height, string Title = "");

/// <summary>A UI-domain composition slot, independent of asset editors and feature logic.</summary>
public interface IUiLayoutPart
{
    string BuildLayout(UiLayoutContext Context);
}

// Internal controls may retain validated XML fragments between renders.
internal interface IUiLayoutFragment : IUiLayoutPart
{
    void WriteTo(XmlWriter Writer, UiLayoutContext Context);
}

public interface IUiActionHandler
{
    bool TryHandleAction(string Action);
}

/// <summary>Immutable RML definition; every render has its own values, parts and XML nodes.</summary>
public sealed class UiLayoutTemplate
{
    private static readonly Regex Binding = new(@"\$\{([A-Za-z][A-Za-z0-9_.]*)\}", RegexOptions.CultureInvariant);
    private readonly XElement Definition;
    public UiDocumentAsset? Package
    {
        get;
        private set;
    }
    public IReadOnlyList<string> StyleSheets
    {
        get;
        private set;
    } = [];
    public string? SourcePath
    {
        get;
        private set;
    }

    public JsonObject DefaultValues
    {
        get
        {
            return UiLayoutUse.ReadValues((string?)Definition.Attribute("values"));
        }
    }

    public IReadOnlyList<UiLayoutUse> Uses
    {
        get
        {
            return Definition.Descendants("ui-use").Select(UiLayoutUse.Read).ToArray();
        }
    }

    public static UiLayoutTemplate FromAsset(UiDocumentAsset Package, string SourcePath)
    {
        var Template = Parse(Package.Rml);
        Template.Package = Package;
        Template.SourcePath = Path.GetFullPath(SourcePath);
        var Folder = Path.GetDirectoryName(SourcePath)!;
        Template.StyleSheets = Package.StyleSheets.Select(Href => Path.GetFullPath(Path.Combine(Folder, Href))).ToArray();
        return Template;
    }

    private UiLayoutTemplate(XElement Definition)
    {
        this.Definition = new XElement(Definition);
    }

    public static UiLayoutTemplate LoadEmbedded(Assembly Assembly, string ResourceName)
    {
        using var Stream = Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidDataException("UILAYOUT_RESOURCE_MISSING: " + ResourceName);
        using var Reader = new StreamReader(Stream);
        return Parse(Reader.ReadToEnd());
    }

    public static UiLayoutTemplate Parse(string Source)
    {
        using var Reader = XmlReader.Create(new StringReader(Source), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        var Root = XElement.Load(Reader);
        if (Root.Name != "template")
        {
            throw new InvalidDataException("UILAYOUT_ROOT_INVALID: Expected template");
        }
        return new UiLayoutTemplate(Root);
    }

    public UiLayoutInstance CreateInstance(string InstanceId) { return new UiLayoutInstance(Definition, InstanceId); }

    internal static UiLayoutTemplate FromElement(XElement Definition) { return new UiLayoutTemplate(Definition); }

    public string Render(UiLayoutContext Context, JsonObject Values,
        IReadOnlyDictionary<string, IUiLayoutPart>? Parts = null,
        Func<string, UiLayoutTemplate>? ResolveAsset = null)
    {
        return RenderCore(Context, Values, Parts, ResolveAsset, new HashSet<string>(StringComparer.Ordinal));
    }

    internal string RenderCore(UiLayoutContext Context, JsonObject Values,
        IReadOnlyDictionary<string, IUiLayoutPart>? Parts,
        Func<string, UiLayoutTemplate>? ResolveAsset, HashSet<string> Stack)
    {
        var EffectiveValues = DefaultValues;
        foreach (var Pair in Values)
        {
            EffectiveValues[Pair.Key] = Pair.Value?.DeepClone();
        }
        var Resolved = Definition.Descendants("ui-use").Any()
            ? UiLayoutComposition.Expand(Definition, Context, Parts, ResolveAsset, Stack) : Definition;
        var Output = new StringBuilder();
        using var Writer = XmlWriter.Create(Output, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            NewLineHandling = NewLineHandling.Entitize,
        });
        foreach (var Node in Resolved.Nodes())
        {
            RenderNode(Writer, Node, Context, [EffectiveValues], Parts);
        }
        Writer.Flush();
        return Output.ToString();
    }

    private static void RenderNode(XmlWriter Writer, XNode Node, UiLayoutContext Context,
        IReadOnlyList<JsonObject> Scopes, IReadOnlyDictionary<string, IUiLayoutPart>? Parts)
    {
        var Text = Node as XText;
        if (Text is not null)
        {
            Writer.WriteString(Expand(Text.Value, Scopes));
            return;
        }
        var Element = Node as XElement;
        if (Element is null)
        {
            return;
        }
        UiAuthoringMetadata.Validate(Element);
        if (Element.Annotation<UiLayoutComposition.ComposedNode>() is not null)
        {
            Element.WriteTo(Writer);
            return;
        }
        if (Element.Name == "ui-if")
        {
            CheckAttributes(Element, "test");
            var Condition = Lookup(Required(Element, "test"), Scopes);
            if (Condition?.GetValueKind() != JsonValueKind.True && Condition?.GetValueKind() != JsonValueKind.False)
            {
                throw new InvalidDataException("UILAYOUT_BOOLEAN_REQUIRED: " + Element);
            }
            if (!Condition.GetValue<bool>())
            {
                return;
            }
            foreach (var Child in Element.Nodes())
            {
                RenderNode(Writer, Child, Context, Scopes, Parts);
            }
            return;
        }
        if (Element.Name == "ui-repeat")
        {
            CheckAttributes(Element, "items");
            var Items = Lookup(Required(Element, "items"), Scopes) as JsonArray ??
                throw new InvalidDataException("UILAYOUT_ARRAY_REQUIRED: " + Element);
            foreach (var Item in Items)
            {
                var Values = Item as JsonObject ?? throw new InvalidDataException("UILAYOUT_OBJECT_REQUIRED");
                var Nested = Scopes.Append(Values).ToArray();
                foreach (var Child in Element.Nodes())
                {
                    RenderNode(Writer, Child, Context, Nested, Parts);
                }
            }
            return;
        }
        if (Element.Name == "ui-slot")
        {
            CheckAttributes(Element, "name", "title", "width", "height");
            if (Element.HasElements)
            {
                throw new InvalidDataException("UILAYOUT_SLOT_CONTENT_UNSUPPORTED");
            }
            var Name = Required(Element, "name");
            IUiLayoutPart? Part = null;
            var Found = Parts?.TryGetValue(Name, out Part) == true;
            if (!Found || Part is null)
            {
                throw new InvalidDataException("UILAYOUT_SLOT_UNBOUND: " + Name);
            }
            var Title = Element.Attribute("title") is null ? Context.Title : Expand(Element.Attribute("title")!.Value, Scopes);
            var Width = Dimension(Element, "width", Context.Width, Scopes);
            var Height = Dimension(Element, "height", Context.Height, Scopes);
            var Fragment = Part as IUiLayoutFragment;
            if (Fragment is not null)
            {
                Fragment.WriteTo(Writer, new(Width, Height, Title));
                return;
            }
            using var Reader = XmlReader.Create(new StringReader(Part.BuildLayout(new(Width, Height, Title))), new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Fragment,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
            });
            Reader.Read();
            while (!Reader.EOF)
            {
                if (Reader.Depth == 0 && Reader.NodeType is XmlNodeType.Comment or XmlNodeType.ProcessingInstruction)
                {
                    Reader.Read();
                    continue;
                }
                Writer.WriteNode(Reader, false);
            }
            return;
        }
        if (Element.Name.LocalName.StartsWith("ui-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("UILAYOUT_DIRECTIVE_UNKNOWN: " + Element.Name);
        }
        var Header = new XElement(Element.Name);
        foreach (var Attribute in Element.Attributes())
        {
            if (UiAuthoringMetadata.IsAttribute(Attribute.Name.LocalName))
            {
                Header.SetAttributeValue(Attribute.Name, Attribute.Value);
                continue;
            }
            if (Attribute.Name == "ui-disabled")
            {
                var Value = Lookup(Attribute.Value, Scopes);
                if (Value?.GetValueKind() != JsonValueKind.True && Value?.GetValueKind() != JsonValueKind.False)
                {
                    throw new InvalidDataException("UILAYOUT_BOOLEAN_REQUIRED: " + Attribute.Value);
                }
                if (Value.GetValue<bool>())
                {
                    Header.SetAttributeValue("disabled", "disabled");
                }
            }
            else
            {
                if (Attribute.Name.LocalName.StartsWith("ui-", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("UILAYOUT_DIRECTIVE_UNKNOWN: " + Attribute.Name);
                }
                Header.SetAttributeValue(Attribute.Name, Expand(Attribute.Value, Scopes));
            }
        }
        // Let LINQ to XML preserve namespace declarations and last-write-wins attributes.
        using var HeaderReader = Header.CreateReader();
        HeaderReader.Read();
        Writer.WriteStartElement(HeaderReader.Prefix, HeaderReader.LocalName, HeaderReader.NamespaceURI);
        Writer.WriteAttributes(HeaderReader, false);
        foreach (var Child in Element.Nodes())
        {
            RenderNode(Writer, Child, Context, Scopes, Parts);
        }
        Writer.WriteEndElement();
    }

    private static string Expand(string Value, IReadOnlyList<JsonObject> Scopes)
    {
        return Binding.Replace(Value, Match =>
        {
            var Node = Lookup(Match.Groups[1].Value, Scopes);
            return Node?.GetValueKind() switch
            {
                null or JsonValueKind.Null => string.Empty,
                JsonValueKind.String => Node.GetValue<string>(),
                JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number => Node.ToJsonString(),
                _ => throw new InvalidDataException("UILAYOUT_SCALAR_REQUIRED: " + Match.Value),
            };
        });
    }

    private static JsonNode? Lookup(string Path, IReadOnlyList<JsonObject> Scopes)
    {
        var Parts = Path.Split('.');
        for (var Index = Scopes.Count - 1; Index >= 0; Index--)
        {
            if (!Scopes[Index].ContainsKey(Parts[0]))
            {
                continue;
            }
            JsonNode? Node = Scopes[Index][Parts[0]];
            foreach (var Part in Parts.Skip(1))
            {
                var Object = Node as JsonObject;
                if (Object is null || !Object.ContainsKey(Part))
                {
                    throw new InvalidDataException("UILAYOUT_VALUE_UNBOUND: " + Path);
                }
                Node = Object[Part];
            }
            return Node;
        }
        throw new InvalidDataException("UILAYOUT_VALUE_UNBOUND: " + Path);
    }

    private static float Dimension(XElement Element, string Name, float Default, IReadOnlyList<JsonObject> Scopes)
    {
        var Value = Element.Attribute(Name)?.Value;
        if (Value is null)
        {
            return Default;
        }
        var Parsed = float.TryParse(Expand(Value, Scopes), NumberStyles.Float, CultureInfo.InvariantCulture, out var Result);
        if (!Parsed || !float.IsFinite(Result) || Result <= 0)
        {
            throw new InvalidDataException("UILAYOUT_DIMENSION_INVALID: " + Name);
        }
        return Result;
    }

    private static string Required(XElement Element, string Attribute)
    {
        return Element.Attribute(Attribute)?.Value ?? throw new InvalidDataException("UILAYOUT_ATTRIBUTE_REQUIRED: " + Attribute);
    }

    private static void CheckAttributes(XElement Element, params string[] Allowed)
    {
        if (Element.Attributes().Any(Attribute => !UiAuthoringMetadata.IsAttribute(Attribute.Name.LocalName) &&
            !Allowed.Contains(Attribute.Name.LocalName, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("UILAYOUT_ATTRIBUTE_UNKNOWN: " + Element.Name);
        }
    }

}
