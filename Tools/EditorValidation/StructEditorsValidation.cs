using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Editor;

internal static class StructEditorsValidation
{
    internal static void RunSettings(EditorRuntime Runtime, Action<bool, string> Check)
    {
        XElement Markup()
        {
            return XElement.Parse("<div>" + Runtime.BuildSettingsMarkup(
                SharpEngine.Settings.SettingsScope.Project) + "</div>");
        }
        XElement Element(string Id)
        {
            return Markup().Descendants().Single(Node => Node.Attribute("id")?.Value == Id);
        }
        ValidationProjectSettings Value()
        {
            return SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>();
        }
        string Commit(string Id)
        {
            return Element(Id).Attribute("data-value-commit")!.Value;
        }
        var GroupId = "setting-struct-" + Uri.EscapeDataString("Project:validation.gameplay:structure:[]");
        Check(Element(GroupId + "-toggle").Attribute("aria-expanded")!.Value == "true" &&
            Element("setting-structure/offset-0").Attribute("value")!.Value == "1",
            "Settings structs expand with shared groups and typed Vector controls");
        Runtime.InvokeUiAction(Commit("setting-structure/power") + "12");
        Check(Value().Structure.Power == 12 && Value().Structure.Offset == new Vector3(1, 2, 3),
            "Settings struct writes preserve value-type siblings through typed reconstruction");
        Runtime.InvokeUiAction(Commit("setting-structure/offset-1") + "8");
        Check(Value().Structure.Offset == new Vector3(1, 8, 3),
            "Settings struct Vector fields edit one axis through the shared transaction");
        Runtime.InvokeUiAction(Element(GroupId + "-toggle").Attribute("data-action")!.Value);
        Check(!Runtime.BuildSettingsMarkup(SharpEngine.Settings.SettingsScope.Project)
                .Contains("setting-structure/power", StringComparison.Ordinal),
            "Settings struct folding hides child editors");
        Runtime.InvokeUiAction("editor.settings-undo:project:validation.gameplay");
        Check(Value().Structure.Offset == new Vector3(1, 2, 3) && Value().Structure.Power == 12,
            "Folding a Settings struct does not enter its Undo history");
        Runtime.InvokeUiAction(Element(GroupId + "-toggle").Attribute("data-action")!.Value);
        var CollectionId = "setting-collection-" + Uri.EscapeDataString("Project:validation.gameplay:structures:");
        Runtime.InvokeUiAction(Commit(CollectionId + "-value-0-power") + "15");
        Check(Value().Structures[0].Power == 15,
            "Settings list struct elements reuse shared groups and commit typed values");
        var MapId = "setting-map-" + Uri.EscapeDataString("Project:validation.gameplay:structMap:[]");
        var EntryId = MapId + "-entry-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("entry")))[..10];
        Runtime.InvokeUiAction(Commit(EntryId + "-value-power") + "16");
        Check(Value().StructMap["entry"].Power == 16,
            "Settings Map struct values commit their annotated members");
        var SamplesId = "setting-collection-" + Uri.EscapeDataString(
            "Project:validation.gameplay:structMap:entry/samples");
        Runtime.InvokeUiAction(Element(SamplesId + "-add").Attribute("data-action")!.Value);
        Check(Value().StructMap["entry"].Samples.SequenceEqual(new[] { 4, 0 }),
            "Collections nested in Settings Map structs retain the root transaction guard");
    }

