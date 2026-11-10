using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.UI;

namespace SharpEngine.Editor;

internal sealed partial class DataAssetEditorInstance
{
    private readonly HashSet<string> CollapsedCollections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> CollectionFingerprints = new(StringComparer.Ordinal);

    private static JsonArray PathJson(IEnumerable<string> Path)
    {
        return new JsonArray(Path.Select(Segment => (JsonNode?)JsonValue.Create(Segment)).ToArray());
    }

    private static JsonNode? AtPath(JsonNode Root, IEnumerable<string> Path)
    {
        JsonNode? Current = Root;
        foreach (var Segment in Path)
        {
            Current = Current is JsonArray Array ? Array[int.Parse(Segment, CultureInfo.InvariantCulture)] : Current?[Segment];
        }
        return Current;
    }

    private static void SetPath(JsonObject Root, string[] Path, JsonNode? Value)
    {
        var Parent = AtPath(Root, Path.SkipLast(1));
        var Array = Parent as JsonArray;
        if (Array is not null)
        {
            Array[int.Parse(Path[^1], CultureInfo.InvariantCulture)] = Value;
        }
        else
        {
            Parent!.AsObject()[Path[^1]] = Value;
        }
    }

    private string CollectionBinding(JsonObject Root, string[] CollectionPath, string[] Path,
        string Operation, string Kind = "json")
    {
        var Key = PathJson(CollectionPath).ToJsonString();
        if (!CollectionFingerprints.ContainsKey(Key))
        {
            CollectionFingerprints[Key] = CollectionEditing.Fingerprint(AtPath(Root, CollectionPath));
        }
        var Payload = new JsonObject
        {
            ["collection"] = PathJson(CollectionPath),
            ["path"] = PathJson(Path),
            ["expected"] = CollectionFingerprints[Key],
            ["operation"] = Operation,
            ["kind"] = Kind,
        };
        return "editor.data-asset-collection:" + Uri.EscapeDataString(Payload.ToJsonString()) + ":";
    }

    private bool TryHandleCollectionAction(string Action)
    {
        const string FoldPrefix = "editor.data-asset-collection-fold:";
        if (Action.StartsWith(FoldPrefix, StringComparison.Ordinal))
        {
            var Key = Action[FoldPrefix.Length..];
            if (!CollapsedCollections.Add(Key))
            {
                CollapsedCollections.Remove(Key);
            }
            PropertiesMarkup = null;
            return true;
        }
        const string Prefix = "editor.data-asset-collection:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var Changed = Document.Edit("data-asset-collection", Root =>
        {
            var Parts = Action[Prefix.Length..].Split(':', 2);
            var Payload = JsonNode.Parse(Uri.UnescapeDataString(Parts[0]))!.AsObject();
            var CollectionPath = Payload["collection"]!.AsArray().Select(Node => Node!.GetValue<string>()).ToArray();
            var Path = Payload["path"]!.AsArray().Select(Node => Node!.GetValue<string>()).ToArray();
            EnsureDataPathEditable(Path);
            var Current = AtPath(Root, CollectionPath);
            if (CollectionEditing.Fingerprint(Current) != Payload["expected"]!.GetValue<string>())
            {
                throw new InvalidDataException("REVISION_CONFLICT: collection changed while editing");
            }
            if (!Path.Take(CollectionPath.Length).SequenceEqual(CollectionPath))
            {
                throw new InvalidDataException("COLLECTION_PATH: target must belong to the collection");
            }
            var Operation = Payload["operation"]!.GetValue<string>();
            if (Operation is "value" or "color")
            {
                var Text = Parts.Length > 1 ? Parts[1] : string.Empty;
                var Kind = Payload["kind"]!.GetValue<string>();
                JsonNode? Value = Kind is "text" or "reference" ?
                    Kind == "reference" && Text.Length == 0 ? null : JsonValue.Create(Text) : JsonNode.Parse(Text);
                var Object = Operation == "color" ? AtPath(Root, Path) as JsonObject : null;
                if (Object is not null)
                {
                    var Channels = Value as JsonArray;
                    if (Channels is null || Channels.Count != 4 ||
                        Channels.Any(Channel => Channel is null || !float.IsFinite(Channel.GetValue<float>())))
                    {
                        throw new InvalidDataException("Color requires four finite RGBA components");
                    }
                    for (var Index = 0; Index < 4; Index++)
                    {
                        Object["rgba"[Index].ToString()] = Channels[Index]!.DeepClone();
                    }
                    return;
                }
                SetPath(Root, Path, Value);
            }
            else
            {
                var Type = MapContainerType(Path);
                var ElementType = CollectionEditing.ElementType(Type) ?? throw new InvalidDataException("COLLECTION_TYPE: unsupported type");
                var Value = AtPath(Root, Path);
                var Items = CollectionEditing.Apply(Value, Operation, () => DefaultCollectionElement(ElementType));
                if (Value is null)
                {
                    SetPath(Root, Path, Items);
                }
            }
        }, out var Message);
        if (!Changed && Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
        return true;
    }

    private static JsonNode? DefaultCollectionElement(Type Type)
    {
        if (Type == typeof(string))
        {
            return JsonValue.Create(string.Empty);
        }
        if (Type.IsEnum)
        {
            return JsonValue.Create(Enum.GetNames(Type)[0]);
        }
        if (IsAssetReference(Type) || !Type.IsValueType)
        {
            return null;
        }
        return SharpEngine.Assets.DataAssetSerializer.SerializeValue(Type, Activator.CreateInstance(Type));
    }

