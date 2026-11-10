using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Editor;

internal static class CollectionEditorsValidation
{
    internal static void RunSettings(EditorRuntime Runtime, Action<bool, string> Check)
    {
        string Id(string Field) { return "setting-collection-" + Uri.EscapeDataString("Project:validation.gameplay:" + Field + ":"); }
        string Action(string Control, string Attribute = "data-action")
        {
            var Markup = XElement.Parse("<div>" + Runtime.BuildSettingsMarkup(SharpEngine.Settings.SettingsScope.Project) + "</div>");
            return Markup.Descendants().Single(Element => Element.Attribute("id")?.Value == Control).Attribute(Attribute)!.Value;
        }
        var TagsId = Id("tags");
        var Add = Action(TagsId + "-add");
        Runtime.InvokeUiAction(Add);
        Check(SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>().Tags.SequenceEqual(new[]
        {
            "starter", string.Empty,
        }),
            "Settings list Add uses the shared collection UI and existing settings transaction");
        Runtime.InvokeUiAction(Add);
        Check(SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>().Tags.Count == 2,
            "Settings collection rejects stale structural commands");
        Runtime.InvokeUiAction(Action(TagsId + "-value-1", "data-value-commit") + "extra");
        Runtime.InvokeUiAction(Action(TagsId + "-item-1-up"));
        Check(SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>().Tags.SequenceEqual(new[]
        {
            "extra", "starter",
        }),
            "Settings element edits and ordering preserve siblings");
        Runtime.InvokeUiAction("editor.settings-undo:project:validation.gameplay");
        Check(SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>().Tags[0] == "starter",
            "One settings Undo restores the preceding collection order");
        Runtime.InvokeUiAction("editor.settings-redo:project:validation.gameplay");
        Runtime.InvokeUiAction(Action(TagsId + "-toggle"));
        Check(!Runtime.BuildSettingsMarkup(SharpEngine.Settings.SettingsScope.Project).Contains(TagsId + "-value-0", StringComparison.Ordinal),
            "Settings collection folding is independent of its data and Undo history");
        Runtime.InvokeUiAction(Action(TagsId + "-toggle"));
        Runtime.InvokeUiAction(Action(Id("profiles") + "-add"));
        var ProfileId = Id("profiles") + "-value-0-strength";
        Runtime.InvokeUiAction(Action(ProfileId, "data-value-commit") + "-5");
        Check(SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>().Profiles[0].Strength == 1,
            "Nested settings constraints reject invalid list element values atomically");
        Runtime.InvokeUiAction(Action(ProfileId, "data-value-commit") + "4");
        Check(SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>().Profiles[0].Strength == 4,
            "Settings collections compose annotated object fields with primitive editors");
        Runtime.InvokeUiAction(Action(TagsId + "-item-0-remove"));
        Runtime.InvokeUiAction(Action(TagsId + "-item-0-remove"));
        Check(SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>().Tags.Count == 0,
            "Removing the final settings item leaves a valid empty collection");
        ValidateSettingsMaps(Runtime, Check);
    }