    internal static void Run(EditorRuntime Runtime, string Folder, Action<bool, string> Check)
    {
        ValidateInspector(Runtime, Check);
        Runtime.RegisteredDataAssetTypes.Register<StructFixture>();
        var Asset = new AssetDescriptor
        {
            Id = "project://validation/structs",
            Type = "Validation.Structs",
            Name = "Structures",
            Source = AssetSource.Project,
        };
        var FilePath = Path.Combine(Folder, "Structures.sdataasset");
        var Original = DataAssetSerializer.Serialize(new StructFixture(), Asset.Id, Asset.Name);
        File.WriteAllText(FilePath, Original);
        var Context = new EditorAssetOpenContext(Runtime, Asset, FilePath,
            "data-asset-editor", "struct-validation");
        using var Editor = new DataAssetEditorInstance(Context);
        XElement Markup() { return XElement.Parse(Editor.BuildPaneMarkup("details")); }
        JsonObject State() { return Editor.Describe()["document"]!.AsObject(); }
        string StructId(params string[] Path)
        {
            return "dataAsset-struct-" + Uri.EscapeDataString(
                new JsonArray(Path.Select(Segment => (JsonNode?)JsonValue.Create(Segment)).ToArray()).ToJsonString());
        }
        XElement Input(string Label, string[] Path)
        {
            var Group = Markup().DescendantsAndSelf().Single(Element =>
                Element.Attribute("id")?.Value == StructId(Path));
            return Group.Descendants().Single(Element =>
                Element.Attribute("id")?.Value == StructId(Path) + "-" + Label);
        }
        string Commit(string Label, string[] Path)
        {
            return Input(Label, Path).Attribute("data-value-commit")!.Value;
        }
        void Edit(string Label, string Text, params string[] Path)
        {
            Editor.TryHandleAction(Commit(Label, Path) + Text);
        }
        var GroupId = StructId("configuration");
        Check(Markup().Descendants().Count(Element =>
                Element.Attribute("class")?.Value.Split(' ').Contains("se-struct") == true) >= 6 &&
            Input("power", new[] { "configuration" }).Attribute("value")!.Value == "5" &&
            !Editor.BuildPaneMarkup("details").Contains("IgnoredValue", StringComparison.Ordinal),
            "DataAsset expands custom structs at the root, in lists and Map values using serialized member names");
        Check(Input("computed", new[] { "configuration" }).Attribute("disabled") is not null &&
            Input("includedField", new[] { "configuration" }).Attribute("value")!.Value == "9",
            "Serialized read-only struct members stay disabled and JsonInclude fields use their existing contract");
        Check(Input("power", new[] { "optional" }).Attribute("value")!.Value == "3" &&
            !Markup().Descendants().Any(Element => Element.Attribute("id")?.Value == StructId("empty")),
            "Non-null nullable structs expand while null structs retain their existing null representation");
        var Cached = Editor.BuildPaneMarkup("details");
        var BeforeAllocations = GC.GetAllocatedBytesForCurrentThread();
        for (var Index = 0; Index < 1000; Index++)
        {
            _ = Editor.BuildPaneMarkup("details");
        }
        Check(ReferenceEquals(Cached, Editor.BuildPaneMarkup("details")) &&
            GC.GetAllocatedBytesForCurrentThread() - BeforeAllocations < 4096,
            "Unchanged struct presentation reuses the document revision cache");
        var Revision = Editor.Describe()["documentRevision"]!.GetValue<ulong>();
        var Toggle = Markup().Descendants().Single(Element =>
            Element.Attribute("id")?.Value == GroupId + "-toggle").Attribute("data-action")!.Value;
        Editor.TryHandleAction(Toggle);
        Check(!Editor.BuildPaneMarkup("details").Contains(GroupId + "-power", StringComparison.Ordinal) &&
            Editor.Describe()["documentRevision"]!.GetValue<ulong>() == Revision &&
            !Editor.IsDirty && !Editor.CanExecuteCommand("undo"),
            "Struct folding is view state and does not change document revision, savepoint or history");
        Editor.TryHandleAction(Toggle);
        var ChildId = StructId("configuration", "child");
        var ChildToggle = Markup().Descendants().Single(Element =>
            Element.Attribute("id")?.Value == ChildId + "-toggle").Attribute("data-action")!.Value;
        Editor.TryHandleAction(ChildToggle);
        Check(!Editor.BuildPaneMarkup("details").Contains(ChildId + "-title", StringComparison.Ordinal),
            "Folding a nested struct invalidates its cached ancestor presentation");
        Editor.TryHandleAction(Toggle);
        _ = Editor.BuildPaneMarkup("details");
        Editor.TryHandleAction(Toggle);
        Check(!Editor.BuildPaneMarkup("details").Contains(ChildId + "-title", StringComparison.Ordinal),
            "Reopening a cached parent preserves its child's independent fold state");
        Editor.TryHandleAction(ChildToggle);
        var NestedSamplesId = "dataAsset-collection-" + Uri.EscapeDataString(
            new JsonArray("configuration", "samples").ToJsonString());
        var SamplesToggle = Markup().Descendants().Single(Element =>
            Element.Attribute("id")?.Value == NestedSamplesId + "-toggle").Attribute("data-action")!.Value;
        Editor.TryHandleAction(SamplesToggle);
        Check(!Markup().Descendants().Any(Element => Element.Attribute("id")?.Value == NestedSamplesId + "-items"),
            "Collection folding invalidates a containing struct without a document edit");
        Editor.TryHandleAction(SamplesToggle);
        var NestedMapId = "dataAsset-map-" + Uri.EscapeDataString(
            MapEditing.ViewKey(Asset.Id, new[] { "configuration", "labels" }));
        XElement NestedMap()
        {
            return Markup().Descendants().Single(Element => Element.Attribute("id")?.Value == NestedMapId);
        }
        var Move = NestedMap().Descendants("button").First(Element =>
            Element.Attribute("id")?.Value.EndsWith("-down", StringComparison.Ordinal) == true)
            .Attribute("data-action")!.Value;
        Editor.TryHandleAction(Move);
        Check(NestedMap().Descendants("input").Where(Element =>
                Element.Attribute("id")?.Value.EndsWith("-key", StringComparison.Ordinal) == true)
            .Select(Element => Element.Attribute("value")!.Value).SequenceEqual(new[] { "b", "a" }),
            "Map view sorting inside a cached struct refreshes the presentation immediately");
        var MapToggle = NestedMap().Descendants().Single(Element =>
            Element.Attribute("id")?.Value == NestedMapId + "-toggle").Attribute("data-action")!.Value;
        Editor.TryHandleAction(MapToggle);
        Check(!NestedMap().Descendants("input").Any() && !Editor.IsDirty && !Editor.CanExecuteCommand("undo"),
            "Nested Map folding and view sorting invalidate ancestors while preserving clean state and history");
        Editor.TryHandleAction(MapToggle);
        Edit("power", "7", "configuration");
        Check(State()["configuration"]!["power"]!.GetValue<int>() == 7 &&
            State()["configuration"]!["child"]!["title"]!.GetValue<string>() == "Nested" &&
            File.ReadAllText(FilePath) == Original &&
            Input("computed", new[] { "configuration" }).Attribute("value")!.Value == "14",
            "A struct member edit preserves siblings and does not save implicitly");
        var ReadOnlyRevision = Editor.Describe()["documentRevision"]!.GetValue<ulong>();
        Editor.TryHandleAction("editor.data-asset-structure:" + Uri.EscapeDataString(new JsonObject
        {
            ["path"] = new JsonArray("configuration", "computed"),
            ["expected"] = MapEditing.Fingerprint(State()["configuration"]),
            ["operation"] = "value",
        }.ToJsonString()) + ":99");
        Check(Editor.Describe()["documentRevision"]!.GetValue<ulong>() == ReadOnlyRevision &&
            Input("computed", new[] { "configuration" }).Attribute("value")!.Value == "14",
            "Semantic struct commands cannot bypass a member's read-only accessors");
        Editor.ExecuteCommand("undo");
        Check(!Editor.IsDirty && State()["configuration"]!["power"]!.GetValue<int>() == 5,
            "One Undo restores a struct member and the savepoint");
        Editor.ExecuteCommand("redo");
        Edit("power", "-1", "configuration");
        Check(State()["configuration"]!["power"]!.GetValue<int>() == 7,
            "Nested DataProperty constraints reject invalid input atomically");
        Edit("title", "Updated", "configuration", "child");
        Check(State()["configuration"]!["child"]!["title"]!.GetValue<string>() == "Updated",
            "Nested custom structs expand recursively and commit through the document transaction");
        var OffsetId = GroupId + "-offset-0";
        var OffsetAction = Markup().Descendants().Single(Element =>
            Element.Attribute("id")?.Value == OffsetId).Attribute("data-value-commit")!.Value;
        Editor.TryHandleAction(OffsetAction + "17");
        Check(State()["configuration"]!["offset"]!.AsArray().Select(Node => Node!.Deserialize<float>())
            .SequenceEqual(new float[] { 17, 2, 3 }),
            "Struct Vector members reuse per-axis editing without overwriting other axes");
        Edit("power", "8", "entries", "0");
        Edit("power", "9", "lookup", "first/key");
        Check(State()["entries"]![0]!["power"]!.GetValue<int>() == 8 &&
            State()["lookup"]!["first/key"]!["power"]!.GetValue<int>() == 9,
            "List and Map struct values edit the intended member including keys containing slashes");
        var MapId = "dataAsset-map-" + Uri.EscapeDataString(MapEditing.ViewKey(Asset.Id, new[] { "lookup" }));
        var MapCommand = Markup().Descendants().Single(Element =>
            Element.Attribute("id")?.Value == MapId + "-add").Attribute("data-action")!.Value;
        var MapPayload = JsonNode.Parse(Uri.UnescapeDataString(MapCommand["editor.data-asset-map:".Length..].Split(':', 2)[0]))!.AsObject();
        MapPayload["operation"] = "value";
        MapPayload["key"] = "first/key";
        MapPayload["valuePath"] = new JsonArray("computed");
        var MapRevision = Editor.Describe()["documentRevision"]!.GetValue<ulong>();
        Editor.TryHandleAction("editor.data-asset-map:" + Uri.EscapeDataString(MapPayload.ToJsonString()) + ":99");
        Check(Editor.Describe()["documentRevision"]!.GetValue<ulong>() == MapRevision,
            "Map value commands cannot bypass nested struct read-only members");
        var Stale = Commit("power", new[] { "entries", "0" });
        var ListId = "dataAsset-collection-" + Uri.EscapeDataString(new JsonArray("entries").ToJsonString());
        Editor.TryHandleAction(Markup().Descendants().Single(Element =>
            Element.Attribute("id")?.Value == ListId + "-item-0-down").Attribute("data-action")!.Value);
        Editor.TryHandleAction(Stale + "99");
        Check(State()["entries"]![0]!["power"]!.GetValue<int>() == 6,
            "A pre-reorder struct field action cannot write to the new list occupant");
        var SamplesId = "dataAsset-collection-" + Uri.EscapeDataString(
            new JsonArray("configuration", "samples").ToJsonString());
        Editor.TryHandleAction(Markup().Descendants().Single(Element =>
            Element.Attribute("id")?.Value == SamplesId + "-add").Attribute("data-action")!.Value);
        Check(State()["configuration"]!["samples"]!.AsArray().Count == 2,
            "Collections inside structs reuse the shared collection editor and typed defaults");
        var ReferenceInput = Input("mesh", new[] { "configuration" });
        Editor.TryHandleAction(ReferenceInput.Attribute("data-change-action")!.Value + "project://missing");
        Check(State()["configuration"]!["mesh"] is null,
            "Asset references inside structs are validated recursively");
        Editor.Save();
        using var Reopened = new DataAssetEditorInstance(Context);
        var Typed = DataAssetSerializer.Parse<StructFixture>(File.ReadAllText(FilePath), Asset.Id);
        Check(Typed.Configuration.Power == 7 && Typed.Configuration.Child.Title == "Updated" &&
            Typed.Lookup["first/key"].Power == 9 && !Reopened.IsDirty,
            "Save and typed reopen retain struct edits across list and Map containers");
        var ReadOnlyAsset = new AssetDescriptor
        {
            Id = Asset.Id,
            Type = Asset.Type,
            Name = Asset.Name,
            ReadOnly = true,
        };
        using var ReadOnly = new DataAssetEditorInstance(new EditorAssetOpenContext(Runtime,
            ReadOnlyAsset, FilePath, "data-asset-editor", "readonly-structs"));
        var ReadOnlyMarkup = XElement.Parse(ReadOnly.BuildPaneMarkup("details"));
        Check(ReadOnlyMarkup.Descendants().Where(Element =>
                Element.Name.LocalName is "input" or "select" or "textarea")
            .All(Element => Element.Attribute("disabled") is not null) &&
            ReadOnlyMarkup.Descendants().Where(Element =>
                Element.Attribute("data-value-view")?.Value == "true")
            .All(Element => Element.Attribute("disabled") is null),
            "Read-only structs preserve nested layouts, disable values and retain expansion");
        var Schema = Runtime.RegisteredDataAssetTypes.Descriptors.Single(Descriptor =>
            Descriptor.TypeId == Asset.Type).Schema();
        Check(Schema["properties"]!["configuration"]!["properties"]!["power"]!["minimum"]!.GetValue<double>() == 0 &&
            Schema["properties"]!["entries"]!["items"]!["properties"]!["child"]!["properties"]!["title"] is not null,
            "DataAsset JSON Schema publishes nested struct members and constraints");
        Check(Schema["properties"]!["optional"]!["anyOf"]![0]!["properties"]!["computed"]!["readOnly"]!.GetValue<bool>() &&
            Schema["properties"]!["optional"]!["anyOf"]![1]!["type"]!.GetValue<string>() == "null",
            "Nullable struct schemas preserve null and read-only member metadata");
    }

