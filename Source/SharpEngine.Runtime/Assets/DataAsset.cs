using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SharpEngine.Components;

namespace SharpEngine.Assets;

/// <summary>Declares a stable, editor-visible type for a data asset.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DataAssetTypeAttribute : Attribute
{
    public DataAssetTypeAttribute(string TypeId)
    {
        this.TypeId = TypeId;
    }

    public new string TypeId
    {
        get;
    }

    public int SchemaVersion
    {
        get;
        init;
    } = 1;
}

/// <summary>Declares validation metadata for one serialized data-asset property.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class DataPropertyAttribute : Attribute
{
    public bool Required
    {
        get;
        init;
    }

    public double Minimum
    {
        get;
        init;
    } = double.NaN;

    public double Maximum
    {
        get;
        init;
    } = double.NaN;
}

/// <summary>
/// Base for user-defined data assets. AssetId and Name are envelope metadata;
/// derived public properties are the authoritative data fields.
/// </summary>
public abstract class DataAsset
{
    [JsonIgnore]
    public string AssetId
    {
        get;
        internal set;
    } = string.Empty;

    [JsonIgnore]
    public string Name
    {
        get;
        internal set;
    } = string.Empty;
}

/// <summary>
/// Convenience base used by project code to define a strongly typed asset.
/// The static helpers are intentionally small; editor and MCP writes still go
/// through the serialized data-asset contract.
/// </summary>
public abstract class DataAsset<TDataAsset> : DataAsset
    where TDataAsset : DataAsset<TDataAsset>
{
    public static TDataAsset Create(string AssetId, string Name)
    {
        return DataAssetFactory.Create<TDataAsset>(AssetId, Name);
    }

    public static TDataAsset Load(AssetRegistry Registry, string AssetId) =>
        DataAssetSerializer.Load<TDataAsset>(Registry, AssetId);
}

/// <summary>Creates concrete data-asset types, including types derived from an abstract data-asset base.</summary>
public static class DataAssetFactory
{
    public static TDataAsset Create<TDataAsset>(string AssetId, string Name)
        where TDataAsset : DataAsset
    {
        if (string.IsNullOrWhiteSpace(AssetId))
        {
            throw new ArgumentException("A stable asset ID is required", nameof(AssetId));
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("A data asset name is required", nameof(Name));
        }
        if (typeof(TDataAsset).IsAbstract)
        {
            throw new InvalidOperationException(
                $"Data asset type '{typeof(TDataAsset).FullName}' is abstract and cannot be instantiated");
        }

        var Asset = Activator.CreateInstance<TDataAsset>();
        Asset.AssetId = AssetId.Trim();
        Asset.Name = Name.Trim();
        return Asset;
    }
}

/// <summary>Describes a serializable property in a registered data-asset type.</summary>
public sealed record DataAssetPropertyDescriptor(
    string Name,
    string JsonName,
    Type PropertyType,
    PropertyInfo Property,
    DataPropertyAttribute? Constraints);

/// <summary>Stable type and schema information for a data asset.</summary>
public sealed class DataAssetTypeDescriptor
{
    internal DataAssetTypeDescriptor(Type RuntimeType, DataAssetTypeAttribute Attribute,
        IReadOnlyList<DataAssetPropertyDescriptor> Properties)
    {
        this.RuntimeType = RuntimeType;
        TypeId = Attribute.TypeId;
        SchemaVersion = Attribute.SchemaVersion;
        this.Properties = Properties;
    }

    public Type RuntimeType
    {
        get;
    }

    public string TypeId
    {
        get;
    }

    public int SchemaVersion
    {
        get;
    }

    public IReadOnlyList<DataAssetPropertyDescriptor> Properties
    {
        get;
    }