    private static void ValidateSettingsMaps(EditorRuntime Runtime,
        Action<bool, string> Check)
    {
        XElement Markup()
        {
            return XElement.Parse("<div>" +
                Runtime.BuildSettingsMarkup(SharpEngine.Settings.SettingsScope.Project) +
                "</div>");
        }
        var Map = FindSettingsMap(Markup(), "Weights");
        Check(MapKeys(Map).Length == 0 &&
            Map.Descendants().Any(Element => Element.Value == "Empty map"),
            "Settings Map rendering distinguishes an empty dictionary");
        Runtime.InvokeUiAction(ButtonAction(Map, "add"));
        Map = FindSettingsMap(Markup(), "Weights");
        Runtime.InvokeUiAction(ButtonAction(Map, "add"));
        Check(SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>()
                .Weights.Count == 2,
            "Settings Map Add uses TrySet and creates a typed default entry");
        Map = FindSettingsMap(Markup(), "Weights");
        var Revision = SettingsRevision(Runtime);
        var InitialOrder = MapKeys(Map);
        var ViewMove = RowButtonAction(Map, InitialOrder[1], "up");
        Runtime.InvokeUiAction(ViewMove);
        Map = FindSettingsMap(Markup(), "Weights");
        Check(MapKeys(Map).SequenceEqual(new[] { "New Key 2", "New Key" }) &&
            SettingsRevision(Runtime) == Revision &&
            SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>()
                .Weights.Keys.OrderBy(Key => Key, StringComparer.Ordinal)
                .SequenceEqual(new[] { "New Key", "New Key 2" }),
            "Settings Map view order does not alter settings values or document revision");
        Runtime.InvokeUiAction("editor.settings-undo:project:validation.gameplay");
        Check(SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>()
                .Weights.Keys.SequenceEqual(new[] { "New Key" }),
            "Settings Map view movement does not add an Undo entry");

        Map = FindSettingsMap(Markup(), "Vectors");
        var VectorRow = FindSettingsRow(Map, "origin");
        var Axis = VectorRow.Descendants("input").Single(Input =>
            Input.Attribute("id")?.Value.EndsWith("-0", StringComparison.Ordinal) == true);
        Runtime.InvokeUiAction(Axis.Attribute("data-value-commit")!.Value + "17");
        var Vector = SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>()
            .Vectors["origin"];
        Check(Vector == new System.Numerics.Vector3(17, 2, 3),
            "Settings Map vector editing changes one component and preserves the others");

        Map = FindSettingsMap(Markup(), "Colors");
        var Picker = Map.Descendants().Single(Element =>
            Element.Attribute("data-color-picker") is not null);
        Runtime.InvokeUiAction(Picker.Attribute("data-color-prefix")!.Value +
            "[0.8,0.7,0.6,0.5]");
        Check(SharpEngine.Settings.DevelopmentSettings.Get<ValidationProjectSettings>()
                .Colors["accent"] ==
            new SharpEngine.Components.Color(0.8f, 0.7f, 0.6f, 0.5f),
            "Settings Map color values reuse the shared color picker transaction");
    }

    private static XElement FindSettingsMap(XElement Markup, string Label)
    {
        return Markup.DescendantsAndSelf().Single(Element =>
            Element.Attribute("class")?.Value.Split(' ').Contains("se-map") == true &&
            Element.Elements().Any(Header =>
                Header.Attribute("class")?.Value == "se-map-header" &&
                Header.Elements("button").Any(Button =>
                    Button.Elements("span").FirstOrDefault()?.Value == Label)));
    }

    private static string[] MapKeys(XElement Map)
    {
        return Map.Descendants().Where(Element =>
                Element.Attribute("class")?.Value == "se-map-entry")
            .Select(Row => Row.Descendants("input").Single(Input =>
                Input.Attribute("id")?.Value.EndsWith("-key", StringComparison.Ordinal) == true)
                .Attribute("value")?.Value ?? string.Empty).ToArray();
    }

    private static XElement FindSettingsRow(XElement Map, string Key)
    {
        var KeyInput = Map.Descendants("input").Single(Input =>
            Input.Attribute("id")?.Value.EndsWith("-key", StringComparison.Ordinal) == true &&
            Input.Attribute("value")?.Value == Key);
        return KeyInput.Ancestors().First(Element =>
            Element.Attribute("class")?.Value == "se-map-entry");
    }

    private static string ButtonAction(XElement Map, string Operation)
    {
        return Map.Descendants("button").Single(Button =>
            Button.Attribute("id")?.Value.EndsWith("-" + Operation,
                StringComparison.Ordinal) == true).Attribute("data-action")!.Value;
    }

    private static string RowButtonAction(XElement Map, string Key, string Operation)
    {
        return FindSettingsRow(Map, Key).Descendants("button").Single(Button =>
            Button.Attribute("id")?.Value.EndsWith("-" + Operation,
                StringComparison.Ordinal) == true).Attribute("data-action")!.Value;
    }