    private static void ValidateInspector(EditorRuntime Runtime, Action<bool, string> Check)
    {
        var Power = new ComponentField("Power", typeof(int),
            Instance => ((InspectorStructValue)Instance).Power,
            (Instance, Value) => typeof(InspectorStructValue).GetField("Power")!.SetValue(Instance, Value));
        Runtime.Components!.RegisterData(new DataDescriptor(typeof(InspectorStructValue),
            () => new InspectorStructValue(), new[] { Power },
            Registry => new JsonObject
            {
                ["Power"] = 5,
            }));
        var Field = new ComponentField("Configuration", typeof(InspectorStructValue),
            Instance => ((InspectorStructComponent)Instance).Configuration,
            (Instance, Value) => ((InspectorStructComponent)Instance).Configuration = (InspectorStructValue)Value!);
        Runtime.Components.Register(new ComponentDescriptor("validation.struct-inspector", 1,
            typeof(InspectorStructComponent), "Validation/Struct", false, Array.Empty<Type>(),
            new[] { Field }, () => new InspectorStructComponent(), Registry => new JsonObject
            {
                ["Configuration"] = new JsonObject
                {
                    ["Power"] = 5,
                },
            }), "validation", "structs");
        var Entity = Runtime.World!.Entities[0];
        var Add = Runtime.EditComponent(new JsonObject
        {
            ["operation"] = "add",
            ["context"] = "authoring",
            ["expectedRevision"] = JsonNode.Parse(Runtime.DescribeComponentsJson())!["revision"]!.DeepClone(),
            ["entityId"] = Entity.Id,
            ["typeId"] = "validation.struct-inspector",
        });
        Check(Add["success"]!.GetValue<bool>(), "Custom struct components use the ordinary authoring path");
        ComponentRecord Record()
        {
            return Runtime.World!.Data.Entities.Single(Item => Item.EntityId == Entity.Id).Components
                .Single(Item => Item.TypeId == "validation.struct-inspector");
        }
        XElement Markup()
        {
            var Text = new System.Text.StringBuilder();
            typeof(EditorRuntime).GetMethod("AppendComponentField",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(Runtime, new object?[]
                {
                    Text, Record(), Field, "Configuration", Record().State["Configuration"], typeof(InspectorStructValue),
                });
            return XElement.Parse(Text.ToString());
        }
        Check(Markup().Attribute("class")!.Value.StartsWith("se-struct", StringComparison.Ordinal),
            "Inspector custom structs compose the same public struct group");
        Runtime.InvokeUiAction(Markup().Descendants("input").Single().Attribute("data-value-commit")!.Value + "17");
        Check(Record().State["Configuration"]!["Power"]!.GetValue<int>() == 17,
            "Inspector struct members keep the component revision checked transaction");
        var Revision = JsonNode.Parse(Runtime.DescribeComponentsJson())!["revision"]!.GetValue<ulong>();
        Runtime.InvokeUiAction(Markup().Descendants("button").Single().Attribute("data-action")!.Value);
        Check(!Markup().Descendants("input").Any() &&
            JsonNode.Parse(Runtime.DescribeComponentsJson())!["revision"]!.GetValue<ulong>() == Revision,
            "Inspector struct folding omits fields without changing component data revision");
    }
}

public struct InspectorStructValue
{
    public int Power;
}

public sealed class InspectorStructComponent : SharpComponent
{
    public InspectorStructValue Configuration;
}

[DataAssetType("Validation.Structs")]
public sealed class StructFixture : DataAsset
{
    public StructConfiguration? Optional
    {
        get;
        set;
    } = StructConfiguration.Create(3);

