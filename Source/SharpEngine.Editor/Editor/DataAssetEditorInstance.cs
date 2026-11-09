using System.Globalization;
using System.Net;
using System.Numerics;
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
        const string ColorPrefix = "editor.data-asset-color:";
        if (Action.StartsWith(ColorPrefix, StringComparison.Ordinal))
        {
            var Payload = Action[ColorPrefix.Length..];
            var Separator = Payload.IndexOf(':');
            if (Separator > 0)
            {
                var Name = Payload[..Separator];
                var Changed = Document.Edit("data-asset-color", Root =>
                {
                    var Values = JsonNode.Parse(Payload[(Separator + 1)..]) as JsonArray;
                    if (Values is null || Values.Count != 4 ||
                        Values.Any(Value => Value is null || !float.IsFinite(Value.GetValue<float>())))
                    {
                        throw new InvalidDataException("Color requires four finite RGBA components");
                    }
                    var Type = Descriptor.Properties.Single(Property => Property.JsonName == Name).PropertyType;
                    if (Type != typeof(Color))
                    {
                        throw new InvalidDataException("The selected property is not a Color");
                    }
                    if (Root[Name] is JsonObject Object)
                    {
                        for (var Index = 0; Index < 4; Index++)
                        {
                            Object["rgba"[Index].ToString()] = Values[Index]!.DeepClone();
                        }
                    }
                    else
                    {
                        Root[Name] = Values.DeepClone();
                    }
                }, out var Message);
                if (!Changed && Message.Length != 0)
                {
                    Context.SetStatus(Message);
                }
            }
            return true;
        }
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

        const string AxisPrefix = "editor.data-asset-axis:";
        if (Action.StartsWith(AxisPrefix, StringComparison.Ordinal))
        {
            var Payload = Action[AxisPrefix.Length..];
            var NameSeparator = Payload.IndexOf(':');
            var AxisSeparator = NameSeparator < 0 ? -1 : Payload.IndexOf(':', NameSeparator + 1);
            if (NameSeparator > 0 && AxisSeparator > NameSeparator)
            {
                SetAxis(Payload[..NameSeparator],
                    int.Parse(Payload[(NameSeparator + 1)..AxisSeparator], CultureInfo.InvariantCulture),
                    Payload[(AxisSeparator + 1)..]);
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

    private void SetAxis(string JsonName, int Axis, string Value)
    {
        var Property = Descriptor.Properties.FirstOrDefault(Property =>
            Property.JsonName == JsonName && VectorSize(Property.PropertyType) > 0);
        if (Property is null)
        {
            Context.SetStatus($"Data asset property is not a vector: {JsonName}");
            return;
        }

        var Changed = Document.Edit("data-asset-axis", Root =>
        {
            var Current = Root[JsonName];
            var Count = VectorSize(Property.PropertyType);
            var Values = ReadAxes(Property.PropertyType, Current);
            if (Axis < 0 || Axis >= Count || Values is null)
            {
                throw new InvalidDataException($"Invalid vector axis {Axis} for {JsonName}");
            }
            var Number = float.Parse(Value, NumberStyles.Float,
                CultureInfo.InvariantCulture);
            if (!float.IsFinite(Number))
            {
                throw new InvalidDataException("Vector value must be finite");
            }
            if (Current is JsonArray Array)
            {
                Array[Axis] = Number;
            }
            else
            {
                var Axes = Property.PropertyType == typeof(Color) ? "rgba" : "xyzw";
                Current!.AsObject()[Axes[Axis].ToString()] = Number;
            }
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
                Markup.Append(SharpEngine.UI.UiValueEditors.Boolean(Property.Name, "dataAsset-" + Property.JsonName, Checked,
                    "editor.data-asset-toggle:" + Property.JsonName, IsReadOnly));
                continue;
            }

            if (Property.PropertyType.IsEnum)
            {
                Markup.Append(SharpEngine.UI.UiValueEditors.Choice(Property.Name, "dataAsset-" + Property.JsonName,
                    Value?.GetValue<string>() ?? string.Empty, Enum.GetNames(Property.PropertyType).Select(Name => (Name, Name)),
                    "editor.data-asset-field:" + Property.JsonName + ":", IsReadOnly));
                continue;
            }
            if (IsAssetReference(Property.PropertyType))
            {
                var ReferenceType = Property.PropertyType.GetGenericArguments()[0];
                var Options = new List<(string Id, string Label)>
                {
                    (string.Empty, "None"),
                };
                Options.AddRange(Context.Runtime.Assets!.Assets.Where(Asset => Asset.Exists && EditorAssetReferenceTypes.Matches(Asset.Type, ReferenceType))
                    .Select(Asset => (Asset.Id, Asset.Name)));
                Markup.Append(SharpEngine.UI.UiValueEditors.Reference(Property.Name, "dataAsset-" + Property.JsonName,
                    Value?.GetValue<string>() ?? string.Empty, Options, "editor.data-asset-field:" + Property.JsonName + ":", IsReadOnly));
                continue;
            }

            var VectorCount = VectorSize(Property.PropertyType);
            var Values = ReadAxes(Property.PropertyType, Value);
            if (VectorCount > 0 && Values is not null)
            {
                var Inputs = Enumerable.Range(0, VectorCount).Select(Index =>
                    new SharpEngine.UI.UiValueInput(
                        "dataAsset-" + Property.JsonName + "-" + Index.ToString(CultureInfo.InvariantCulture),
                        SharpEngine.UI.UiValueEditors.Format(Values[Index]),
                        "editor.data-asset-axis:" + Property.JsonName + ":" + Index.ToString(CultureInfo.InvariantCulture) + ":",
                        IsReadOnly, "number")).ToArray();
                Markup.Append(Property.PropertyType == typeof(Color)
                    ? SharpEngine.UI.UiValueEditors.Color(Property.Name, Inputs,
                        "editor.data-asset-color:" + Property.JsonName + ":")
                    : SharpEngine.UI.UiValueEditors.Vector(Property.Name, Inputs,
                        Property.PropertyType == typeof(Vector2) ? "XY" : "XYZW"));
                continue;
            }

            var Text = FormatValue(Property.PropertyType, Value);
            if (VectorCount > 0)
            {
                Markup.Append(SharpEngine.UI.UiValueEditors.Multiline(Property.Name,
                    new SharpEngine.UI.UiValueInput("dataAsset-" + Property.JsonName, Text,
                        string.Empty, true), 2, "The current serialized value does not expose numeric components"));
                continue;
            }
            var ValueInput = new SharpEngine.UI.UiValueInput("dataAsset-" + Property.JsonName,
                Text, "editor.data-asset-field:" + Property.JsonName + ":", IsReadOnly,
                IsNumber(Property.PropertyType) ?
                    (IsInteger(Property.PropertyType) ? "integer" : "number") : "text");
            if (IsNumber(Property.PropertyType) && Property.Constraints is { } Constraints &&
                !double.IsNaN(Constraints.Minimum) && !double.IsNaN(Constraints.Maximum))
            {
                Markup.Append(SharpEngine.UI.UiValueEditors.Range(Property.Name, ValueInput,
                    Constraints.Minimum, Constraints.Maximum,
                    IsInteger(Property.PropertyType) ? 1 : 0.01, BuildHint(Property), "asset-field"));
            }
            else if (IsNumber(Property.PropertyType))
            {
                Markup.Append(SharpEngine.UI.UiValueEditors.Number(Property.Name, ValueInput,
                    BuildHint(Property), "asset-field"));
            }
            else
            {
                Markup.Append(AssetEditorInstanceUtilities.Field(Property.Name, Text,
                    "editor.data-asset-field:" + Property.JsonName + ":", IsReadOnly,
                    BuildHint(Property), "dataAsset-" + Property.JsonName));
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

    private static bool IsInteger(Type Type) => Type == typeof(byte) ||
        Type == typeof(short) || Type == typeof(int) || Type == typeof(long) ||
        Type == typeof(ushort) || Type == typeof(uint) || Type == typeof(ulong);

    private static int VectorSize(Type Type) => Type == typeof(Vector2) ? 2 :
        Type == typeof(Vector3) ? 3 : Type == typeof(Vector4) ||
        Type == typeof(Quaternion) || Type == typeof(Color) ? 4 : 0;

    private static double[]? ReadAxes(Type Type, JsonNode? Value)
    {
        var Count = VectorSize(Type);
        if (Count == 0)
        {
            return null;
        }
        if (Value is JsonArray Array && Array.Count == Count)
        {
            return Array.Select(Node => Node!.Deserialize<double>()).ToArray();
        }
        var Axes = Type == typeof(Color) ? "rgba" : "xyzw";
        var Object = Value as JsonObject;
        if (Object is not null && Enumerable.Range(0, Count).All(Index => Object[Axes[Index].ToString()] is JsonValue))
        {
            return Enumerable.Range(0, Count).Select(Index => Object[Axes[Index].ToString()]!.Deserialize<double>()).ToArray();
        }
        return null;
    }

    private static bool IsAssetReference(Type Type) => Type.IsGenericType &&
        Type.GetGenericTypeDefinition() == typeof(AssetRef<>);
}