    private static long SettingsRevision(EditorRuntime Runtime)
    {
        var Page = JsonNode.Parse(Runtime.DescribeSettingsJson())!["pages"]!.AsArray()
            .Single(Node => Node!["id"]!.GetValue<string>() == "validation.gameplay")!;
        return Page["revision"]!.GetValue<long>();
    }

    internal static void Run(EditorRuntime Runtime, string Folder, Action<bool, string> Check)
    {
        Runtime.RegisteredDataAssetTypes.Register<CollectionFixture>();
        var Asset = new AssetDescriptor
        {
            Id = "project://validation/collections",
            Type = "Validation.Collections",
            Name = "Collections",
            Source = AssetSource.Project,
        };
        var PathValue = Path.Combine(Folder, "Collections.sdataasset");
        var Original = DataAssetSerializer.Serialize(new CollectionFixture(), Asset.Id, Asset.Name);
        File.WriteAllText(PathValue, Original);
        var Context = new EditorAssetOpenContext(Runtime, Asset, PathValue, "data-asset-editor", "collection-validation");
        using var Editor = new DataAssetEditorInstance(Context);
        JsonObject State() { return Editor.Describe()["document"]!.AsObject(); }
        XElement Markup() { return XElement.Parse(Editor.BuildPaneMarkup("details")); }
        string Id(string Field) { return "dataAsset-collection-" + Uri.EscapeDataString(new JsonArray(Field).ToJsonString()); }
        string Action(string Control, string Attribute = "data-action")
        {
            return Markup().DescendantsAndSelf().Single(Element => Element.Attribute("id")?.Value == Control)
                .Attribute(Attribute)!.Value;
        }
        var NumberId = Id("numbers");
        var Cached = Editor.BuildPaneMarkup("details");
        var AllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var Index = 0; Index < 1000; Index++)
        {
            _ = Editor.BuildPaneMarkup("details");
        }
        Check(GC.GetAllocatedBytesForCurrentThread() - AllocatedBefore < 4096 &&
            ReferenceEquals(Cached, Editor.BuildPaneMarkup("details")),
            "Unchanged collection presentation reuses its revision cache without traversing the document");
        var Add = Action(NumberId + "-add");
        Editor.TryHandleAction(Add);
        Check(State()["numbers"]!.AsArray().Select(Node => Node!.GetValue<int>()).SequenceEqual(new[]
        {
            2, 7, 0,
        }) &&
            Editor.IsDirty && File.ReadAllText(PathValue) == Original, "Add appends a typed default without saving");
        var Revision = Editor.Describe()["documentRevision"]!.GetValue<ulong>();
        Editor.TryHandleAction(Add);
        Check(Editor.Describe()["documentRevision"]!.GetValue<ulong>() == Revision,
            "A stale collection action cannot apply against a changed list");
        Editor.ExecuteCommand("undo");
        Check(!Editor.IsDirty && State()["numbers"]!.AsArray().Count == 2, "One Undo restores collection and savepoint");
        Editor.ExecuteCommand("redo");
        Check(State()["numbers"]!.AsArray().Count == 3, "Redo restores the collection addition");
        var StaleInput = Action(NumberId + "-item-0-value", "data-value-commit");
        Editor.TryHandleAction(Action(NumberId + "-item-0-down"));
        Check(State()["numbers"]![0]!.GetValue<int>() == 7, "Down preserves values and their authored order");
        Editor.TryHandleAction(StaleInput + "42");
        Check(State()["numbers"]![0]!.GetValue<int>() == 7, "A field rendered before reorder cannot write to the new index occupant");
        Editor.TryHandleAction(Action(NumberId + "-item-0-value", "data-value-commit") + "11");
        Check(State()["numbers"]![0]!.GetValue<int>() == 11 && State()["numbers"]![1]!.GetValue<int>() == 2,
            "A fresh primitive element commit preserves siblings");
        var BeforeInvalid = State().ToJsonString();
        Editor.TryHandleAction(Action(NumberId + "-item-0-value", "data-value-commit") + "not-a-number");
        Check(State().ToJsonString() == BeforeInvalid, "Invalid element input is rejected atomically");
        Editor.TryHandleAction(Action(NumberId + "-item-1-remove"));
        Check(State()["numbers"]!.AsArray().Count == 2 && State()["numbers"]![1]!.GetValue<int>() == 0,
            "Remove changes only the selected element");
        var BeforeFold = Editor.Describe()["documentRevision"]!.GetValue<ulong>();
        Editor.TryHandleAction(Action(NumberId + "-toggle"));
        Check(Editor.Describe()["documentRevision"]!.GetValue<ulong>() == BeforeFold &&
            !Editor.BuildPaneMarkup("details").Contains(NumberId + "-item-0-value", StringComparison.Ordinal),
            "Folding omits child editors without changing authoritative state");
        Editor.TryHandleAction(Action(NumberId + "-toggle"));
        var NullId = Id("optional");
        Editor.TryHandleAction(Action(NullId + "-add"));
        Check(State()["optional"]![0]!.GetValue<string>() == string.Empty, "Add initializes null collections with one typed default");
        Editor.ExecuteCommand("undo");
        Check(State()["optional"] is null, "Undo restores null distinctly from an empty array");
        var ColorId = Id("colors") + "-item-0-value-0-picker-apply";
        var Picker = Markup().Descendants().Single(Element => Element.Attribute("id")?.Value == ColorId).Ancestors()
            .First(Element => Element.Attribute("data-color-picker") is not null);
        Editor.TryHandleAction(Picker.Attribute("data-color-prefix")!.Value + "[0.1,0.3,0.5,0.7]");
        Check(State()["colors"]![0] is JsonObject && State()["colors"]![0]!["a"]!.GetValue<double>() == 0.7,
            "Color collection elements reuse the palette and preserve object storage");
        var NestedId = "dataAsset-collection-" + Uri.EscapeDataString(new JsonArray("nested", "0").ToJsonString());
        Editor.TryHandleAction(Action(NestedId + "-add"));
        Check(State()["nested"]![0]!.AsArray().Count == 2 && State()["nested"]![1]![0]!.GetValue<int>() == 9,
            "Nested collections reuse the same editor without changing adjacent lists");
        var RefId = Id("references") + "-item-0-value";
        Editor.TryHandleAction(Action(RefId, "data-change-action") + "project://missing/material");
        Check(State()["references"]![0] is null, "Missing references inside a collection are rejected");
        Editor.TryHandleAction(Action(RefId, "data-change-action") + "engine://meshes/cube");
        Check(State()["references"]![0] is null, "Wrong-type references inside a collection are rejected");
        Editor.Save();
        using var Reopened = new DataAssetEditorInstance(Context);
        Check(!Editor.IsDirty && Reopened.Describe()["document"]!.ToJsonString() == State().ToJsonString(),
            "Explicit Save and reopen preserve nested collections, colors and order");
        var ReadOnlyAsset = new AssetDescriptor
        {
            Id = Asset.Id,
            Type = Asset.Type,
            Name = Asset.Name,
            ReadOnly = true,
        };
        using var ReadOnly = new DataAssetEditorInstance(new EditorAssetOpenContext(Runtime,
            ReadOnlyAsset, PathValue, "data-asset-editor", "collection-readonly"));
        var ReadOnlyMarkup = XElement.Parse(ReadOnly.BuildPaneMarkup("details"));
        Check(ReadOnlyMarkup.Descendants().Where(Element => Element.Name.LocalName is "input" or "select" or "textarea")
            .All(Element => Element.Attribute("disabled") is not null), "Read-only collection values use disabled primitive controls");
        ReadOnly.TryHandleAction(Action(NumberId + "-add"));
        Check(!ReadOnly.IsDirty, "Read-only document rejects direct mutation actions");
        Check(Runtime.RegisteredDataAssetTypes.TryGet(Asset.Type, out var Descriptor) &&
            Descriptor!.Schema()["properties"]!["numbers"]!["type"]!.GetValue<string>() == "array",
            "DataAsset List schema agrees with its serialized array contract");
        ValidateComponent(Runtime, Check);
    }

    private static void ValidateComponent(EditorRuntime Runtime, Action<bool, string> Check)
    {
        var Field = new ComponentField("Values", typeof(List<int>),
            Instance => ((CollectionComponent)Instance).Values,
            (Instance, Value) => ((CollectionComponent)Instance).Values = (List<int>)Value!);
        Runtime.Components!.Register(new ComponentDescriptor("validation.collections", 1, typeof(CollectionComponent),
            "Validation/Collections", false, Array.Empty<Type>(), new[]
            {
                Field,
            }, () => new CollectionComponent(),
            Registry => new JsonObject
            {
                ["Values"] = new JsonArray(3, 8),
            }), "validation", "collections");
        var Entity = Runtime.World!.Entities[0];
        var Components = JsonNode.Parse(Runtime.DescribeComponentsJson())!;
        var Result = Runtime.EditComponent(new JsonObject
        {
            ["operation"] = "add",
            ["context"] = "authoring",
            ["expectedRevision"] = Components["revision"]!.DeepClone(),
            ["entityId"] = Entity.Id,
            ["typeId"] = "validation.collections",
        });
        Check(Result["success"]!.GetValue<bool>(), "A list component enters the ordinary authoring transaction path");
        ComponentRecord Record()
        {
            return Runtime.World!.Data.Entities.Single(Item => Item.EntityId == Entity.Id).Components
                .Single(Item => Item.TypeId == "validation.collections");
        }
        XElement Markup()
        {
            var Text = new StringBuilder();
            typeof(EditorRuntime).GetMethod("AppendComponentField", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Runtime, new object?[]
                {
                    Text, Record(), Field, "Values", Record().State["Values"], typeof(List<int>),
                });
            return XElement.Parse(Text.ToString());
        }
        string Command(string Suffix, string Attribute = "data-action")
        {
            return Markup().Descendants().Single(Element => Element.Attribute("id")?.Value.EndsWith(Suffix, StringComparison.Ordinal) == true)
                .Attribute(Attribute)!.Value;
        }
        var Stale = Command("-item-0-down");
        Runtime.InvokeUiAction(Stale);
        Check(Record().State["Values"]![0]!.GetValue<int>() == 8, "Inspector collection uses the shared down command");
        Runtime.InvokeUiAction(Stale);
        Check(Record().State["Values"]![0]!.GetValue<int>() == 8, "Inspector rejects repeated stale index commands");
        Runtime.InvokeUiAction(Command("Values%2F0", "data-value-commit") + "14");
        Check(Record().State["Values"]![0]!.GetValue<int>() == 14 && Record().State["Values"]![1]!.GetValue<int>() == 3,
            "Inspector collection elements retain the existing component revision-checked field transaction");
        Runtime.InvokeUiAction(Command("-item-0-up"));
        Check(Record().State["Values"]![0]!.GetValue<int>() == 14, "Disabled first-row Up exposes no mutating command");
    }
}

[DataAssetType("Validation.Collections")]
public sealed class CollectionFixture : DataAsset
{
    public List<int> Numbers
    {
        get;
        set;
    } = new()
    {
        2, 7,
    };
    public string[]? Optional
    {
        get;
        set;
    }
    public int[][] Nested
    {
        get;
        set;
    } = new[]
    {
        new[]
        {
            4,
        },
        new[]
        {
            9,
        },
    };
    public Color[] Colors
    {
        get;
        set;
    } = new[]
    {
        new Color(0.2f, 0.6f, 0.9f, 1),
    };
    public AssetRef<MaterialAsset>[] References
    {
        get;
        set;
    } = new AssetRef<MaterialAsset>[1];
}

public sealed class CollectionComponent : SharpComponent
{
    public List<int> Values = new()
    {
        3, 8,
    };
}
