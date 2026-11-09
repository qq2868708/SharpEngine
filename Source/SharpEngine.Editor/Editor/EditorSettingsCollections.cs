using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Settings;
using SharpEngine.UI;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private readonly HashSet<string> CollapsedSettingCollections = new(StringComparer.Ordinal);
    private string SettingsCollectionFingerprint = string.Empty;
    private readonly HashSet<SettingsScope> SettingsCollectionRefreshPending = new();
    private readonly HashSet<SettingsScope> SettingsValueEditing = new();
    private readonly HashSet<SettingsScope> SettingsValuePointerCommit = new();

    private bool HandleSettingsValueAction(string HostId, string Action)
    {
        var IsSettingsHost = TryScopeFromHost(HostId, out var Scope);
        if (!IsSettingsHost || !Action.StartsWith("ui.value-", StringComparison.Ordinal))
        {
            return false;
        }
        switch (Action)
        {
            case "ui.value-focus":
                SettingsValueEditing.Add(Scope);
                break;
            case "ui.value-end":
                SettingsValueEditing.Remove(Scope);
                SettingsCollectionRefreshPending.Add(Scope);
                break;
            case "ui.value-end-pointer":
                SettingsValueEditing.Remove(Scope);
                SettingsValuePointerCommit.Add(Scope);
                SettingsCollectionRefreshPending.Add(Scope);
                break;
            case "ui.value-release":
                SettingsValuePointerCommit.Remove(Scope);
                break;
            default:
                return false;
        }
        return true;
    }

    private void FlushSettingsCollectionRefreshes()
    {
        foreach (var Scope in SettingsCollectionRefreshPending.ToArray())
        {
            if (!SettingsValueEditing.Contains(Scope) && !SettingsValuePointerCommit.Contains(Scope))
            {
                SettingsCollectionRefreshPending.Remove(Scope);
                RefreshSettingsWindow(Scope);
            }
        }
    }

    private bool HandleSettingsCollectionAction(string Action)
    {
        const string Prefix = "editor.settings-collection:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            var Parts = Action[Prefix.Length..].Split(':', 2);
            var Payload = JsonNode.Parse(Uri.UnescapeDataString(Parts[0]))!;
            var Scope = Enum.Parse<SettingsScope>(Payload["scope"]!.GetValue<string>());
            var Id = Payload["page"]!.GetValue<string>();
            var FieldPath = Payload["field"]!.GetValue<string>();
            var Path = Payload["path"]!.AsArray().Select(Node => Node!.GetValue<string>()).ToArray();
            var Descriptor = _developmentSettings!.Find(Scope, Id) ?? throw new InvalidDataException("Unknown settings page");
            object? Current = Descriptor.Current;
            var Type = Descriptor.Type;
            foreach (var Segment in FieldPath.Split('/'))
            {
                var Field = GetSettingFields(Type).Single(Field => Field.Attribute.Id == Segment);
                Current = Field.Property.GetValue(Current);
                Type = Field.Property.PropertyType;
            }
            var Root = JsonNode.Parse(_developmentSettings.EditorValue(Type, Current));
            var Operation = Payload["operation"]!.GetValue<string>();
            if (Operation == "fold")
            {
                var Key = Payload["key"]!.GetValue<string>();
                if (!CollapsedSettingCollections.Add(Key))
                {
                    CollapsedSettingCollections.Remove(Key);
                }
                RefreshSettingsWindow(Scope);
                return true;
            }
            if (CollectionEditing.Fingerprint(Root) != Payload["expected"]!.GetValue<string>())
            {
                throw new InvalidDataException("REVISION_CONFLICT: collection changed while editing");
            }
            JsonNode? Cursor = Root;
            JsonNode? Parent = null;
            foreach (var Segment in Path)
            {
                Parent = Cursor;
                var ElementType = CollectionEditing.ElementType(Type);
                if (ElementType is not null)
                {
                    Type = ElementType;
                    Cursor = Cursor!.AsArray()[int.Parse(Segment, CultureInfo.InvariantCulture)];
                }
                else
                {
                    Type = GetSettingFields(Type).Single(Field => Field.Attribute.Id == Segment).Property.PropertyType;
                    Cursor = Cursor?[Segment];
                }
            }
            JsonNode? Result;
            if (Operation == "value")
            {
                var Text = Parts.Length > 1 ? Parts[1] : string.Empty;
                Result = Type == typeof(string) || Type.IsEnum ? JsonValue.Create(Text) : JsonNode.Parse(Text);
            }
            else
            {
                var ElementType = CollectionEditing.ElementType(Type) ?? throw new InvalidDataException("Unsupported settings collection");
                Result = CollectionEditing.Apply(Cursor, Operation, () =>
                {
                    object? Value = ElementType == typeof(string) ? string.Empty :
                        ElementType.IsEnum ? Enum.GetValues(ElementType).GetValue(0) :
                        CollectionEditing.ElementType(ElementType) is not null ? null : Activator.CreateInstance(ElementType);
                    var Text = _developmentSettings.EditorValue(ElementType, Value);
                    return ElementType == typeof(string) || ElementType.IsEnum ? JsonValue.Create(Text) : JsonNode.Parse(Text);
                });
                if (Cursor is not null)
                {
                    Result = Cursor;
                }
            }
            if (!ReferenceEquals(Result, Cursor))
            {
                var Array = Parent as JsonArray;
                if (Path.Length == 0)
                {
                    Root = Result;
                }
                else if (Array is not null)
                {
                    Array[int.Parse(Path[^1], CultureInfo.InvariantCulture)] = Result;
                }
                else
                {
                    Parent!.AsObject()[Path[^1]] = Result;
                }
            }
            var Changed = _developmentSettings.TrySet(Scope, Id, FieldPath, Root?.ToJsonString() ?? "null", out var Error);
            if (!Changed)
            {
                throw new InvalidDataException(Error);
            }
            if (SettingsValueEditing.Contains(Scope) || SettingsValuePointerCommit.Contains(Scope))
            {
                SettingsCollectionRefreshPending.Add(Scope);
            }
            else
            {
                RefreshSettingsWindow(Scope);
            }
            MarkExtensionUiDirty();
        }
        catch (Exception Error) when (Error is ArgumentException or InvalidOperationException or
            InvalidDataException or System.Text.Json.JsonException or FormatException or OverflowException)
        {
            SetStatus(Error.Message);
        }
        return true;
    }

    private string BuildSettingsCollection(DevelopmentSettingsDescriptor Descriptor, string FieldPath,
        string Label, Type Type, JsonNode? Value, JsonNode? Root, string[] Path)
    {
        if (Path.Length == 0)
        {
            SettingsCollectionFingerprint = CollectionEditing.Fingerprint(Root);
        }
        var ElementType = CollectionEditing.ElementType(Type)!;
        var Items = Value as JsonArray ?? new JsonArray();
        var Id = "setting-collection-" + Uri.EscapeDataString(Descriptor.Attribute.Scope + ":" +
            Descriptor.Attribute.Id + ":" + FieldPath + ":" + string.Join('/', Path));
        var Expanded = !CollapsedSettingCollections.Contains(Id);
        var Rows = new List<UiCollectionItem>();
        for (var Index = 0; Index < Items.Count; Index++)
        {
            var ItemPath = Path.Append(Index.ToString(CultureInfo.InvariantCulture)).ToArray();
            Rows.Add(new UiCollectionItem(Id + "-item-" + Index,
                Expanded ? BuildSettingsCollectionValue(Descriptor, FieldPath, ElementType, Items[Index], Root, ItemPath, Id + "-value-" + Index) : string.Empty,
                SettingsCollectionBinding(Descriptor, FieldPath, Root, Path, "remove," + Index, Id),
                SettingsCollectionBinding(Descriptor, FieldPath, Root, Path, "up," + Index, Id),
                SettingsCollectionBinding(Descriptor, FieldPath, Root, Path, "down," + Index, Id)));
        }
        return UiCollectionEditor.Render(Id, Label, Rows,
            SettingsCollectionBinding(Descriptor, FieldPath, Root, Path, "fold", Id),
            SettingsCollectionBinding(Descriptor, FieldPath, Root, Path, "add", Id), Expanded, IsNull: Value is null);
    }

    private string BuildSettingsCollectionValue(DevelopmentSettingsDescriptor Descriptor, string FieldPath,
        Type Type, JsonNode? Value, JsonNode? Root, string[] Path, string Id)
    {
        if (CollectionEditing.ElementType(Type) is not null)
        {
            return BuildSettingsCollection(Descriptor, FieldPath, "Items", Type, Value, Root, Path);
        }
        var Object = Value as JsonObject;
        if (IsSettingsObject(Type) && Object is not null)
        {
            var Markup = new StringBuilder();
            foreach (var Field in GetSettingFields(Type))
            {
                Markup.Append(UiValueEditors.Field(Field.Attribute.DisplayName ?? Field.Property.Name,
                    BuildSettingsCollectionValue(Descriptor, FieldPath, Field.Property.PropertyType,
                        Object[Field.Attribute.Id], Root, Path.Append(Field.Attribute.Id).ToArray(), Id + "-" + Field.Attribute.Id)));
            }
            return Markup.ToString();
        }
        var Binding = SettingsCollectionBinding(Descriptor, FieldPath, Root, Path, "value", Id);
        if (Type == typeof(bool))
        {
            var Checked = Value?.GetValue<bool>() == true;
            return UiValueEditors.Boolean("Value", Id, Checked, Binding + (!Checked ? "true" : "false"));
        }
        if (Type.IsEnum)
        {
            return UiValueEditors.Choice("Value", Id, Value?.GetValue<string>() ?? string.Empty,
                Enum.GetNames(Type).Select(Name => (Name, Name)), Binding);
        }
        var Text = Type == typeof(string) ? Value?.GetValue<string>() ?? string.Empty : Value?.ToJsonString() ?? "null";
        var Number = Type.IsPrimitive && Type != typeof(char);
        return UiValueEditors.Text("Value", new UiValueInput(Id, Text, Binding, Kind: Number ? "number" : "text"));
    }

    private string SettingsCollectionBinding(DevelopmentSettingsDescriptor Descriptor, string FieldPath,
        JsonNode? Root, string[] Path, string Operation, string Key)
    {
        return "editor.settings-collection:" + Uri.EscapeDataString(new JsonObject
        {
            ["scope"] = Descriptor.Attribute.Scope.ToString(),
            ["page"] = Descriptor.Attribute.Id,
            ["field"] = FieldPath,
            ["path"] = new JsonArray(Path.Select(Segment => (JsonNode?)JsonValue.Create(Segment)).ToArray()),
            ["expected"] = SettingsCollectionFingerprint,
            ["operation"] = Operation,
            ["key"] = Key,
        }.ToJsonString()) + ":";
    }
}