    /// <summary>Returns the JSON Schema used by Inspector and AI tooling.</summary>
    public JsonObject Schema()
    {
        var Fields = new JsonObject();
        var Required = new JsonArray();
        foreach (var Property in Properties)
        {
            var Field = DataAssetSchema.SchemaFor(Property.PropertyType);
            if (Property.Constraints is { } Constraints)
            {
                if (!double.IsNaN(Constraints.Minimum))
                {
                    Field["minimum"] = Constraints.Minimum;
                }
                if (!double.IsNaN(Constraints.Maximum))
                {
                    Field["maximum"] = Constraints.Maximum;
                }
                if (Constraints.Required)
                {
                    Required.Add(Property.JsonName);
                }
            }

            Fields[Property.JsonName] = Field;
        }

        var Schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["properties"] = Fields,
            ["x-sharpengine-asset-type"] = TypeId,
            ["x-sharpengine-schema-version"] = SchemaVersion,
        };
        if (Required.Count != 0)
        {
            Schema["required"] = Required;
        }
        return Schema;
    }
}

/// <summary>
/// Registry for C# data-asset definitions. Projects can register types
/// explicitly or discover types from their runtime assemblies.
/// </summary>
public sealed class DataAssetTypeRegistry
{
    private readonly Dictionary<string, DataAssetTypeDescriptor> Types =
        new(StringComparer.Ordinal);

    public IReadOnlyCollection<DataAssetTypeDescriptor> Descriptors
    {
        get
        {
            return Types.Values;
        }
    }

    public void Register<TDataAsset>() where TDataAsset : DataAsset
    {
        Register(typeof(TDataAsset));
    }

    public void Register(Type RuntimeType)
    {
        ArgumentNullException.ThrowIfNull(RuntimeType);
        var Descriptor = DataAssetSchema.GetDescriptor(RuntimeType);
        if (Types.TryGetValue(Descriptor.TypeId, out var Existing) &&
            Existing.RuntimeType != RuntimeType)
        {
            throw new InvalidDataException(
                $"Data asset type ID '{Descriptor.TypeId}' is already registered by '{Existing.RuntimeType.FullName}'.");
        }

        Types[Descriptor.TypeId] = Descriptor;
    }

    public bool TryGet(string TypeId, out DataAssetTypeDescriptor? Descriptor)
    {
        return Types.TryGetValue(TypeId, out Descriptor);
    }

    public bool TryGetAssignable<TDataAsset>(string TypeId,
        out DataAssetTypeDescriptor? Descriptor)
        where TDataAsset : DataAsset
    {
        if (!TryGet(TypeId, out Descriptor) || Descriptor is null ||
            !typeof(TDataAsset).IsAssignableFrom(Descriptor.RuntimeType))
        {
            Descriptor = null;
            return false;
        }

        return true;
    }

    public TDataAsset Load<TDataAsset>(AssetRegistry Registry, string AssetId)
        where TDataAsset : DataAsset
    {
        var Asset = DataAssetSerializer.Load(Registry, AssetId, this);
        if (Asset is not TDataAsset TypedAsset)
        {
            throw new InvalidDataException(
                $"Asset '{AssetId}' is '{Asset.GetType().FullName}', which is not assignable to '{typeof(TDataAsset).FullName}'");
        }

        return TypedAsset;
    }

    public static DataAssetTypeRegistry Discover(params Assembly[] Assemblies)
    {
        ArgumentNullException.ThrowIfNull(Assemblies);
        var Registry = new DataAssetTypeRegistry();
        foreach (var Assembly in Assemblies)
        {
            ArgumentNullException.ThrowIfNull(Assembly);
            foreach (var Type in Assembly.GetTypes().Where(Type =>
                         !Type.IsAbstract && typeof(DataAsset).IsAssignableFrom(Type)))
            {
                Registry.Register(Type);
            }
        }

        return Registry;
    }
}

