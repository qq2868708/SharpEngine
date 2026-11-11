using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.UI;

namespace SharpEngine.Editor;

internal sealed partial class DataAssetEditorInstance
{
    private readonly Dictionary<string, MapEditing.ViewState> MapViews = new(StringComparer.Ordinal);

    private string MapAction(MapEditing.ViewState View, string[] MapPath,
        string[] ValuePath, string Operation, string Key)
    {
        var Payload = new JsonObject
        {
            ["path"] = new JsonArray(MapPath.Select(Segment =>
                (JsonNode?)JsonValue.Create(Segment)).ToArray()),
            ["valuePath"] = new JsonArray(ValuePath.Select(Segment =>
                (JsonNode?)JsonValue.Create(Segment)).ToArray()),
            ["key"] = Key,
            ["operation"] = Operation,
            ["expected"] = View.Fingerprint,
            ["containerExpected"] = View.ContainerFingerprint,
        };
        return "editor.data-asset-map:" + Uri.EscapeDataString(Payload.ToJsonString()) + ":";
    }

    private bool TryHandleMapAction(string Action)
    {
        const string Prefix = "editor.data-asset-map:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            var Parts = Action[Prefix.Length..].Split(':', 2);
            var Payload = JsonNode.Parse(Uri.UnescapeDataString(Parts[0]))!.AsObject();
            var Path = Payload["path"]!.AsArray()
                .Select(Node => Node!.GetValue<string>()).ToArray();
            var ValuePath = Payload["valuePath"]!.AsArray()
                .Select(Node => Node!.GetValue<string>()).ToArray();
            var Key = Payload["key"]!.GetValue<string>();
            var Operation = Payload["operation"]!.GetValue<string>();
            var Expected = Payload["expected"]!.GetValue<string>();
            var ViewKey = MapEditing.ViewKey(Asset.Id, Path);
            if (!MapViews.TryGetValue(ViewKey, out var View))
            {
                throw new InvalidOperationException(
                    "MAP_VIEW_MISSING: reopen the editor to refresh Map order");
            }
            var Snapshot = Document.Snapshot;
            if (Path.Length == 0 || MapEditing.Fingerprint(Snapshot[Path[0]]) !=
                Payload["containerExpected"]!.GetValue<string>())
            {
                throw new InvalidDataException("REVISION_CONFLICT: Map container changed while editing");
            }
            var CurrentMapNode = MapAt(Snapshot, Path);
            if (CurrentMapNode is not null && CurrentMapNode is not JsonObject)
            {
                throw new InvalidDataException("MAP_TYPE: an object or null is required");
            }
            if (!string.Equals(MapEditing.Fingerprint(CurrentMapNode), Expected,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "REVISION_CONFLICT: Map data changed while editing");
            }

            if (Operation == "fold")
            {
                View.Order = MapEditing.ReconcileOrder(CurrentMapNode as JsonObject, View.Order);
                View.Expanded = !View.Expanded;
                InvalidateStructureMarkup(Path);
                return true;
            }
            if (Operation is "view-up" or "view-down")
            {
                MoveMapViewEntry(View, Key, Operation);
                InvalidateStructureMarkup(Path);
                return true;
            }

            var PreviousKeys = (CurrentMapNode as JsonObject)?.Select(Property => Property.Key)
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            var Changed = Document.Edit("data-asset-map", Root =>
            {
                EnsureDataPathEditable(Path);
                var MapNode = MapAt(Root, Path);
                if (!string.Equals(MapEditing.Fingerprint(MapNode), Expected,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "REVISION_CONFLICT: Map data changed while editing");
                }
                var Map = MapNode as JsonObject;
                if (MapNode is not null && Map is null)
                {
                    throw new InvalidDataException("MAP_TYPE: an object or null is required");
                }
                if (Map is null)
                {
                    if (Operation != "add")
                    {
                        throw new InvalidDataException(
                            "MAP_TYPE: null Map can only be initialized by Add");
                    }
                    JsonNode? RootNode = Root;
                    MapEditing.SetAt(ref RootNode, Path, new JsonObject());
                    Map = MapAt(Root, Path)!.AsObject();
                }

                var MapType = MapContainerType(Path);
                var EntryType = MapEditing.ValueType(MapType) ??
                    throw new InvalidDataException("MAP_TYPE: unsupported map type");
                var Text = Parts.Length > 1 ? Parts[1] : string.Empty;
                if (Operation == "add")
                {
                    var NewKey = MapEditing.UniqueKey(Map, Key);
                    Map[NewKey] = DefaultMapValue(EntryType);
                    return;
                }
                if (Operation == "remove")
                {
                    if (!Map.Remove(Key))
                    {
                        throw new InvalidDataException("MAP_KEY: entry no longer exists");
                    }
                    return;
                }
                if (Operation == "rename")
                {
                    MapEditing.Rename(Map, Key, Text);
                    return;
                }
                if (Operation == "value" || Operation == "color" ||
                    Operation.StartsWith("axis,", StringComparison.Ordinal))
                {
                    EnsureDataPathEditable(Path.Append(Key).Concat(ValuePath).ToArray());
                    MapEditing.SetValue(Map, new[] { Key }.Concat(ValuePath).ToArray(),
                        Operation, Text, EntryType, GetDataPropertyType);
                    return;
                }
                throw new InvalidDataException("MAP_OPERATION: unsupported operation");
            }, out var Message);
            if (!Changed)
            {
                if (Message.Length != 0)
                {
                    Context.SetStatus(Message);
                }
                return true;
            }

            var NewMap = MapAt(Document.Snapshot, Path) as JsonObject;
            if (NewMap is not null)
            {
                if (Operation == "remove")
                {
                    View.Order.Remove(Key);
                }
                else if (Operation == "rename" && Parts.Length > 1)
                {
                    var Index = View.Order.IndexOf(Key);
                    if (Index >= 0)
                    {
                        View.Order[Index] = Parts[1];
                    }
                }
                else if (Operation == "add")
                {
                    var AddedKey = NewMap.Select(Property => Property.Key)
                        .Except(PreviousKeys, StringComparer.Ordinal).SingleOrDefault();
                    if (AddedKey is not null)
                    {
                        View.Order.Add(AddedKey);
                    }
                }
                View.Order = MapEditing.ReconcileOrder(NewMap, View.Order);
                View.Fingerprint = MapEditing.Fingerprint(NewMap);
            }
            PropertiesMarkup = null;
        }
        catch (Exception Error) when (Error is ArgumentException or InvalidOperationException or
            InvalidDataException or JsonException or FormatException or OverflowException)
        {
            Context.SetStatus(Error.Message);
        }
        return true;
    }

    private static void MoveMapViewEntry(MapEditing.ViewState View, string Key,
        string Operation)
    {
        var Index = View.Order.IndexOf(Key);
        var Target = Index + (Operation == "view-up" ? -1 : 1);
        if (Index < 0 || Target < 0 || Target >= View.Order.Count)
        {
            throw new InvalidOperationException("MAP_VIEW_INDEX: entry no longer exists");
        }
        (View.Order[Index], View.Order[Target]) = (View.Order[Target], View.Order[Index]);
    }

    private JsonNode? MapAt(JsonObject Root, IEnumerable<string> Path)
    {
        return MapEditing.GetAt(Root, Path);
    }

    private Type MapContainerType(string[] Path)
    {
        if (Path.Length == 0)
        {
            throw new InvalidDataException("MAP_PATH: a map property path is required");
        }
        var Type = Descriptor.Properties.Single(Property => Property.JsonName == Path[0])
            .PropertyType;
        foreach (var Segment in Path.Skip(1))
        {
            var MapValueType = MapEditing.ValueType(Type);
            if (MapValueType is not null)
            {
                Type = MapValueType;
            }
            else
            {
                var ElementType = CollectionEditing.ElementType(Type);
                if (ElementType is not null)
                {
                    _ = int.Parse(Segment, CultureInfo.InvariantCulture);
                    Type = ElementType;
                }
                else
                {
                    Type = GetDataPropertyType(Type, Segment);
                }
            }
        }
        return Type;
    }

    private Type GetDataPropertyType(Type Type, string JsonName)
    {
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        var MapValueType = MapEditing.ValueType(Type);
        if (MapValueType is not null)
        {
            return MapValueType;
        }
        var ElementType = CollectionEditing.ElementType(Type);
        if (ElementType is not null)
        {
            _ = int.Parse(JsonName, CultureInfo.InvariantCulture);
            return ElementType;
        }
        var Members = DataAssetStructs.Members(Type);
        if (Members.Count != 0)
        {
            return Members.SingleOrDefault(Member => Member.Name == JsonName)?.PropertyType ??
                throw new InvalidDataException("STRUCT_PATH: member was not found: " + JsonName);
        }
        var Properties = Type.GetProperties(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public);
        var Property = Properties.FirstOrDefault(Property =>
                Property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), true)
                    .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
                    .Any(Attribute => Attribute.Name == JsonName)) ??
            Properties.FirstOrDefault(Property => string.Equals(
                char.ToLowerInvariant(Property.Name[0]) + Property.Name[1..],
                JsonName, StringComparison.Ordinal));
        return Property?.PropertyType ??
            throw new InvalidDataException("MAP_PATH: field was not found: " + JsonName);
    }

    private static JsonNode? DefaultMapValue(Type Type)
    {
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        if (Type == typeof(string))
        {
            return JsonValue.Create(string.Empty);
        }
        if (Type.IsEnum)
        {
            return JsonValue.Create(Enum.GetNames(Type)[0]);
        }
        if (Type == typeof(bool))
        {
            return JsonValue.Create(false);
        }
        if (Type == typeof(Color))
        {
            return new JsonObject
            {
                ["r"] = 0,
                ["g"] = 0,
                ["b"] = 0,
                ["a"] = 1,
            };
        }
        if (Type == typeof(System.Numerics.Vector2))
        {
            return new JsonArray(0, 0);
        }
        if (Type == typeof(System.Numerics.Vector3))
        {
            return new JsonArray(0, 0, 0);
        }
        if (Type == typeof(System.Numerics.Vector4) || Type == typeof(System.Numerics.Quaternion))
        {
            return new JsonArray(0, 0, 0, 0);
        }
        if (Type.IsGenericType && Type.GetGenericTypeDefinition() == typeof(AssetRef<>))
        {
            return null;
        }
        if (CollectionEditing.ElementType(Type) is not null || MapEditing.ValueType(Type) is not null)
        {
            return null;
        }
        if (Type.IsValueType)
        {
            return DataAssetSerializer.SerializeValue(Type, Activator.CreateInstance(Type));
        }
        return null;
    }

    private string BuildMap(string Label, Type Type, JsonNode? Value, string[] Path,
        JsonObject Root, bool ReadOnly = false)
    {
        ReadOnly |= IsReadOnly;
        var Map = Value as JsonObject;
        var EntryType = MapEditing.ValueType(Type) ??
            throw new InvalidDataException("MAP_TYPE: unsupported map type");
        var ViewKey = MapEditing.ViewKey(Asset.Id, Path);
        if (!MapViews.TryGetValue(ViewKey, out var View))
        {
            View = new MapEditing.ViewState();
            MapViews.Add(ViewKey, View);
        }
        var Fingerprint = MapEditing.Fingerprint(Value);
        View.ContainerFingerprint = MapEditing.Fingerprint(Root[Path[0]]);
        if (View.Fingerprint != Fingerprint)
        {
            View.Order = MapEditing.ReconcileOrder(Map, View.Order);
            View.Fingerprint = Fingerprint;
        }
        var Id = "dataAsset-map-" + Uri.EscapeDataString(ViewKey);
        var Entries = new List<UiMapEntry>();
        for (var Index = 0; Index < View.Order.Count; Index++)
        {
            var Key = View.Order[Index];
            var EntryId = Id + "-entry-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Key)))[..10];
            var KeyInput = "<div class=\"se-map-key\">" + MapEditing.KeyInput(
                EntryId + "-key", Key,
                ReadOnly ? string.Empty : MapAction(View, Path, Array.Empty<string>(), "rename", Key),
                ReadOnly) + "</div>";
            var HasStructure = TryBuildDataStructure("Value", EntryType, Map![Key],
                Path.Append(Key).ToArray(), Root, out var Structure, ReadOnly);
            var ValueMarkup = HasStructure ? Structure : MapEditing.ValueType(EntryType) is not null
                ? BuildMap("Value", EntryType, Map![Key], Path.Append(Key).ToArray(), Root, ReadOnly)
                : MapEditing.ValueActionMarkup("Value", EntryId + "-value", EntryType,
                    Map![Key], MapAction(View, Path, Array.Empty<string>(),
                        EntryType == typeof(Color) ? "color" : "value", Key),
                    Axis => MapAction(View, Path, Array.Empty<string>(), "axis," +
                        Axis.ToString(CultureInfo.InvariantCulture), Key),
                    ReadOnly, BuildMapReferenceOptions(EntryType));
            Entries.Add(new UiMapEntry(EntryId, KeyInput, ValueMarkup,
                ReadOnly ? string.Empty : MapAction(View, Path, Array.Empty<string>(), "remove", Key),
                Index == 0 ? string.Empty : MapAction(View, Path, Array.Empty<string>(), "view-up", Key),
                Index + 1 == View.Order.Count ? string.Empty :
                    MapAction(View, Path, Array.Empty<string>(), "view-down", Key)));
        }
        return UiMapEditor.Render(Id, Label, Entries,
            MapAction(View, Path, Array.Empty<string>(), "fold", string.Empty),
            ReadOnly ? string.Empty : MapAction(View, Path, Array.Empty<string>(),
                "add", MapEditing.UniqueKey(Map ?? new JsonObject())),
            View.Expanded, ReadOnly, Value is null);
    }

    private IEnumerable<(string Id, string Label)> BuildMapReferenceOptions(Type Type)
    {
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        if (!Type.IsGenericType || Type.GetGenericTypeDefinition() != typeof(AssetRef<>))
        {
            return Array.Empty<(string Id, string Label)>();
        }
        var ReferenceType = Type.GetGenericArguments()[0];
        return new[]
        {
            (string.Empty, "None"),
        }.Concat(Context.Runtime.Assets!.Assets.Where(Asset => Asset.Exists &&
            EditorAssetReferenceTypes.Matches(Asset.Type, ReferenceType))
            .Select(Asset => (Asset.Id, Asset.Name))).ToArray();
    }
}