    public StructConfiguration? Empty
    {
        get;
        set;
    }

    public StructConfiguration Configuration
    {
        get;
        set;
    } = StructConfiguration.Create(5);

    public List<StructConfiguration> Entries
    {
        get;
        set;
    } = new()
    {
        StructConfiguration.Create(5), StructConfiguration.Create(6),
    };

    public Dictionary<string, StructConfiguration> Lookup
    {
        get;
        set;
    } = new()
    {
        ["first/key"] = StructConfiguration.Create(5),
    };
}

public struct StructConfiguration
{
    [DataProperty(Minimum = 0, Maximum = 100)]
    public int Power
    {
        get;
        set;
    }

    public StructChild Child
    {
        get;
        set;
    }

    public Vector3 Offset
    {
        get;
        set;
    }

    public List<int> Samples
    {
        get;
        set;
    }

    public Dictionary<string, StructChild> Labels
    {
        get;
        set;
    }

    public AssetRef<StaticMeshAsset> Mesh
    {
        get;
        set;
    }

    [JsonInclude]
    public int IncludedField;

    [JsonIgnore]
    public int IgnoredValue
    {
        get;
        set;
    }

    public int Computed
    {
        get
        {
            return Power * 2;
        }
    }

    public static StructConfiguration Create(int Power)
    {
        return new StructConfiguration
        {
            Power = Power,
            Child = new StructChild
            {
                Title = "Nested",
            },
            Offset = new Vector3(1, 2, 3),
            Samples = new List<int>
            {
                2,
            },
            Labels = new Dictionary<string, StructChild>
            {
                ["a"] = new StructChild
                {
                    Title = "First",
                },
                ["b"] = new StructChild
                {
                    Title = "Second",
                },
            },
            IncludedField = 9,
        };
    }
}

public struct StructChild
{
    [JsonPropertyName("title")]
    [DataProperty(Required = true)]
    public string Title
    {
        get;
        set;
    }
}
