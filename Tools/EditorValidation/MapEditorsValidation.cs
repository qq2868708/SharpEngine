using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Editor;

internal static class MapEditorsValidation
{
    internal static void Run(EditorRuntime Runtime, string Folder,
        Action<bool, string> Check)
    {
        Runtime.RegisteredDataAssetTypes.Register<MapFixture>();
        var Asset = new AssetDescriptor
        {
            Id = "project://validation/maps",
            Type = "Validation.Maps",
            Name = "Maps",
            Source = AssetSource.Project,
        };
        var PathValue = Path.Combine(Folder, "Maps.sdataasset");
        var Fixture = new MapFixture();
        var Original = DataAssetSerializer.Serialize(Fixture, Asset.Id, Asset.Name);
        var SerializedRoot = JsonNode.Parse(Original)!.AsObject();
        var SerializedKeys = SerializedRoot["vectors"]!.AsObject()
            .Select(Property => Property.Key).ToArray();
        Check(SerializedKeys.SequenceEqual(new[] { "a", "z" }) &&
            SerializedRoot["vectors"]!["a"]!.AsArray()[1]!.GetValue<float>() == 2,
            "DataAsset Map serialization sorts keys and writes vector components");
        var RoundTrip = DataAssetSerializer.Parse<MapFixture>(Original, Asset.Id);
        Check(RoundTrip.Vectors["a"] == new Vector3(1, 2, 3),
            "DataAsset Map vector values survive a typed round trip");
        File.WriteAllText(PathValue, Original);
        var Context = new EditorAssetOpenContext(Runtime, Asset, PathValue,
            "data-asset-editor", "map-validation");
        using var Editor = new DataAssetEditorInstance(Context);
        XElement Markup()
        {
            return XElement.Parse(Editor.BuildPaneMarkup("details"));
        }
        JsonObject State()
        {
            return Editor.Describe()["document"]!.AsObject();
        }
        var VectorMap = FindMap(Markup(), "Vectors");
        Check(MapKeys(VectorMap).SequenceEqual(new[] { "a", "z" }),
            "A newly opened Map view starts in ordinal key order");
        var Revision = Editor.Describe()["documentRevision"]!.GetValue<ulong>();
        var ViewSource = File.ReadAllText(PathValue);
        Editor.TryHandleAction(RowButtonAction(VectorMap, "z", "up"));
        Check(MapKeys(FindMap(Markup(), "Vectors")).SequenceEqual(new[] { "z", "a" }) &&
            Editor.Describe()["documentRevision"]!.GetValue<ulong>() == Revision &&
            !Editor.IsDirty && !Editor.CanExecuteCommand("undo") &&
            File.ReadAllText(PathValue) == ViewSource,
            "DataAsset Map view reordering is transient and never enters document history");

        var AddAction = ButtonAction(FindMap(Markup(), "Vectors"), "add");
        Editor.TryHandleAction(AddAction);
        Check(Editor.IsDirty && State()["vectors"]!["New Key"]!.AsArray()
                .All(Value => Value!.Deserialize<float>() == 0),
            "DataAsset Map Add creates a typed vector default in one authoring edit");
        var AddedRevision = Editor.Describe()["documentRevision"]!.GetValue<ulong>();
        Editor.TryHandleAction(AddAction);
        Check(Editor.Describe()["documentRevision"]!.GetValue<ulong>() == AddedRevision,
            "A stale Map Add action cannot modify a changed dictionary");

        var ARow = FindRow(FindMap(Markup(), "Vectors"), "a");
        var Axis = ARow.Descendants("input").Single(Input =>
            Input.Attribute("id")?.Value.EndsWith("-0", StringComparison.Ordinal) == true);
        Editor.TryHandleAction(Axis.Attribute("data-value-commit")!.Value + "17");
        Check(State()["vectors"]!["a"]!.AsArray()[0]!.GetValue<float>() == 17 &&
            State()["vectors"]!["a"]!.AsArray()[1]!.GetValue<float>() == 2 &&
            State()["vectors"]!["a"]!.AsArray()[2]!.GetValue<float>() == 3,
            "Map vector axis editing preserves the other components");

        var Rename = FindRow(FindMap(Markup(), "Vectors"), "a")
            .Descendants("input").Single(Input =>
                Input.Attribute("id")?.Value.EndsWith("-key", StringComparison.Ordinal) == true)
            .Attribute("data-value-commit")!.Value;
        Editor.TryHandleAction(Rename + "front/back");
        Check(State()["vectors"]!["front/back"] is not null &&
            MapKeys(FindMap(Markup(), "Vectors")).Contains("front/back"),
            "Map keys remain arbitrary JSON object keys and support slash characters");

        var ColorMap = FindMap(Markup(), "Colors");
        var Picker = ColorMap.Descendants().Single(Element =>
            Element.Attribute("data-color-picker") is not null);
        Editor.TryHandleAction(Picker.Attribute("data-color-prefix")!.Value +
            "[0.2,0.4,0.6,0.8]");
        Check(State()["colors"]!["accent"] is JsonObject &&
            State()["colors"]!["accent"]!["a"]!.GetValue<float>() == 0.8f,
            "DataAsset Map colors commit one RGBA update without changing their serialized object shape");

        var NestedMap = FindMap(Markup(), "Value");
        Check(MapKeys(NestedMap).SequenceEqual(new[] { "inner" }),
            "Nested dictionary values render as a child Map editor");
        Editor.TryHandleAction(ButtonAction(NestedMap, "add"));
        Check(State()["nested"]!["outer"]!["New Key"]!.GetValue<float>() == 0,
            "Nested Map Add updates only the nested dictionary");
        var OptionalMap = FindMap(Markup(), "Optional");
        Check(OptionalMap.Descendants().Any(Element => Element.Value == "Null map"),
            "A null DataAsset Map is distinguished from an empty Map");
        Editor.TryHandleAction(ButtonAction(OptionalMap, "add"));
        Check(State()["optional"]!["New Key"]!.GetValue<int>() == 0,
            "Adding to a null Map initializes it with one typed entry");
        Editor.ExecuteCommand("undo");
        Check(State()["optional"] is null,
            "Undo restores null distinctly from an empty Map");

        Editor.Save();
        var SavedRoot = JsonNode.Parse(File.ReadAllText(PathValue))!.AsObject();
        Check(SavedRoot["vectors"]!.AsObject().Select(Property => Property.Key)
                .SequenceEqual(new[] { "New Key", "front/back", "z" }),
            "Saving normalizes Map storage by key without adopting editor view order");
        using var Reopened = new DataAssetEditorInstance(Context);
        Check(Reopened.Describe()["document"]!["vectors"]!["front/back"] is not null &&
            MapKeys(FindMap(XElement.Parse(Reopened.BuildPaneMarkup("details")), "Vectors"))
                .SequenceEqual(new[] { "New Key", "front/back", "z" }),
            "Save and reopen preserve authoritative Map content and deterministic initial view order");

        var ReadOnlyAsset = new AssetDescriptor
        {
            Id = Asset.Id,
            Type = Asset.Type,
            Name = Asset.Name,
            ReadOnly = true,
        };
        using var ReadOnly = new DataAssetEditorInstance(new EditorAssetOpenContext(Runtime,
            ReadOnlyAsset, PathValue, "data-asset-editor", "map-readonly-validation"));
        var ReadOnlyMarkup = XElement.Parse(ReadOnly.BuildPaneMarkup("details"));
        var ReadOnlyMap = FindMap(ReadOnlyMarkup, "Vectors");
        var ReadOnlyFirstRow = FindRow(ReadOnlyMap, "New Key");
        Check(ReadOnlyFirstRow.Descendants("input").All(Input =>
                Input.Attribute("disabled") is not null) &&
            ReadOnlyFirstRow.Descendants("button").Single(Button =>
                Button.Attribute("id")?.Value.EndsWith("-down", StringComparison.Ordinal) == true)
                .Attribute("data-action")!.Value.Length != 0,
            "Read-only Maps disable data entry but retain visual ordering controls");
        ReadOnly.TryHandleAction(RowButtonAction(ReadOnlyMap, "New Key", "down"));
        Check(MapKeys(FindMap(XElement.Parse(ReadOnly.BuildPaneMarkup("details")), "Vectors"))
                .SequenceEqual(new[] { "front/back", "New Key", "z" }) &&
            !ReadOnly.IsDirty,
            "Read-only Map visual ordering remains transient and usable");

        Check(Runtime.RegisteredDataAssetTypes.TryGet(Asset.Type, out var Descriptor) &&
            Descriptor!.Schema()["properties"]!["vectors"]!["type"]!.GetValue<string>() == "object" &&
            Descriptor.Schema()["properties"]!["vectors"]!["additionalProperties"]!["type"]!
                .GetValue<string>() == "array",
            "DataAsset Map schema publishes additionalProperties with typed vector values");
    }

