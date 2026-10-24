using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SharpEngine.Editor;

internal sealed partial class UiEditorDocument
{
    private readonly HashSet<string> CollapsedTreeKeys = new(StringComparer.Ordinal);
    internal string TreeSearch
    {
        get;
        private set;
    } = string.Empty;

    internal string ControlLabel(XElement Element)
    {
        return ControlOf(Element)?.Label ?? Element.Name.LocalName switch
        {
            "ui-use" => "Reusable Control",
            "ui-slot" => "Slot",
            "ui-if" => "Condition",
            "ui-repeat" => "Repeat",
            "br" => "Line Break",
            _ => Element.Name.LocalName,
        };
    }

    internal string DisplayName(XElement Element)
    {
        var AuthoredName = AttributeOf(Element, "data-ui-name").Trim();
        if (AuthoredName.Length > 0)
        {
            return AuthoredName;
        }
        var Text = Element.HasElements ? string.Empty : Element.Value;
        Text = Regex.Replace(SampleText(Text), @"\s+", " ").Trim();
        if (Text.Length > 0)
        {
            return Text.Length > 60 ? Text[..57] + "..." : Text;
        }
        var Id = ElementId(Element);
        if (Id.Length > 0)
        {
            return Id;
        }
        var Class = ClassList(Element).FirstOrDefault();
        return Class ?? ControlLabel(Element);
    }

    internal void SetTreeSearch(string Search)
    {
        TreeSearch = Search.Trim();
    }

    internal void ToggleTree(string Key)
    {
        var Element = Find(Key);
        if (Element is null || !Element.HasElements || TreeSearch.Length > 0)
        {
            return;
        }
        if (!CollapsedTreeKeys.Remove(Key))
        {
            CollapsedTreeKeys.Add(Key);
        }
    }

    internal void ExpandTree(bool Expand)
    {
        CollapsedTreeKeys.Clear();
        if (!Expand)
        {
            foreach (var Element in EnumerateBody().Where(Element => Element.HasElements))
            {
                CollapsedTreeKeys.Add(KeyOf(Element));
            }
        }
    }

    internal bool TreeExpanded(XElement Element) => Element.HasElements &&
        (TreeSearch.Length > 0 || !CollapsedTreeKeys.Contains(KeyOf(Element)));

    internal IReadOnlyList<XElement> VisibleTreeElements()
    {
        var Included = TreeIncludedKeys();
        var Result = new List<XElement>();
        void Visit(XElement Element)
        {
            if (!Included.Contains(KeyOf(Element)))
            {
                return;
            }
            Result.Add(Element);
            if (TreeExpanded(Element))
            {
                foreach (var Child in Element.Elements())
                {
                    Visit(Child);
                }
            }
        }
        foreach (var Element in Body()?.Elements() ?? [])
        {
            Visit(Element);
        }
        return Result;
    }

    private HashSet<string> TreeIncludedKeys()
    {
        var Included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Element in EnumerateBody())
        {
            var Text = DisplayName(Element) + " " + ControlLabel(Element) + " " + ElementId(Element) + " " +
                string.Join(' ', ClassList(Element)) + " " + AttributeOf(Element, "asset");
            if (TreeSearch.Length > 0 && !Text.Contains(TreeSearch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Included.Add(KeyOf(Element));
            foreach (var Parent in Element.Ancestors().TakeWhile(Parent => Parent != Body()))
            {
                Included.Add(KeyOf(Parent));
            }
        }
        return Included;
    }

    private void RevealInTree(XElement Element)
    {
        if (TreeSearch.Length > 0 && !TreeIncludedKeys().Contains(KeyOf(Element)))
        {
            TreeSearch = string.Empty;
        }
        foreach (var Parent in Element.Ancestors().TakeWhile(Parent => Parent != Body()))
        {
            CollapsedTreeKeys.Remove(KeyOf(Parent));
        }
    }

    private JsonObject DescribeTree()
    {
        return new JsonObject
        {
            ["search"] = TreeSearch,
            ["collapsedKeys"] = new JsonArray(CollapsedTreeKeys.OrderBy(Key => Key, StringComparer.Ordinal)
                .Select(Key => (JsonNode)Key).ToArray()),
            ["visibleKeys"] = new JsonArray(VisibleTreeElements().Select(Element => (JsonNode)KeyOf(Element)).ToArray()),
        };
    }
}
