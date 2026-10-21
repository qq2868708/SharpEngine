using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using SharpEngine.UI;

namespace SharpEngine.Editor.Layout;

/// <summary>Immutable editor layout definition: Dock composition plus UI-domain child slots.</summary>
public sealed class AssetEditorLayoutDefinition
{
    private readonly XElement Dock;
    private readonly Dictionary<string, UiLayoutTemplate> PaneLayouts;

    private AssetEditorLayoutDefinition(XElement Root)
    {
        if (Root.Name != "asset-editor-layout" || Required(Root, "schema-version") != "1")
        {
            throw new InvalidDataException("ASSET_LAYOUT_VERSION_INVALID");
        }
        CheckAttributes(Root, "schema-version", "primary-pane");
        if (Root.Elements().Any(Element => Element.Name != "dock" && Element.Name != "panes") ||
            Root.Elements("dock").Count() != 1 || Root.Elements("panes").Count() != 1)
        {
            throw new InvalidDataException("ASSET_LAYOUT_STRUCTURE_INVALID");
        }
        var DockRoot = Root.Element("dock")!;
        CheckAttributes(DockRoot);
        if (DockRoot.Elements().Count() != 1)
        {
            throw new InvalidDataException("ASSET_LAYOUT_DOCK_ROOT_REQUIRED");
        }
        Dock = new XElement(DockRoot.Elements().Single());
        var PaneRoot = Root.Element("panes")!;
        CheckAttributes(PaneRoot);
        if (PaneRoot.Elements().Any(Element => Element.Name != "pane"))
        {
            throw new InvalidDataException("ASSET_LAYOUT_PANE_INVALID");
        }
        var Definitions = new List<AssetEditorPaneDefinition>();
        PaneLayouts = new Dictionary<string, UiLayoutTemplate>(StringComparer.Ordinal);
        foreach (var Pane in PaneRoot.Elements("pane"))
        {
            CheckAttributes(Pane, "id", "label", "closable");
            var Id = Required(Pane, "id");
            var ParsedClosable = bool.TryParse(Required(Pane, "closable"), out var Closable);
            if (!ParsedClosable || PaneLayouts.ContainsKey(Id))
            {
                throw new InvalidDataException("ASSET_LAYOUT_PANE_DUPLICATE_OR_INVALID: " + Id);
            }
            Definitions.Add(new(Id, Required(Pane, "label"), Closable));
            PaneLayouts.Add(Id, UiLayoutTemplate.Parse(new XElement("template", Pane.Nodes()).ToString()));
        }
        Panes = Definitions.AsReadOnly();
        PrimaryPaneId = Required(Root, "primary-pane");
        if (Definitions.All(Pane => Pane.PaneId != PrimaryPaneId || Pane.Closable))
        {
            throw new InvalidDataException("ASSET_LAYOUT_PRIMARY_PANE_INVALID");
        }
        _ = CreateDockLayout();
    }

    public string PrimaryPaneId
    {
        get;
    }
    public IReadOnlyList<AssetEditorPaneDefinition> Panes
    {
        get;
    }

    public static AssetEditorLayoutDefinition LoadEmbedded(Assembly Assembly, string ResourceName)
    {
        using var Stream = Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidDataException("ASSET_LAYOUT_RESOURCE_MISSING: " + ResourceName);
        using var Reader = new StreamReader(Stream);
        return Parse(Reader.ReadToEnd());
    }