/// <summary>
/// Serializes, validates, loads and atomically saves versioned data assets.
/// </summary>
public static class DataAssetSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize<TDataAsset>(TDataAsset Asset,
        bool Indented = true, AssetRegistry? Registry = null)
        where TDataAsset : DataAsset
    {
        ArgumentNullException.ThrowIfNull(Asset);
        return Serialize((DataAsset)Asset, Asset.AssetId, Asset.Name, Indented, Registry);
    }

    public static string Serialize<TDataAsset>(TDataAsset Asset, string AssetId,
        string Name, bool Indented = true, AssetRegistry? Registry = null)
        where TDataAsset : DataAsset
    {
        ArgumentNullException.ThrowIfNull(Asset);
        return Serialize((DataAsset)Asset, AssetId, Name, Indented, Registry);
    }

    public static string Serialize(DataAsset Asset, bool Indented = true,
        AssetRegistry? Registry = null)
    {
        ArgumentNullException.ThrowIfNull(Asset);
        return Serialize(Asset, Asset.AssetId, Asset.Name, Indented, Registry);
    }

    public static string Serialize(DataAsset Asset, string AssetId, string Name,
        bool Indented = true, AssetRegistry? Registry = null)
    {
        ArgumentNullException.ThrowIfNull(Asset);
        var Descriptor = DataAssetSchema.GetDescriptor(Asset.GetType());
        RequireEnvelope(AssetId, Name);
        ValidateValues(Asset, Descriptor, Registry);

        var Root = new JsonObject
        {
            ["schemaVersion"] = Descriptor.SchemaVersion,
            ["kind"] = Descriptor.TypeId,
            ["assetId"] = AssetId.Trim(),
            ["name"] = Name.Trim(),
        };

        foreach (var Property in Descriptor.Properties)
        {
            var Value = Property.Property.GetValue(Asset);
            Root[Property.JsonName] = JsonSerializer.SerializeToNode(
                Value, Property.PropertyType, Options);
        }

        return Root.ToJsonString(new JsonSerializerOptions(Options)
        {
            WriteIndented = Indented,
        });
    }

    public static TDataAsset Parse<TDataAsset>(string Json, string AssetId,
        AssetRegistry? Registry = null)
        where TDataAsset : DataAsset
    {
        var Descriptor = DataAssetSchema.GetDescriptor(typeof(TDataAsset));
        return (TDataAsset)ParseCore(Json, AssetId, Descriptor, Registry);
    }

    public static TDataAsset Load<TDataAsset>(AssetRegistry Registry, string AssetId)
        where TDataAsset : DataAsset
    {
        ArgumentNullException.ThrowIfNull(Registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(AssetId);
        var Asset = Registry.Find(AssetId) ??
            throw new KeyNotFoundException($"Asset was not registered: '{AssetId}'");
        var Descriptor = DataAssetSchema.GetDescriptor(typeof(TDataAsset));
        if (!string.Equals(Asset.Type, Descriptor.TypeId, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Asset '{AssetId}' is '{Asset.Type}', expected '{Descriptor.TypeId}'");
        var Path = Registry.ResolvePath(Asset) ??
            throw new InvalidDataException($"Asset '{AssetId}' has no resolved path");
        return Parse<TDataAsset>(File.ReadAllText(Path), Asset.Id, Registry);
    }

    public static DataAsset Load(AssetRegistry Registry, string AssetId,
        DataAssetTypeRegistry TypeRegistry)
    {
        ArgumentNullException.ThrowIfNull(Registry);
        ArgumentNullException.ThrowIfNull(TypeRegistry);
        ArgumentException.ThrowIfNullOrWhiteSpace(AssetId);
        var Asset = Registry.Find(AssetId) ??
            throw new KeyNotFoundException($"Asset was not registered: '{AssetId}'");
        if (!TypeRegistry.TryGet(Asset.Type, out var Descriptor) || Descriptor is null)
        {
            throw new InvalidDataException(
                $"Data asset type '{Asset.Type}' is not registered for '{AssetId}'");
        }

        var Path = Registry.ResolvePath(Asset) ??
            throw new InvalidDataException($"Asset '{AssetId}' has no resolved path");
        return Parse(File.ReadAllText(Path), Asset.Id, Descriptor, Registry);
    }

    public static DataAsset Parse(string Json, string AssetId,
        DataAssetTypeDescriptor Descriptor, AssetRegistry? Registry = null)
    {
        ArgumentNullException.ThrowIfNull(Descriptor);
        return ParseCore(Json, AssetId, Descriptor, Registry);
    }

    private static DataAsset ParseCore(string Json, string AssetId,
        DataAssetTypeDescriptor Descriptor, AssetRegistry? Registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Json);
        ArgumentException.ThrowIfNullOrWhiteSpace(AssetId);
        if (Descriptor.RuntimeType.IsAbstract)
        {
            throw new InvalidDataException(
                $"Data asset type '{Descriptor.RuntimeType.FullName}' is abstract and cannot be loaded");
        }

        try
        {
            var Root = JsonNode.Parse(Json) as JsonObject ??
                throw new InvalidDataException("Data asset root must be an object");
            ValidateEnvelope(Root, Descriptor, AssetId);

            var Allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "schemaVersion", "kind", "assetId", "name",
            };
            foreach (var Property in Descriptor.Properties)
            {
                if (!Root.ContainsKey(Property.JsonName))
                    throw new InvalidDataException(
                        $"Data asset '{AssetId}' is missing field '{Property.JsonName}'");
                Allowed.Add(Property.JsonName);
            }

            foreach (var Property in Root)
            {
                if (!Allowed.Contains(Property.Key))
                    throw new InvalidDataException(
                        $"Data asset '{AssetId}' contains unknown field '{Property.Key}'");
            }

            var Data = (JsonObject)Root.DeepClone();
            var Name = Data["name"]!.GetValue<string>();
            Data.Remove("schemaVersion");
            Data.Remove("kind");
            Data.Remove("assetId");
            Data.Remove("name");

            var Asset = JsonSerializer.Deserialize(Data.ToJsonString(),
                    Descriptor.RuntimeType, Options) as DataAsset ??
                throw new InvalidDataException($"Data asset '{AssetId}' produced no value");
            Asset.AssetId = AssetId.Trim();
            Asset.Name = Name;
            ValidateValues(Asset, Descriptor, Registry);
            return Asset;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception Exception) when (Exception is JsonException or
            InvalidOperationException or NotSupportedException or FormatException or
            ArgumentException)
        {
            throw new InvalidDataException(
                $"Invalid data asset '{AssetId}': {Exception.Message}", Exception);
        }
    }

    public static void Save<TDataAsset>(AssetRegistry Registry, string AssetId,
        TDataAsset Asset, bool Indented = true)
        where TDataAsset : DataAsset
    {
        ArgumentNullException.ThrowIfNull(Registry);
        ArgumentNullException.ThrowIfNull(Asset);
        var AssetDescriptor = Registry.Find(AssetId) ??
            throw new KeyNotFoundException($"Asset was not registered: '{AssetId}'");
        if (AssetDescriptor.ReadOnly)
        {
            throw new InvalidOperationException($"Asset '{AssetId}' is read-only");
        }

        var RuntimeDescriptor = DataAssetSchema.GetDescriptor(Asset.GetType());
        if (!string.Equals(AssetDescriptor.Type, RuntimeDescriptor.TypeId,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Asset '{AssetId}' is '{AssetDescriptor.Type}', but the supplied value is " +
                $"'{RuntimeDescriptor.TypeId}'");
        }

        var Path = Registry.ResolvePath(AssetDescriptor) ??
            throw new InvalidDataException($"Asset '{AssetId}' has no resolved path");
        var Json = Serialize((DataAsset)Asset, AssetDescriptor.Id,
            AssetDescriptor.Name, Indented, Registry);
        var Directory = System.IO.Path.GetDirectoryName(Path) ??
            throw new InvalidDataException($"Asset '{AssetId}' has no parent directory");
        System.IO.Directory.CreateDirectory(Directory);
        var TemporaryPath = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(TemporaryPath, Json, new System.Text.UTF8Encoding(false));
            File.Move(TemporaryPath, Path, true);
        }
        finally
        {
            if (File.Exists(TemporaryPath))
            {
                File.Delete(TemporaryPath);
            }
        }
    }

    private static void ValidateEnvelope(JsonObject Root,
        DataAssetTypeDescriptor Descriptor, string AssetId)
    {
        if (!Root.TryGetPropertyValue("schemaVersion", out var VersionNode) ||
            VersionNode?.GetValue<int>() != Descriptor.SchemaVersion)
            throw new InvalidDataException(
                $"Data asset '{AssetId}' requires schemaVersion={Descriptor.SchemaVersion}");
        if (!Root.TryGetPropertyValue("kind", out var KindNode) ||
            !string.Equals(KindNode?.GetValue<string>(), Descriptor.TypeId,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Data asset '{AssetId}' requires kind='{Descriptor.TypeId}'");
        if (!Root.TryGetPropertyValue("assetId", out var IdNode) ||
            !string.Equals(IdNode?.GetValue<string>(), AssetId,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Data asset ID does not match the registered ID '{AssetId}'");
        if (!Root.TryGetPropertyValue("name", out var NameNode) ||
            string.IsNullOrWhiteSpace(NameNode?.GetValue<string>()))
            throw new InvalidDataException($"Data asset '{AssetId}' requires a name");
    }

    private static void RequireEnvelope(string AssetId, string Name)
    {
        if (string.IsNullOrWhiteSpace(AssetId))
            throw new ArgumentException("A stable asset ID is required", nameof(AssetId));
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("A data asset name is required", nameof(Name));
    }

    private static void ValidateValues(DataAsset Asset,
        DataAssetTypeDescriptor Descriptor, AssetRegistry? Registry)
    {
        foreach (var Property in Descriptor.Properties)
        {
            var Value = Property.Property.GetValue(Asset);
            var Constraints = Property.Constraints;
            if (Constraints?.Required == true && (Value is null ||
                Value is string Text && string.IsNullOrWhiteSpace(Text) ||
                Value is IAssetReference RequiredReference && string.IsNullOrWhiteSpace(RequiredReference.Id)))
            {
                throw new InvalidDataException(
                    $"Data asset '{Descriptor.TypeId}' requires '{Property.JsonName}'");
            }

            if (Value is not null && (Constraints is not null) &&
                (!double.IsNaN(Constraints.Minimum) || !double.IsNaN(Constraints.Maximum)))
            {
                if (!TryGetNumber(Value, out var Number) || !double.IsFinite(Number))
                    throw new InvalidDataException(
                        $"Data asset property '{Property.JsonName}' must be a finite number");
                if (!double.IsNaN(Constraints.Minimum) && Number < Constraints.Minimum)
                    throw new InvalidDataException(
                        $"Data asset property '{Property.JsonName}' is below its minimum");
                if (!double.IsNaN(Constraints.Maximum) && Number > Constraints.Maximum)
                    throw new InvalidDataException(
                        $"Data asset property '{Property.JsonName}' is above its maximum");
            }

            if (Registry is not null && Value is IAssetReference AssetReference &&
                !string.IsNullOrWhiteSpace(AssetReference.Id))
            {
                ValidateReference(Registry, Property, AssetReference);
            }
        }
    }

    private static void ValidateReference(AssetRegistry Registry,
        DataAssetPropertyDescriptor Property, IAssetReference Reference)
    {
        var Referenced = Registry.Find(Reference.Id);
        if (Referenced is null || !Referenced.Exists)
            throw new InvalidDataException(
                $"Data asset reference '{Property.JsonName}' points to missing asset '{Reference.Id}'");

        if (!Property.PropertyType.IsGenericType ||
            Property.PropertyType.GetGenericTypeDefinition() != typeof(AssetRef<>))
            return;
        var Expected = Property.PropertyType.GetGenericArguments()[0].Name
            .Replace("Asset", string.Empty, StringComparison.Ordinal);
        var Matches = string.Equals(Expected, Referenced.Type,
            StringComparison.OrdinalIgnoreCase) ||
            Expected.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) &&
            (Referenced.Type.Equals("Mesh", StringComparison.OrdinalIgnoreCase) ||
             Referenced.Type.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase));
        if (!Matches)
            throw new InvalidDataException(
                $"Data asset reference '{Property.JsonName}' points to '{Referenced.Type}', expected '{Expected}'");
    }

    private static bool TryGetNumber(object Value, out double Number)
    {
        try
        {
            Number = Convert.ToDouble(Value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception Exception) when (Exception is InvalidCastException or
            FormatException or OverflowException)
        {
            Number = 0;
            return false;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var Result = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
        };
        Result.Converters.Add(new AssetRefJsonConverterFactory());
        Result.Converters.Add(new JsonStringEnumConverter());
        return Result;
    }
}

internal static class DataAssetSchema
{
    private static readonly ConcurrentDictionary<Type, DataAssetTypeDescriptor> Cache = new();

    public static DataAssetTypeDescriptor GetDescriptor(Type RuntimeType)
    {
        return Cache.GetOrAdd(RuntimeType, CreateDescriptor);
    }

    internal static JsonObject SchemaFor(Type Type)
    {
        if (Type == typeof(string))
        {
            return new JsonObject { ["type"] = "string" };
        }
        if (Type == typeof(bool))
        {
            return new JsonObject { ["type"] = "boolean" };
        }
        if (Type.IsEnum)
        {
            return new JsonObject { ["type"] = "string" };
        }
        if (Type == typeof(byte) || Type == typeof(short) || Type == typeof(int) ||
            Type == typeof(long) || Type == typeof(ushort) || Type == typeof(uint) ||
            Type == typeof(ulong))
        {
            return new JsonObject { ["type"] = "integer" };
        }
        if (Type == typeof(float) || Type == typeof(double) || Type == typeof(decimal))
        {
            return new JsonObject { ["type"] = "number" };
        }
        if (Type.IsGenericType && Type.GetGenericTypeDefinition() == typeof(AssetRef<>))
        {
            var AssetType = Type.GetGenericArguments()[0].Name.Replace(
                "Asset", string.Empty, StringComparison.Ordinal);
            return new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["x-sharpengine-reference"] = "asset",
                ["x-sharpengine-asset-type"] = AssetType,
            };
        }
        if (Type.IsArray)
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = SchemaFor(Type.GetElementType()!),
            };
        }

        return new JsonObject { ["type"] = "object" };
    }

    private static DataAssetTypeDescriptor CreateDescriptor(Type RuntimeType)
    {
        if (!typeof(DataAsset).IsAssignableFrom(RuntimeType))
            throw new InvalidDataException($"'{RuntimeType.FullName}' is not a data asset type");
        var Attribute = RuntimeType.GetCustomAttribute<DataAssetTypeAttribute>() ??
            throw new InvalidDataException(
                $"Data asset type '{RuntimeType.FullName}' requires DataAssetTypeAttribute");
        if (string.IsNullOrWhiteSpace(Attribute.TypeId) || Attribute.SchemaVersion < 1)
            throw new InvalidDataException(
                $"Data asset type '{RuntimeType.FullName}' has invalid type metadata");

        var Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var Properties = RuntimeType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(Property => Property.GetMethod is not null && Property.SetMethod is not null &&
                Property.GetIndexParameters().Length == 0 &&
                Property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(Property => new DataAssetPropertyDescriptor(
                Property.Name,
                Property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                    Options.PropertyNamingPolicy!.ConvertName(Property.Name),
                Property.PropertyType,
                Property,
                Property.GetCustomAttribute<DataPropertyAttribute>()))
            .OrderBy(Property => Property.JsonName, StringComparer.Ordinal)
            .ToArray();
        var Names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Property in Properties)
        {
            if (!Names.Add(Property.JsonName))
                throw new InvalidDataException(
                    $"Data asset type '{RuntimeType.FullName}' has duplicate JSON field '{Property.JsonName}'");
        }

        return new DataAssetTypeDescriptor(RuntimeType, Attribute, Properties);
    }
}

internal sealed class AssetRefJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type Type) => Type.IsGenericType &&
        Type.GetGenericTypeDefinition() == typeof(AssetRef<>);

    public override JsonConverter CreateConverter(Type Type, JsonSerializerOptions Options)
    {
        var ValueType = Type.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(
            typeof(AssetRefJsonConverter<>).MakeGenericType(ValueType))!;
    }
}

internal sealed class AssetRefJsonConverter<TAsset> : JsonConverter<AssetRef<TAsset>>
{
    public override AssetRef<TAsset> Read(ref Utf8JsonReader Reader,
        Type Type, JsonSerializerOptions Options)
    {
        return Reader.TokenType == JsonTokenType.Null
            ? new AssetRef<TAsset>(null)
            : new AssetRef<TAsset>(Reader.GetString());
    }

    public override void Write(Utf8JsonWriter Writer, AssetRef<TAsset> Value,
        JsonSerializerOptions Options)
    {
        Writer.WriteStringValue(Value.Id);
    }
}
