using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

internal sealed class DataAssetEditorInstance : IAssetEditorInstance
{
    private readonly EditorAssetOpenContext Context;
    private readonly JsonAssetAuthoringDocument Document;

    public DataAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        if (!Context.Runtime.RegisteredDataAssetTypes.TryGet(Context.Asset.Type,
                out var DescriptorValue) || DescriptorValue is null)
        {
            throw new InvalidOperationException(
                $"Data asset type is not registered: {Context.Asset.Type}");
        }
        Document = new JsonAssetAuthoringDocument(Context.Asset, Context.Path,
            Validate);
    }

    public AssetDescriptor Asset => Context.Asset;
    public string EditorId => "data-asset-editor";
    public string Title => Asset.Name;
    public string PrimaryPaneId => "details";
    public bool IsDirty => Document.IsDirty;
    public bool IsReadOnly => Document.IsReadOnly;
    public IReadOnlyList<AssetEditorPaneDefinition> Panes { get; } =
    [
        new("details", "Properties", false),
        new("summary", "Details"),
        new("source", "Source"),
        new("schema", "Schema"),
        new("diagnostics", "Diagnostics"),
    ];
    public IReadOnlyList<AssetEditorCommandDefinition> Commands { get; } =
    [
        new("undo", "Undo", "Edit", 100, "reset", true),
        new("redo", "Redo", "Edit", 110, "reset", true),
    ];
    private DataAssetTypeDescriptor Descriptor
    {
        get
        {
            if (!Context.Runtime.RegisteredDataAssetTypes.TryGet(Asset.Type,
                    out var Value) || Value is null)
            {
                throw new InvalidOperationException(
                    $"Data asset type is no longer registered: {Asset.Type}");
            }
            return Value;
        }
    }

    public EditorLayoutDocument CreateDefaultLayout() =>
        AssetEditorInstanceUtilities.CreateDocumentLayout("details", "summary",
            "source", "schema", "diagnostics");

    public bool CanExecuteCommand(string CommandId)
    {
        return CommandId switch
        {
            "undo" => !IsReadOnly && Document.CanUndo,
            "redo" => !IsReadOnly && Document.CanRedo,
            _ => false,
        };
    }

    public void ExecuteCommand(string CommandId)
    {
        if (CommandId == "undo")
        {
            Document.Undo();
        }
        else if (CommandId == "redo")
        {
            Document.Redo();
        }
    }

    public bool TryHandleAction(string Action)
    {
        const string FieldPrefix = "editor.data-asset-field:";
        if (Action.StartsWith(FieldPrefix, StringComparison.Ordinal))
        {
            var Payload = Action[FieldPrefix.Length..];
            var Separator = Payload.IndexOf(':');
            if (Separator > 0)
            {
                SetField(Payload[..Separator], Payload[(Separator + 1)..]);
            }
            return true;
        }

        const string TogglePrefix = "editor.data-asset-toggle:";
        if (Action.StartsWith(TogglePrefix, StringComparison.Ordinal))
        {
            Toggle(Action[TogglePrefix.Length..]);
            return true;
        }
        return false;
    }

    public string BuildPaneMarkup(string PaneId)
    {
        return PaneId switch
        {
            "details" => BuildPropertiesMarkup(),
            "summary" => BuildSummaryMarkup(),
            "source" => AssetEditorInstanceUtilities.Source(Document.Source),
            "schema" => AssetEditorInstanceUtilities.Source(
                Descriptor.Schema().ToJsonString(new JsonSerializerOptions
                    (JsonSerializerOptions.Default)
                {
                    WriteIndented = true,
                })),
            "diagnostics" => AssetEditorInstanceUtilities.Diagnostics([]),
            _ => string.Empty,
        };
    }

    public void Save()
    {
        Document.Save();
    }

    public void Update(float DeltaSeconds)
    {
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["assetId"] = Asset.Id,
            ["editorRootId"] = Context.EditorRootId,
            ["typeId"] = Descriptor.TypeId,
            ["assetSchemaVersion"] = Descriptor.SchemaVersion,
            ["runtimeType"] = Descriptor.RuntimeType.FullName,
            ["dirty"] = IsDirty,
            ["readOnly"] = IsReadOnly,
            ["documentRevision"] = Document.Revision,
            ["savepointRevision"] = Document.SavepointRevision,
            ["propertyCount"] = Descriptor.Properties.Count,
            ["document"] = Document.Snapshot,
        };
    }

    public void Dispose()
    {
    }

    private void Validate(JsonObject Root)
    {
        var Registry = Context.Runtime.Assets ?? throw new InvalidOperationException(
            "Asset registry is unavailable");
        _ = DataAssetSerializer.Parse(Root.ToJsonString(), Asset.Id, Descriptor, Registry);
    }

    private void SetField(string JsonName, string Value)
    {
        var Property = Descriptor.Properties.FirstOrDefault(Property =>
            Property.JsonName == JsonName);
        if (Property is null)
        {
            Context.SetStatus($"Unknown data asset property: {JsonName}");
            return;
        }

        var Changed = Document.Edit("data-asset-field", Root =>
        {
            Root[JsonName] = ParseValue(Property.PropertyType, Value);
        }, out var Message);
        if (!Changed && Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
    }

    private void Toggle(string JsonName)
    {
        var Property = Descriptor.Properties.FirstOrDefault(Property =>
            Property.JsonName == JsonName && Property.PropertyType == typeof(bool));
        if (Property is null)
        {
            Context.SetStatus($"Data asset property is not boolean: {JsonName}");
            return;
        }

        var Changed = Document.Edit("data-asset-toggle", Root =>
        {
            Root[JsonName] = !(Root[JsonName]?.GetValue<bool>() ?? false);
        }, out var Message);
        if (!Changed && Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
    }

    private static JsonNode? ParseValue(Type Type, string Value)
    {
        if (Type == typeof(string) || Type.IsEnum || IsAssetReference(Type))
        {
            return Value.Length == 0 && IsAssetReference(Type) ? null : Value;
        }
        if (Type == typeof(bool))
        {
            return bool.Parse(Value);
        }
        if (Type == typeof(byte) || Type == typeof(short) || Type == typeof(int) ||
            Type == typeof(long) || Type == typeof(ushort) || Type == typeof(uint) ||
            Type == typeof(ulong))
        {
            return JsonNode.Parse(Value) ?? throw new InvalidDataException(
                "Integer value is required");
        }
        if (Type == typeof(float) || Type == typeof(double) || Type == typeof(decimal))
        {
            _ = double.Parse(Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            return JsonNode.Parse(Value) ?? throw new InvalidDataException(
                "Numeric value is required");
        }

        return JsonNode.Parse(Value) ?? throw new InvalidDataException(
            "A JSON value is required");
    }

    private string BuildPropertiesMarkup()
    {
        var Root = Document.Snapshot;
        var Markup = new StringBuilder("<div class=\"asset-inspector data-asset-properties\">");
        foreach (var Property in Descriptor.Properties)
        {
            var Value = Root[Property.JsonName];
            if (Property.PropertyType == typeof(bool))
            {
                var Checked = Value?.GetValue<bool>() == true;
                var Disabled = IsReadOnly ? " disabled=\"disabled\"" : string.Empty;
                Markup.Append("<div class=\"asset-field\"><label>")
                    .Append(AssetEditorInstanceUtilities.Escape(Property.Name))
                    .Append("</label><button class=\"settings-toggle ")
                    .Append(Checked ? "enabled" : string.Empty)
                    .Append("\" data-action=\"editor.data-asset-toggle:")
                    .Append(AssetEditorInstanceUtilities.EscapeAttribute(Property.JsonName))
                    .Append("\"").Append(Disabled).Append('>')
                    .Append(Checked ? "Enabled" : "Disabled").Append("</button></div>");
                continue;
            }

            var Text = FormatValue(Property.PropertyType, Value);
            var Hint = BuildHint(Property);
            if (Property.PropertyType.IsArray ||
                Property.PropertyType != typeof(string) && !Property.PropertyType.IsEnum &&
                !IsNumber(Property.PropertyType) && !IsAssetReference(Property.PropertyType))
            {
                var Disabled = IsReadOnly ? " disabled=\"disabled\"" : string.Empty;
                Markup.Append("<div class=\"asset-field asset-field-large\"><label>")
                    .Append(AssetEditorInstanceUtilities.Escape(Property.Name))
                    .Append("</label><input data-change-action=\"editor.data-asset-field:")
                    .Append(AssetEditorInstanceUtilities.EscapeAttribute(Property.JsonName))
                    .Append(":\" value=\"")
                    .Append(AssetEditorInstanceUtilities.EscapeAttribute(Text))
                    .Append("\"").Append(Disabled).Append(" />")
                    .Append("<span class=\"asset-field-hint\">")
                    .Append(AssetEditorInstanceUtilities.Escape(Hint))
                    .Append("</span></div>");
            }
            else
            {
                Markup.Append(AssetEditorInstanceUtilities.Field(Property.Name, Text,
                    $"editor.data-asset-field:{Property.JsonName}:", IsReadOnly, Hint));
            }
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }

    private string BuildSummaryMarkup()
    {
        return "<div class=\"asset-inspector\">" +
            AssetEditorInstanceUtilities.ReadOnlyField("Asset ID", Asset.Id) +
            AssetEditorInstanceUtilities.ReadOnlyField("Type ID", Descriptor.TypeId) +
            AssetEditorInstanceUtilities.ReadOnlyField("Schema",
                Descriptor.SchemaVersion.ToString()) +
            AssetEditorInstanceUtilities.ReadOnlyField("Runtime Type",
                Descriptor.RuntimeType.FullName ?? Descriptor.RuntimeType.Name) +
            AssetEditorInstanceUtilities.ReadOnlyField("Properties",
                Descriptor.Properties.Count.ToString()) + "</div>";
    }

    private static string FormatValue(Type Type, JsonNode? Value)
    {
        if (Value is null)
        {
            return string.Empty;
        }
        if (Type == typeof(string) || Type.IsEnum || IsAssetReference(Type))
        {
            return Value.GetValue<string>();
        }
        return Value.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = Type.IsArray || Value is JsonObject,
        });
    }

    private static string BuildHint(DataAssetPropertyDescriptor Property)
    {
        var Parts = new List<string> { Property.PropertyType.Name };
        if (Property.PropertyType.IsEnum)
        {
            Parts.Add(string.Join(", ", Enum.GetNames(Property.PropertyType)));
        }
        if (Property.Constraints?.Required == true)
        {
            Parts.Add("required");
        }
        if (Property.Constraints is { } Constraints)
        {
            if (!double.IsNaN(Constraints.Minimum))
            {
                Parts.Add("min " + Constraints.Minimum.ToString(
                    CultureInfo.InvariantCulture));
            }
            if (!double.IsNaN(Constraints.Maximum))
            {
                Parts.Add("max " + Constraints.Maximum.ToString(
                    CultureInfo.InvariantCulture));
            }
        }
        if (IsAssetReference(Property.PropertyType))
        {
            Parts.Add("asset reference");
        }
        return string.Join(" · ", Parts);
    }

    private static bool IsNumber(Type Type) => Type == typeof(byte) ||
        Type == typeof(short) || Type == typeof(int) || Type == typeof(long) ||
        Type == typeof(ushort) || Type == typeof(uint) || Type == typeof(ulong) ||
        Type == typeof(float) || Type == typeof(double) || Type == typeof(decimal);

    private static bool IsAssetReference(Type Type) => Type.IsGenericType &&
        Type.GetGenericTypeDefinition() == typeof(AssetRef<>);
}