    private string BuildCollection(string Label, Type Type, JsonNode? Value, string[] Path,
        JsonObject Root, string[]? GuardPath = null, bool ReadOnly = false)
    {
        ReadOnly |= IsReadOnly;
        GuardPath ??= Path;
        var ElementType = CollectionEditing.ElementType(Type)!;
        var Items = Value as JsonArray ?? new JsonArray();
        var Key = Uri.EscapeDataString(PathJson(Path).ToJsonString());
        var Id = "dataAsset-collection-" + Key;
        var Expanded = !CollapsedCollections.Contains(Key);
        var Rows = new List<UiCollectionItem>();
        for (var Index = 0; Index < Items.Count; Index++)
        {
            var ItemPath = Path.Append(Index.ToString(CultureInfo.InvariantCulture)).ToArray();
            var ItemId = Id + "-item-" + Index.ToString(CultureInfo.InvariantCulture);
            var Content = Expanded ? BuildCollectionValue(ElementType, Items[Index], ItemPath, Root, GuardPath, ItemId + "-value", ReadOnly) : string.Empty;
            Rows.Add(new UiCollectionItem(ItemId, Content,
                CollectionBinding(Root, GuardPath, Path, "remove," + Index),
                CollectionBinding(Root, GuardPath, Path, "up," + Index),
                CollectionBinding(Root, GuardPath, Path, "down," + Index)));
        }
        return UiCollectionEditor.Render(Id, Label, Rows, "editor.data-asset-collection-fold:" + Key,
            CollectionBinding(Root, GuardPath, Path, "add"), Expanded, ReadOnly, Value is null);
    }

    private string BuildCollectionValue(Type Type, JsonNode? Value, string[] Path, JsonObject Root,
        string[] GuardPath, string Id, bool ReadOnly = false)
    {
        ReadOnly |= IsReadOnly;
        var HasStructure = TryBuildDataStructure("Value", Type, Value, Path, Root,
            out var Structure, ReadOnly);
        if (HasStructure)
        {
            return Structure;
        }
        if (MapEditing.ValueType(Type) is not null)
        {
            return BuildMap("Value", Type, Value, Path, Root, ReadOnly);
        }
        if (CollectionEditing.ElementType(Type) is not null)
        {
            return BuildCollection("Items", Type, Value, Path, Root, GuardPath, ReadOnly);
        }
        var Binding = CollectionBinding(Root, GuardPath, Path, Type == typeof(Color) ? "color" : "value",
            IsAssetReference(Type) ? "reference" : Type == typeof(string) || Type.IsEnum ? "text" : "json");
        if (Type == typeof(bool))
        {
            var Checked = Value?.GetValue<bool>() == true;
            return UiValueEditors.Boolean("Value", Id, Checked, Binding + (!Checked ? "true" : "false"), ReadOnly);
        }
        if (Type.IsEnum)
        {
            return UiValueEditors.Choice("Value", Id, Value?.GetValue<string>() ?? string.Empty,
                Enum.GetNames(Type).Select(Name => (Name, Name)), Binding, ReadOnly);
        }
        if (IsAssetReference(Type))
        {
            var ReferenceType = Type.GetGenericArguments()[0];
            var Options = new List<(string Id, string Label)>
            {
                (string.Empty, "None"),
            };
            Options.AddRange(Context.Runtime.Assets!.Assets.Where(Asset => Asset.Exists &&
                EditorAssetReferenceTypes.Matches(Asset.Type, ReferenceType)).Select(Asset => (Asset.Id, Asset.Name)));
            return UiValueEditors.Reference("Value", Id, Value?.GetValue<string>() ?? string.Empty, Options, Binding, ReadOnly);
        }
        var Axes = ReadAxes(Type, Value);
        if (Axes is not null)
        {
            var Inputs = Axes.Select((Axis, Index) => new UiValueInput(Id + "-" + Index,
                UiValueEditors.Format(Axis), CollectionBinding(Root, GuardPath,
                    Path.Append(Value is JsonArray ? Index.ToString(CultureInfo.InvariantCulture) :
                        (Type == typeof(Color) ? "rgba" : "xyzw")[Index].ToString()).ToArray(), "value"), ReadOnly, "number")).ToArray();
            return Type == typeof(Color) ? UiValueEditors.Color("Value", Inputs, Binding) : UiValueEditors.Vector("Value", Inputs);
        }
        if (VectorSize(Type) > 0)
        {
            return UiValueEditors.Multiline("Value", new UiValueInput(Id, Value?.ToJsonString() ?? "null",
                string.Empty, true), 2, "The current serialized value does not expose numeric components");
        }
        var Text = Type == typeof(string) ? Value?.GetValue<string>() ?? string.Empty : Value?.ToJsonString() ?? "null";
        var Input = new UiValueInput(Id, Text, Binding, ReadOnly, IsNumber(Type) ? (IsInteger(Type) ? "integer" : "number") : "text");
        if (Type == typeof(string))
        {
            return UiValueEditors.Text("Value", Input);
        }
        if (IsNumber(Type))
        {
            return UiValueEditors.Number("Value", Input);
        }
        return UiValueEditors.Multiline("Value", Input, 3, "Serialized JSON value");
    }
}