    public static AssetEditorLayoutDefinition Parse(string Source)
    {
        using var Reader = XmlReader.Create(new StringReader(Source), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        return new AssetEditorLayoutDefinition(XElement.Load(Reader));
    }

    public EditorLayoutDocument CreateDockLayout()
    {
        var Nodes = new HashSet<string>(StringComparer.Ordinal);
        var Groups = new HashSet<string>(StringComparer.Ordinal);
        var UsedPanes = new HashSet<string>(StringComparer.Ordinal);
        var Root = ParseDock(Dock, Nodes, Groups, UsedPanes);
        if (!UsedPanes.SetEquals(PaneLayouts.Keys))
        {
            throw new InvalidDataException("ASSET_LAYOUT_PANES_UNREACHABLE");
        }
        return new EditorLayoutDocument
        {
            MainHost = new DockHostState
            {
                HostId = "main",
                Root = Root,
            },
            HiddenPaneIds = [],
            ActiveContentManagerId = string.Empty,
            ContentManagers = [],
        };
    }

    public AssetEditorLayoutInstance CreateInstance(string InstanceId)
    {
        return new AssetEditorLayoutInstance(this, PaneLayouts.ToDictionary(Pair => Pair.Key,
            Pair => Pair.Value.CreateInstance(InstanceId + "/" + Pair.Key), StringComparer.Ordinal));
    }

    public string RenderPane(string PaneId, UiLayoutContext Context, JsonObject Values,
        IReadOnlyDictionary<string, IUiLayoutPart> Parts)
    {
        var Found = PaneLayouts.TryGetValue(PaneId, out var Template);
        if (!Found || Template is null)
        {
            throw new InvalidDataException("ASSET_LAYOUT_PANE_UNKNOWN: " + PaneId);
        }
        return Template.Render(Context, Values, Parts);
    }

    private DockNodeState ParseDock(XElement Element, HashSet<string> Nodes,
        HashSet<string> Groups, HashSet<string> UsedPanes)
    {
        var Id = Required(Element, "id");
        if (!Nodes.Add(Id))
        {
            throw new InvalidDataException("ASSET_LAYOUT_NODE_DUPLICATE: " + Id);
        }
        if (Element.Name == "split")
        {
            CheckAttributes(Element, "id", "orientation", "ratio");
            var Orientation = Required(Element, "orientation") switch
            {
                "horizontal" => DockSplitOrientation.Horizontal,
                "vertical" => DockSplitOrientation.Vertical,
                _ => throw new InvalidDataException("ASSET_LAYOUT_ORIENTATION_INVALID"),
            };
            var Parsed = float.TryParse(Required(Element, "ratio"), NumberStyles.Float, CultureInfo.InvariantCulture, out var Ratio);
            var Children = Element.Elements().ToArray();
            if (!Parsed || !float.IsFinite(Ratio) || Ratio < 0.05f || Ratio > 0.95f || Children.Length != 2)
            {
                throw new InvalidDataException("ASSET_LAYOUT_SPLIT_INVALID: " + Id);
            }
            return new DockNodeState
            {
                NodeId = Id,
                Kind = DockNodeKind.Split,
                Orientation = Orientation,
                Ratio = Ratio,
                First = ParseDock(Children[0], Nodes, Groups, UsedPanes),
                Second = ParseDock(Children[1], Nodes, Groups, UsedPanes),
            };
        }
        if (Element.Name != "tabs")
        {
            throw new InvalidDataException("ASSET_LAYOUT_NODE_INVALID: " + Element.Name);
        }
        CheckAttributes(Element, "id", "group", "active");
        var Group = Required(Element, "group");
        var Active = Required(Element, "active");
        var Tabs = new List<DockPaneState>();
        foreach (var Tab in Element.Elements())
        {
            CheckAttributes(Tab, "pane");
            var Pane = Required(Tab, "pane");
            if (Tab.Name != "tab" || Tab.HasElements || !PaneLayouts.ContainsKey(Pane) || !UsedPanes.Add(Pane))
            {
                throw new InvalidDataException("ASSET_LAYOUT_TAB_INVALID: " + Pane);
            }
            Tabs.Add(new DockPaneState
            {
                PaneId = Pane,
            });
        }
        if (!Groups.Add(Group) || Tabs.All(Tab => Tab.PaneId != Active))
        {
            throw new InvalidDataException("ASSET_LAYOUT_TAB_GROUP_INVALID: " + Group);
        }
        return new DockNodeState
        {
            NodeId = Id,
            Kind = DockNodeKind.Tabs,
            TabGroup = new DockTabGroupState
            {
                GroupId = Group,
                ActivePaneId = Active,
                Panes = Tabs,
            },
        };
    }

    private static string Required(XElement Element, string Name)
    {
        var Value = Element.Attribute(Name)?.Value;
        return !string.IsNullOrWhiteSpace(Value) ? Value : throw new InvalidDataException("ASSET_LAYOUT_ATTRIBUTE_REQUIRED: " + Name);
    }

    private static void CheckAttributes(XElement Element, params string[] Allowed)
    {
        if (Element.Attributes().Any(Attribute => !Allowed.Contains(Attribute.Name.LocalName, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("ASSET_LAYOUT_ATTRIBUTE_UNKNOWN: " + Element.Name);
        }
    }
}

/// <summary>Mutable runtime UI belongs to this use of an editor layout, independently of source and Dock authoring.</summary>
public sealed class AssetEditorLayoutInstance : IDisposable, IUiActionHandler
{
    private readonly IReadOnlyDictionary<string, UiLayoutInstance> Panes;

    internal AssetEditorLayoutInstance(AssetEditorLayoutDefinition Definition, IReadOnlyDictionary<string, UiLayoutInstance> Panes)
    {
        this.Panes = Panes;
    }

    public UiLayoutInstance GetPane(string PaneId)
    {
        var Found = Panes.TryGetValue(PaneId, out var Pane);
        return Found ? Pane! : throw new ArgumentException("ASSET_LAYOUT_PANE_UNKNOWN: " + PaneId);
    }

    public string RenderPane(string PaneId, UiLayoutContext Context, JsonObject Values,
        IReadOnlyDictionary<string, IUiLayoutPart> Parts)
    {
        return GetPane(PaneId).Render(Context, Values, Parts);
    }

    public bool TryHandleAction(string Action)
    {
        return Panes.Values.Any(Pane => Pane.TryHandleAction(Action));
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["panes"] = new JsonObject(Panes.Select(Pair => new KeyValuePair<string, JsonNode?>(Pair.Key, Pair.Value.Describe()))),
        };
    }

    public void Dispose()
    {
        foreach (var Pane in Panes.Values)
        {
            Pane.Dispose();
        }
    }
}
