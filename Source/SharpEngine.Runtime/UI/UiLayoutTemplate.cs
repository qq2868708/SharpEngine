using System.Globalization;
using System.Reflection;
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
        var Resolved = UiLayoutComposition.Expand(Definition, Context, Parts, ResolveAsset, Stack);
        var Output = new XElement("fragment");
        foreach (var Node in Resolved.Nodes())
        {
            foreach (var Rendered in RenderNode(Node, Context, [EffectiveValues], Parts))
            {
                Output.Add(Rendered);
            }
        }
        return string.Concat(Output.Nodes().Select(Node => Node.ToString(SaveOptions.DisableFormatting)));
    }

    private static IEnumerable<XNode> RenderNode(XNode Node, UiLayoutContext Context,
        IReadOnlyList<JsonObject> Scopes, IReadOnlyDictionary<string, IUiLayoutPart>? Parts)
    {
        var Text = Node as XText;
        if (Text is not null)
        {
            yield return new XText(Expand(Text.Value, Scopes));
            yield break;
        }
        var Element = Node as XElement;
        if (Element is null)
        {
            yield break;
        }
        if (Element.Annotation<UiLayoutComposition.ComposedNode>() is not null)
        {
            yield return new XElement(Element);
            yield break;
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
                yield break;
            }
            foreach (var Child in Element.Nodes())
            {
                foreach (var Rendered in RenderNode(Child, Context, Scopes, Parts))
                {
                    yield return Rendered;
                }
            }
            yield break;
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
                    foreach (var Rendered in RenderNode(Child, Context, Nested, Parts))
                    {
                        yield return Rendered;
                    }
                }
            }
            yield break;
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
            var Fragment = Parse("<template>" + Part.BuildLayout(new(Width, Height, Title)) + "</template>");
            foreach (var Child in Fragment.Definition.Nodes())
            {
                yield return Clone(Child);
            }
            yield break;
        }
        if (Element.Name.LocalName.StartsWith("ui-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("UILAYOUT_DIRECTIVE_UNKNOWN: " + Element.Name);
        }
        var Result = new XElement(Element.Name);
        foreach (var Attribute in Element.Attributes())
        {
            if (Attribute.Name == "ui-disabled")
            {
                var Value = Lookup(Attribute.Value, Scopes);
                if (Value?.GetValueKind() != JsonValueKind.True && Value?.GetValueKind() != JsonValueKind.False)
                {
                    throw new InvalidDataException("UILAYOUT_BOOLEAN_REQUIRED: " + Attribute.Value);
                }
                if (Value.GetValue<bool>())
                {
                    Result.SetAttributeValue("disabled", "disabled");
                }
            }
            else
            {
                if (Attribute.Name.LocalName.StartsWith("ui-", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("UILAYOUT_DIRECTIVE_UNKNOWN: " + Attribute.Name);
                }
                Result.SetAttributeValue(Attribute.Name, Expand(Attribute.Value, Scopes));
            }
        }
        foreach (var Child in Element.Nodes())
        {
            foreach (var Rendered in RenderNode(Child, Context, Scopes, Parts))
            {
                Result.Add(Rendered);
            }
        }
        yield return Result;
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
        if (Element.Attributes().Any(Attribute => !Allowed.Contains(Attribute.Name.LocalName, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("UILAYOUT_ATTRIBUTE_UNKNOWN: " + Element.Name);
        }
    }

    private static XNode Clone(XNode Node)
    {
        return Node switch
        {
            XElement Element => new XElement(Element),
            XText Text => new XText(Text.Value),
            _ => new XText(string.Empty),
        };
    }
}
