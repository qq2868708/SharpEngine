using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Settings;
using SharpEngine.UI;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private bool HandleSettingsMapAction(string Action)
    {
        const string Prefix = "editor.settings-map:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            var Parts = Action[Prefix.Length..].Split(':', 2);
            var Payload = JsonNode.Parse(Uri.UnescapeDataString(Parts[0]))!.AsObject();
            var Scope = Enum.Parse<SettingsScope>(Payload["scope"]!.GetValue<string>());
            var Id = Payload["page"]!.GetValue<string>();
            var FieldPath = Payload["field"]!.GetValue<string>();
            var Path = Payload["path"]!.AsArray()
                .Select(Node => Node!.GetValue<string>()).ToArray();
            var ValuePath = Payload["valuePath"]!.AsArray()
                .Select(Node => Node!.GetValue<string>()).ToArray();
            var Key = Payload["key"]!.GetValue<string>();
            var Operation = Payload["operation"]!.GetValue<string>();
            var Descriptor = _developmentSettings?.Find(Scope, Id) ??
                throw new InvalidDataException("Unknown settings page");
            var (FieldType, FieldValue) = ResolveSettingsField(Descriptor, FieldPath);
            var Root = JsonNode.Parse(_developmentSettings!.EditorValue(FieldType, FieldValue));
            var CurrentMapNode = MapEditing.GetAt(Root, Path);
            if (CurrentMapNode is not null && CurrentMapNode is not JsonObject)
            {
                throw new InvalidDataException("MAP_TYPE: an object or null is required");
            }
            var Expected = Payload["expected"]!.GetValue<string>();
            if (!string.Equals(MapEditing.Fingerprint(CurrentMapNode), Expected,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("REVISION_CONFLICT: Map changed while editing");
            }

            var ViewId = SettingsMapViewId(Scope, Id, FieldPath, Path);
            SettingsMapOrders.TryGetValue(ViewId, out var ExistingOrder);
            var Order = MapEditing.ReconcileOrder(CurrentMapNode as JsonObject, ExistingOrder);
            SettingsMapOrders[ViewId] = Order;
            if (Operation == "fold")
            {
                var Expanded = !SettingsMapExpanded.GetValueOrDefault(ViewId, true);
                SettingsMapExpanded[ViewId] = Expanded;
                RefreshSettingsWindow(Scope);
                MarkExtensionUiDirty();
                return true;
            }
            if (Operation is "view-up" or "view-down")
            {
                MoveSettingsMapEntry(Order, Key, Operation);
                RefreshSettingsWindow(Scope);
                MarkExtensionUiDirty();
                return true;
            }

            var PreviousKeys = (CurrentMapNode as JsonObject)?.Select(Property => Property.Key)
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            var Text = Parts.Length > 1 ? Parts[1] : string.Empty;
            var MapType = ResolveSettingsPathType(FieldType, Path);
            var EntryType = MapEditing.ValueType(MapType) ??
                throw new InvalidDataException("MAP_TYPE: unsupported map type");
            var Map = CurrentMapNode as JsonObject;
            if (Map is null)
            {
                if (Operation != "add")
                {
                    throw new InvalidDataException("MAP_TYPE: null Map can only be initialized by Add");
                }
                MapEditing.SetAt(ref Root, Path, new JsonObject());
                Map = MapEditing.GetAt(Root, Path)!.AsObject();
            }

            if (Operation == "add")
            {
                var NewKey = MapEditing.UniqueKey(Map, Key);
                Map[NewKey] = DefaultSettingsMapValue(EntryType);
            }
            else if (Operation == "remove")
            {
                if (!Map.Remove(Key))
                {
                    throw new InvalidDataException("MAP_KEY: entry no longer exists");
                }
            }
            else if (Operation == "rename")
            {
                MapEditing.Rename(Map, Key, Text);
            }
            else if (Operation == "value" || Operation == "color" ||
                Operation.StartsWith("axis,", StringComparison.Ordinal))
            {
                MapEditing.SetValue(Map, new[] { Key }.Concat(ValuePath).ToArray(),
                    Operation, Text, EntryType, GetSettingsMemberType);
            }
            else
            {
                throw new InvalidDataException("MAP_OPERATION: unsupported operation");
            }

            if (!_developmentSettings.TrySet(Scope, Id, FieldPath,
                    Root?.ToJsonString() ?? "null", out var Error))
            {
                throw new InvalidDataException(Error);
            }
            UpdateSettingsMapOrder(ViewId, Order, Map, Key, Operation, Text, PreviousKeys);
            if (SettingsValueEditing.Contains(Scope) || SettingsValuePointerCommit.Contains(Scope))
            {
                SettingsCollectionRefreshPending.Add(Scope);
            }
            else
            {
                RefreshSettingsWindow(Scope);
            }
            MarkExtensionUiDirty();
            SetStatus($"Settings map changed · {Id}/{FieldPath}");
        }
        catch (Exception Error) when (Error is ArgumentException or InvalidOperationException or
            InvalidDataException or JsonException or FormatException or OverflowException)
        {
            SetStatus(Error.Message);
        }
        return true;
    }

    private string BuildSettingsMap(DevelopmentSettingsDescriptor Descriptor,
        string FieldPath, string Label, Type Type, JsonNode? Value, JsonNode? Root,
        string[] Path)
    {
        if (Path.Length == 0)
        {
            SettingsCollectionFingerprint = CollectionEditing.Fingerprint(Root);
        }
        var Map = Value as JsonObject;
        var EntryType = MapEditing.ValueType(Type) ??
            throw new InvalidDataException("MAP_TYPE: unsupported map type");
        var ViewId = SettingsMapViewId(Descriptor.Attribute.Scope,
            Descriptor.Attribute.Id, FieldPath, Path);
        SettingsMapOrders.TryGetValue(ViewId, out var ExistingOrder);
        var Order = MapEditing.ReconcileOrder(Map, ExistingOrder);
        SettingsMapOrders[ViewId] = Order;
        var Expanded = SettingsMapExpanded.GetValueOrDefault(ViewId, true);
        var Fingerprint = MapEditing.Fingerprint(Value);
        var Id = "setting-map-" + Uri.EscapeDataString(ViewId);
        var Entries = new List<UiMapEntry>();
        for (var Index = 0; Index < Order.Count; Index++)
        {
            var Key = Order[Index];
            var EntryId = Id + "-entry-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Key)))[..10];
            var RenameAction = SettingsMapAction(Descriptor, FieldPath, Path,
                Array.Empty<string>(), Fingerprint, "rename", Key);
            var KeyMarkup = "<div class=\"se-map-key\">" +
                MapEditing.KeyInput(EntryId + "-key", Key, RenameAction, false) + "</div>";
            var ValueMarkup = BuildSettingsMapValue(Descriptor, FieldPath, EntryType,
                Map![Key], Root, Path, Key, Array.Empty<string>(), EntryId + "-value",
                Fingerprint);
            Entries.Add(new UiMapEntry(EntryId, KeyMarkup, ValueMarkup,
                SettingsMapAction(Descriptor, FieldPath, Path, Array.Empty<string>(),
                    Fingerprint, "remove", Key),
                Index == 0 ? string.Empty : SettingsMapAction(Descriptor, FieldPath,
                    Path, Array.Empty<string>(), Fingerprint, "view-up", Key),
                Index + 1 == Order.Count ? string.Empty : SettingsMapAction(Descriptor,
                    FieldPath, Path, Array.Empty<string>(), Fingerprint, "view-down", Key)));
        }
        return UiMapEditor.Render(Id, Label, Entries,
            SettingsMapAction(Descriptor, FieldPath, Path, Array.Empty<string>(),
                Fingerprint, "fold", string.Empty),
            SettingsMapAction(Descriptor, FieldPath, Path, Array.Empty<string>(),
                Fingerprint, "add", MapEditing.UniqueKey(Map ?? new JsonObject())),
            Expanded, IsNull: Value is null);
    }

    private string BuildSettingsMapValue(DevelopmentSettingsDescriptor Descriptor,
        string FieldPath, Type Type, JsonNode? Value, JsonNode? Root, string[] MapPath,
        string Key, string[] ValuePath, string Id, string Fingerprint, string Label = "Value")
    {
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        if (CollectionEditing.ElementType(Type) is not null)
        {
            return BuildSettingsCollection(Descriptor, FieldPath, Label, Type, Value,
                Root, MapPath.Append(Key).Concat(ValuePath).ToArray());
        }
        var MapValueType = MapEditing.ValueType(Type);
        if (MapValueType is not null)
        {
            var NestedPath = MapPath.Append(Key).Concat(ValuePath).ToArray();
            return BuildSettingsMap(Descriptor, FieldPath, Label, Type, Value,
                Root, NestedPath);
        }
        var Object = Value as JsonObject;
        var Fields = IsSettingsObject(Type) ? GetSettingFields(Type) : [];
        if (Object is not null && Fields.Count != 0)
        {
            return BuildSettingsStructure(Descriptor, FieldPath,
                MapPath.Append(Key).Concat(ValuePath).ToArray(), Label, () =>
            {
                var Markup = new StringBuilder();
                foreach (var Field in Fields)
                {
                    var NestedValuePath = ValuePath.Append(Field.Attribute.Id).ToArray();
                    Markup.Append(BuildSettingsMapValue(Descriptor, FieldPath,
                        Field.Property.PropertyType, Object[Field.Attribute.Id], Root,
                        MapPath, Key, NestedValuePath,
                        Id + "-" + Field.Attribute.Id, Fingerprint,
                        Field.Attribute.DisplayName ?? Field.Property.Name));
                }
                return Markup.ToString();
            });
        }

        var Editable = IsSettingsMapValueEditable(Type);
        var Operation = Type == typeof(Color) ? "color" : "value";
        var Action = SettingsMapAction(Descriptor, FieldPath, MapPath, ValuePath,
            Fingerprint, Editable ? Operation : string.Empty, Key);
        if (!Editable)
        {
            return UiValueEditors.Multiline(Label, new UiValueInput(Id,
                Value?.ToJsonString() ?? "null", string.Empty, true), 4,
                "This settings Map value is read-only because its type is not editable");
        }
        return MapEditing.ValueActionMarkup(Label, Id, Type, Value, Action,
            Axis => SettingsMapAction(Descriptor, FieldPath, MapPath, ValuePath,
                Fingerprint, "axis," + Axis.ToString(CultureInfo.InvariantCulture), Key),
            false);
    }

    private string SettingsMapAction(DevelopmentSettingsDescriptor Descriptor,
        string FieldPath, string[] Path, string[] ValuePath, string Fingerprint,
        string Operation, string Key)
    {
        var Payload = new JsonObject
        {
            ["scope"] = Descriptor.Attribute.Scope.ToString(),
            ["page"] = Descriptor.Attribute.Id,
            ["field"] = FieldPath,
            ["path"] = new JsonArray(Path.Select(Segment =>
                (JsonNode?)JsonValue.Create(Segment)).ToArray()),
            ["valuePath"] = new JsonArray(ValuePath.Select(Segment =>
                (JsonNode?)JsonValue.Create(Segment)).ToArray()),
            ["expected"] = Fingerprint,
            ["operation"] = Operation,
            ["key"] = Key,
        };
        return "editor.settings-map:" + Uri.EscapeDataString(Payload.ToJsonString()) + ":";
    }

    private static string SettingsMapViewId(SettingsScope Scope, string Id,
        string FieldPath, string[] Path)
    {
        return Scope + ":" + Id + ":" + FieldPath + ":" + JsonSerializer.Serialize(Path);
    }

    private (Type Type, object? Value) ResolveSettingsField(
        DevelopmentSettingsDescriptor Descriptor, string FieldPath)
    {
        object? Value = Descriptor.Current;
        var Type = Descriptor.Type;
        foreach (var Segment in FieldPath.Split('/'))
        {
            var Field = GetSettingFields(Type).SingleOrDefault(Field =>
                Field.Attribute.Id == Segment) ??
                throw new InvalidDataException("Unknown settings field '" + Segment + "'");
            Value = Field.Property.GetValue(Value);
            Type = Field.Property.PropertyType;
        }
        return (Type, Value);
    }

    private Type ResolveSettingsPathType(Type Type, IEnumerable<string> Path)
    {
        foreach (var Segment in Path)
        {
            Type = Nullable.GetUnderlyingType(Type) ?? Type;
            var MapValueType = MapEditing.ValueType(Type);
            if (MapValueType is not null)
            {
                Type = MapValueType;
                continue;
            }
            var ElementType = CollectionEditing.ElementType(Type);
            if (ElementType is not null)
            {
                _ = int.Parse(Segment, CultureInfo.InvariantCulture);
                Type = ElementType;
                continue;
            }
            Type = GetSettingsMemberType(Type, Segment);
        }
        return Type;
    }

    private Type GetSettingsMemberType(Type Type, string Name)
    {
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        return GetSettingFields(Type).SingleOrDefault(Field => Field.Attribute.Id == Name)
            ?.Property.PropertyType ??
            throw new InvalidDataException("MAP_PATH: settings field was not found: " + Name);
    }

    private JsonNode? DefaultSettingsMapValue(Type Type)
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
        if (CollectionEditing.ElementType(Type) is not null || MapEditing.ValueType(Type) is not null)
        {
            return null;
        }
        object? Value = null;
        if (Type.IsValueType)
        {
            Value = Activator.CreateInstance(Type);
        }
        else if (IsSettingsObject(Type))
        {
            Value = Activator.CreateInstance(Type);
        }
        var Text = _developmentSettings!.EditorValue(Type, Value);
        if (Type == typeof(string) || Type.IsEnum)
        {
            return JsonValue.Create(Text);
        }
        return JsonNode.Parse(Text);
    }

    private static bool IsSettingsMapValueEditable(Type Type)
    {
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        if (Type == typeof(string) || Type == typeof(bool) || Type.IsEnum ||
            Type == typeof(Guid) || Type == typeof(Uri) || Type == typeof(DateTime) ||
            Type == typeof(DateTimeOffset) || Type == typeof(TimeSpan) || Type == typeof(decimal) ||
            Type.IsPrimitive || Type == typeof(System.Numerics.Vector2) ||
            Type == typeof(System.Numerics.Vector3) || Type == typeof(System.Numerics.Vector4) ||
            Type == typeof(System.Numerics.Quaternion) || Type == typeof(Color))
        {
            return true;
        }
        if (Type.IsGenericType && Type.GetGenericTypeDefinition() == typeof(AssetRef<>))
        {
            return false;
        }
        var Fields = IsSettingsObject(Type) ? GetSettingFields(Type) : [];
        return Fields.Count != 0 && Fields.All(Field =>
            IsSettingsMapValueEditable(Field.Property.PropertyType));
    }

    private static void MoveSettingsMapEntry(List<string> Order, string Key,
        string Operation)
    {
        var Index = Order.IndexOf(Key);
        var Target = Index + (Operation == "view-up" ? -1 : 1);
        if (Index < 0 || Target < 0 || Target >= Order.Count)
        {
            throw new InvalidOperationException("MAP_VIEW_INDEX: entry no longer exists");
        }
        (Order[Index], Order[Target]) = (Order[Target], Order[Index]);
    }

    private void UpdateSettingsMapOrder(string ViewId, List<string> Order,
        JsonObject Map, string Key, string Operation, string Text,
        IReadOnlySet<string> PreviousKeys)
    {
        if (Operation == "remove")
        {
            Order.Remove(Key);
        }
        else if (Operation == "rename")
        {
            var Index = Order.IndexOf(Key);
            if (Index >= 0)
            {
                Order[Index] = Text;
            }
        }
        else if (Operation == "add")
        {
            var AddedKey = Map.Select(Property => Property.Key)
                .Except(PreviousKeys, StringComparer.Ordinal).SingleOrDefault();
            if (AddedKey is not null)
            {
                Order.Add(AddedKey);
            }
        }
        SettingsMapOrders[ViewId] = MapEditing.ReconcileOrder(Map, Order);
    }
}
