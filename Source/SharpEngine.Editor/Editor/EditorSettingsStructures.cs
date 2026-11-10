using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using SharpEngine.Settings;
using SharpEngine.UI;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private readonly HashSet<string> CollapsedSettingsStructures = new(StringComparer.Ordinal);

    private static bool IsSettingsNumericType(Type Type)
    {
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        return Type == typeof(System.Numerics.Vector2) || Type == typeof(System.Numerics.Vector3) ||
            Type == typeof(System.Numerics.Vector4) || Type == typeof(System.Numerics.Quaternion) ||
            Type == typeof(SharpEngine.Components.Color);
    }

    private string BuildSettingsStructure(DevelopmentSettingsDescriptor Descriptor,
        string FieldPath, string[] Path, string Label, Type Type, Func<string> BuildMembers)
    {
        var Key = Descriptor.Attribute.Scope + ":" + Descriptor.Attribute.Id + ":" +
            FieldPath + ":" + JsonSerializer.Serialize(Path);
        var Id = "setting-struct-" + Uri.EscapeDataString(Key);
        var Expanded = !CollapsedSettingsStructures.Contains(Key);
        var Payload = new JsonObject
        {
            ["scope"] = Descriptor.Attribute.Scope.ToString(),
            ["key"] = Key,
        };
        return UiStructEditor.Render(Id, Label,
            (Nullable.GetUnderlyingType(Type) ?? Type).Name,
            Expanded ? BuildMembers() : string.Empty,
            "editor.settings-structure-fold:" + Uri.EscapeDataString(Payload.ToJsonString()),
            Expanded);
    }

    private bool HandleSettingsStructureAction(string Action)
    {
        if (HandleSettingsNumericAction(Action))
        {
            return true;
        }
        const string Prefix = "editor.settings-structure-fold:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var Payload = JsonNode.Parse(Uri.UnescapeDataString(Action[Prefix.Length..]))!.AsObject();
        var Scope = Enum.Parse<SettingsScope>(Payload["scope"]!.GetValue<string>());
        var Key = Payload["key"]!.GetValue<string>();
        if (!CollapsedSettingsStructures.Add(Key))
        {
            CollapsedSettingsStructures.Remove(Key);
        }
        RefreshSettingsWindow(Scope);
        MarkExtensionUiDirty();
        return true;
    }

    private string BuildSettingsNumericValue(DevelopmentSettingsDescriptor Descriptor,
        string FieldPath, string[] Path, string Label, Type Type, JsonNode? Value,
        JsonNode? Root, string Id)
    {
        string Binding(string Operation)
        {
            return "editor.settings-numeric:" + Uri.EscapeDataString(new JsonObject
            {
                ["scope"] = Descriptor.Attribute.Scope.ToString(),
                ["page"] = Descriptor.Attribute.Id,
                ["field"] = FieldPath,
                ["path"] = new JsonArray(Path.Select(Segment =>
                    (JsonNode?)JsonValue.Create(Segment)).ToArray()),
                ["expected"] = MapEditing.Fingerprint(Root),
                ["operation"] = Operation,
            }.ToJsonString()) + ":";
        }
        return MapEditing.ValueActionMarkup(Label, Id, Type, Value,
            Binding(Type == typeof(SharpEngine.Components.Color) ? "color" : "value"),
            Axis => Binding("axis," + Axis.ToString(CultureInfo.InvariantCulture)), false);
    }

    private bool HandleSettingsNumericAction(string Action)
    {
        const string Prefix = "editor.settings-numeric:";
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
            var Descriptor = _developmentSettings!.Find(Scope, Id) ??
                throw new InvalidDataException("Unknown settings page");
            var (Type, Value) = ResolveSettingsField(Descriptor, FieldPath);
            var Root = JsonNode.Parse(_developmentSettings.EditorValue(Type, Value));
            if (MapEditing.Fingerprint(Root) != Payload["expected"]!.GetValue<string>())
            {
                throw new InvalidDataException("REVISION_CONFLICT: settings value changed while editing");
            }
            var Path = Payload["path"]!.AsArray().Select(Node => Node!.GetValue<string>()).ToArray();
            var Wrapper = new JsonObject
            {
                ["value"] = Root,
            };
            MapEditing.SetValue(Wrapper, new[] { "value" }.Concat(Path).ToArray(),
                Payload["operation"]!.GetValue<string>(), Parts.Length > 1 ? Parts[1] : string.Empty,
                Type, GetSettingsMemberType);
            if (!_developmentSettings.TrySet(Scope, Id, FieldPath,
                Wrapper["value"]?.ToJsonString() ?? "null", out var Error))
            {
                throw new InvalidDataException(Error);
            }
            SettingsCollectionRefreshPending.Add(Scope);
            MarkExtensionUiDirty();
        }
        catch (Exception Error) when (Error is ArgumentException or InvalidOperationException or
            InvalidDataException or JsonException or FormatException or OverflowException)
        {
            SetStatus(Error.Message);
        }
        return true;
    }
}