    private static XElement FindMap(XElement Markup, string Label)
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
        return Map.Descendants("input").Where(Input =>
                Input.Attribute("id")?.Value.EndsWith("-key", StringComparison.Ordinal) == true)
            .Select(Input => Input.Attribute("value")?.Value ?? string.Empty).ToArray();
    }

    private static XElement FindRow(XElement Map, string Key)
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
        return FindRow(Map, Key).Descendants("button").Single(Button =>
            Button.Attribute("id")?.Value.EndsWith("-" + Operation,
                StringComparison.Ordinal) == true).Attribute("data-action")!.Value;
    }
}

[DataAssetType("Validation.Maps")]
public sealed class MapFixture : DataAsset
{
    public Dictionary<string, Vector3> Vectors { get; set; } = new()
    {
        ["z"] = new Vector3(9, 8, 7),
        ["a"] = new Vector3(1, 2, 3),
    };

    public Dictionary<string, Dictionary<string, float>> Nested { get; set; } = new()
    {
        ["outer"] = new Dictionary<string, float>
        {
            ["inner"] = 2,
        },
    };

    public Dictionary<string, Color> Colors { get; set; } = new()
    {
        ["accent"] = new Color(0.1f, 0.2f, 0.3f, 1),
    };

    public Dictionary<string, int>? Optional { get; set; }
}
