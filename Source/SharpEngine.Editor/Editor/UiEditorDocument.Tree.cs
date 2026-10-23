using System.Globalization;
using System.Xml.Linq;

namespace SharpEngine.Editor;

internal sealed partial class UiEditorDocument
{
    private const string PreviewIdPrefix = "ui-design-node-";

    public XElement? BodyElement => Body();

    internal static string KeyOf(XElement Element) => ElementId(Element).Length != 0
        ? "#" + ElementId(Element)
        : "path:" + PathOf(Element);

    internal static string ElementIdOf(XElement Element) => ElementId(Element);

    internal static string PreviewNodeIdOf(XElement Element) => PreviewNodeId(Element);

    internal static int DepthOf(XElement Element) => Depth(Element);

    internal static string AttributeOf(XElement Element, string Name) =>
        UiStyleText.Attribute(Element, Name);

    internal static IReadOnlyList<string> ClassList(XElement Element) =>
        UiStyleText.ClassList(Element).OrderBy(Name => Name, StringComparer.Ordinal)
            .ToArray();

    internal static Dictionary<string, string> DeclarationsOf(XElement Element)
    {
        var Result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var Declaration in UiStyleText.ParseDeclarations(
                     UiStyleText.Attribute(Element, "style")))
        {
            Result[Declaration.Name] = Declaration.Value;
        }
        return Result;
    }

    private XElement? Body() => Document.Root?.Elements()
        .FirstOrDefault(Element => Element.Name.LocalName == "body");

    private IEnumerable<XElement> EnumerateBody() =>
        Body()?.Descendants() ?? Enumerable.Empty<XElement>();

    internal XElement? SelectedElement() =>
        SelectedKey is null ? null : Find(SelectedKey);

    private XElement? Find(string Key) => FindElement(Document, Key);

    private static XElement? FindElement(XDocument Source, string Key)
    {
        if (Key.StartsWith('#'))
        {
            var Id = Key[1..];
            return Source.Descendants().FirstOrDefault(Element =>
                IsBodyContent(Element) &&
                string.Equals(ElementId(Element), Id, StringComparison.Ordinal));
        }
        if (!Key.StartsWith("path:", StringComparison.Ordinal))
        {
            return null;
        }
        var Indexes = Key[5..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (Indexes.Length == 0)
        {
            return null;
        }
        var Current = Source.Root?.Elements()
            .FirstOrDefault(Element => Element.Name.LocalName == "body");
        if (Current is null)
        {
            return null;
        }
        foreach (var Index in Indexes)
        {
            if (!int.TryParse(Index, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var Position))
            {
                return null;
            }
            var Children = Current.Elements().ToArray();
            if (Position < 0 || Position >= Children.Length)
            {
                return null;
            }
            Current = Children[Position];
        }
        return Current;
    }

    private static bool IsBodyContent(XElement Element) =>
        Element.Name.LocalName is not ("rml" or "head" or "body");

    /// <summary>Selection key. An element with an id is addressed by that id, and an
    /// anonymous element by the child indexes of its body path, so a key never depends
    /// on memory addresses or on unrelated element count changes.</summary>
    private static string PathOf(XElement Element)
    {
        var Indexes = new List<int>();
        var Current = Element;
        while (Current.Parent is not null)
        {
            var Parent = Current.Parent;
            if (Parent.Name.LocalName is "rml" or "head")
            {
                return string.Empty;
            }
            Indexes.Add(Parent.Elements().ToList().IndexOf(Current));
            if (Parent.Name.LocalName == "body")
            {
                break;
            }
            Current = Parent;
        }
        Indexes.Reverse();
        return string.Join('.', Indexes);
    }

    private static string ElementId(XElement Element) =>
        ((string?)Element.Attribute("id") ?? string.Empty).Trim();

    private static int Depth(XElement Element)
    {
        var Value = 0;
        for (var Current = Element.Parent; Current is not null;
             Current = Current.Parent)
        {
            if (Current.Name.LocalName is "rml" or "head" or "body")
            {
                break;
            }
            Value++;
        }
        return Value;
    }

    /// <summary>Deterministic native element id for one authored element on the design
    /// surface. It depends only on document structure.</summary>
    private static string PreviewNodeId(XElement Element)
    {
        var Path = PathOf(Element);
        return PreviewIdPrefix + (Path.Length == 0 ? "root" :
            Path.Replace('.', '-'));
    }
}
