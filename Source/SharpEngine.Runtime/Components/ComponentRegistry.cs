using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEngine.Components;

public sealed record ComponentField(string Name, Type ValueType,
    Func<object, object?> Read, Action<object, object?> Write,
    string? DisplayName = null, string? Tooltip = null, string? Header = null,
    double? Minimum = null, double? Maximum = null, bool Hidden = false, int Lines = 0);

public sealed class ComponentDescriptor(string typeId, int schemaVersion, Type runtimeType,
    string menuPath, bool single, Type[] requires, ComponentField[] fields,
    Func<SharpComponent> factory, Func<ComponentRegistry, JsonObject> defaults,
    Action<JsonObject, ComponentDiagnosticSink>? validate = null,
    Func<int, JsonObject, IReadOnlyList<ComponentRecord>, JsonObject>? Migrate = null)
{
    public string TypeId { get; } = typeId;
    public int SchemaVersion { get; } = schemaVersion;
    public Type RuntimeType { get; } = runtimeType;
    public string DisplayName => RuntimeType.Name;
    public string MenuPath { get; } = menuPath;
    public bool Single { get; } = single;
    public IReadOnlyList<Type> Requires { get; } = Array.AsReadOnly(requires);
    public IReadOnlyList<ComponentField> Fields { get; } = Array.AsReadOnly(fields);
    public string ModuleId { get; internal set; } = "";
    public string GenerationId { get; internal set; } = "";
    internal Func<SharpComponent> Factory { get; } = factory;
    internal Func<ComponentRegistry, JsonObject> DefaultFactory { get; } = defaults;
    internal Action<JsonObject, ComponentDiagnosticSink>? Validator { get; } = validate;
    internal Func<int, JsonObject, IReadOnlyList<ComponentRecord>, JsonObject>? Migration
    {
        get;
    } = Migrate;
    public JsonObject CreateDefaultState(ComponentRegistry registry) => DefaultFactory(registry);
    public JsonObject Schema(ComponentRegistry registry)
    {
        var properties = new JsonObject();
        var definitions = new JsonObject();
        foreach (var field in Fields)
        {
            var schema = ComponentValue.Schema(field.ValueType, registry);
            if (schema["$defs"] is JsonObject nestedDefinitions)
            {
                foreach (var (key, value) in nestedDefinitions) definitions[key] = value?.DeepClone();
                schema.Remove("$defs");
            }
            if (field.Minimum is { } min) schema["minimum"] = min;
            if (field.Maximum is { } max) schema["maximum"] = max;
            if (field.Tooltip is { } tooltip) schema["description"] = tooltip;
            schema["x-sharpengine-hidden"] = field.Hidden;
            properties[field.Name] = schema;
        }
        return new() { ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["$defs"] = definitions,
            ["required"] = new JsonArray(Fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()) };
    }
}

public sealed record DataDescriptor(Type Type, Func<object> Factory, ComponentField[] Fields,
    Func<ComponentRegistry, JsonObject> Defaults);

public sealed class ComponentRegistry
{
    internal void MigrateScene(SceneData Scene)
    {
        foreach (var Entity in Scene.Entities)
        {
            foreach (var Component in Entity.Components)
            {
                var Descriptor = Find(Component.TypeId);
                if (Descriptor?.Migration is not null && Component.SchemaVersion < Descriptor.SchemaVersion)
                {
                    Component.State = Descriptor.Migration(Component.SchemaVersion, (JsonObject)Component.State.DeepClone(), Entity.Components.AsReadOnly());
                    Component.SchemaVersion = Descriptor.SchemaVersion;
                }
            }
        }
    }
    public Func<string, Type, bool>? AssetExists { get; set; }
    private readonly Dictionary<string, ComponentDescriptor> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, DataDescriptor> _data = [];
    public IReadOnlyCollection<ComponentDescriptor> Types => _types.Values.ToArray();
    public ComponentDescriptor? Find(string typeId) => _types.GetValueOrDefault(typeId);
    public ComponentDescriptor Get(Type type) => _types.Values.SingleOrDefault(d => d.RuntimeType == type)
        ?? throw new InvalidOperationException($"Unregistered component {type.FullName}");
    public DataDescriptor Data(Type type) => _data.GetValueOrDefault(type)
        ?? throw new InvalidOperationException($"Unregistered serializable data {type.FullName}");
    public void RegisterData(DataDescriptor descriptor) => _data.TryAdd(descriptor.Type, descriptor);
    public void Register(ComponentDescriptor descriptor, string moduleId, string generationId)
    {
        if (descriptor.SchemaVersion < 1 || string.IsNullOrWhiteSpace(descriptor.TypeId) ||
            descriptor.Fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != descriptor.Fields.Count)
            throw new InvalidDataException("Invalid component descriptor");
        if (_types.Values.Any(d => d.RuntimeType == descriptor.RuntimeType) || !_types.TryAdd(descriptor.TypeId, descriptor))
            throw new InvalidDataException($"Duplicate component type {descriptor.TypeId}");
        descriptor.ModuleId = moduleId; descriptor.GenerationId = generationId;
    }
    public void RegisterAssembly(Assembly assembly, string moduleId, string generationId)
    {
        foreach (var catalog in assembly.GetCustomAttributes<ComponentCatalogAttribute>())
            ((IComponentCatalog)Activator.CreateInstance(catalog.Provider)!).Register(this, moduleId, generationId);
    }
    public static ComponentRegistry CreateBuiltin()
    {
        var registry = new ComponentRegistry();
        registry.RegisterAssembly(typeof(SharpComponent).Assembly, "engine", "builtin");
        registry.ValidateRegistrations();
        return registry;
    }
    public void ValidateRegistrations()
    {
        foreach (var type in Types)
        {
            Visit(type, []);
            ValidateState(type, type.CreateDefaultState(this));
        }
        void Visit(ComponentDescriptor type, HashSet<string> visiting)
        {
            if (!visiting.Add(type.TypeId)) throw new InvalidDataException($"Component dependency cycle at {type.TypeId}");
            foreach (var dependency in type.Requires) Visit(Get(dependency), visiting);
            visiting.Remove(type.TypeId);
        }
    }
    public IReadOnlyList<ComponentDescriptor> Addition(string typeId, IEnumerable<string> existing)
    {
        var known = existing.ToHashSet(StringComparer.Ordinal);
        var result = new List<ComponentDescriptor>();
        var requested = Find(typeId) ?? throw new InvalidDataException($"Unknown component {typeId}");
        Add(requested, true, []);
        return result;
        void Add(ComponentDescriptor descriptor, bool explicitAdd, HashSet<string> visiting)
        {
            if (!visiting.Add(descriptor.TypeId)) throw new InvalidDataException("Component dependency cycle");
            if (known.Contains(descriptor.TypeId))
            {
                if (!explicitAdd) { visiting.Remove(descriptor.TypeId); return; }
                if (descriptor.Single) throw new InvalidOperationException($"Only one {descriptor.DisplayName} is allowed");
            }
            foreach (var dependency in descriptor.Requires) Add(Get(dependency), false, visiting);
            visiting.Remove(descriptor.TypeId);
            result.Add(descriptor); known.Add(descriptor.TypeId);
        }
    }
    public void ValidateState(ComponentDescriptor descriptor, JsonObject state, Func<string, Type, bool>? reference = null)
    {
        ValidateFields(descriptor.Fields, state, reference);
        if (descriptor.TypeId == "engine.transform")
        {
            var scale = (Vector3)ComponentValue.Decode(state["Scale"], typeof(Vector3), this)!;
            if (scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) throw new InvalidDataException("Scale must be positive");
        }
        if (descriptor.TypeId == "engine.camera" &&
            (state["NearPlane"]!.Deserialize<float>() <= 0 || state["FarPlane"]!.Deserialize<float>() <= state["NearPlane"]!.Deserialize<float>()))
            throw new InvalidDataException("Camera requires 0 < NearPlane < FarPlane");
        if (descriptor.TypeId == "engine.directional-light" && state["Intensity"]!.Deserialize<float>() < 0)
            throw new InvalidDataException("Light intensity must be nonnegative");
        var diagnostics = new ComponentDiagnosticSink();
        descriptor.Validator?.Invoke((JsonObject)state.DeepClone(), diagnostics);
        if (diagnostics.Items.Count != 0) throw new ComponentValidationException(diagnostics.Items[0].Code,
            diagnostics.Items[0].FieldPath, diagnostics.Items[0].Message);
    }
    internal void ValidateFields(IReadOnlyList<ComponentField> fields, JsonObject state, Func<string, Type, bool>? reference)
    {
        if (state.Count != fields.Count || fields.Any(f => !state.ContainsKey(f.Name)))
            throw new InvalidDataException("Component state must contain exactly the declared fields");
        foreach (var field in fields)
        {
            ComponentValue.Validate(state[field.Name], field.ValueType, this, reference);
            if ((field.Minimum is not null || field.Maximum is not null) && state[field.Name] is { } value)
            {
                var number = double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture);
                if (number < field.Minimum || number > field.Maximum) throw new ComponentValidationException("FIELD_RANGE", field.Name, "Value is outside the declared range");
            }
        }
    }
    public JsonObject Read(SharpComponent component) => ReadFields(component.Descriptor.Fields, component);
    internal JsonObject ReadFields(IReadOnlyList<ComponentField> fields, object instance, HashSet<object>? stack = null)
    {
        var state = new JsonObject();
        foreach (var field in fields) state[field.Name] = ComponentValue.Encode(field.Read(instance), field.ValueType, this, stack);
        return state;
    }
    internal void Write(SharpComponent component, JsonObject state, RuntimeWorld world)
    {
        foreach (var field in component.Descriptor.Fields)
            field.Write(component, ComponentValue.Decode(state[field.Name], field.ValueType, this, world));
    }
}

public static class ComponentValue
{
    public static bool IsReference(Type type) => type == typeof(SharpEntity) || typeof(SharpComponent).IsAssignableFrom(type);
    private static bool Asset(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AssetRef<>);
    private static Type? Element(Type type) => type.IsArray ? type.GetElementType() :
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0] : null;
    private static int VectorSize(Type type) => type == typeof(Vector2) ? 2 : type == typeof(Vector3) ? 3 :
        type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color) ? 4 : 0;
    public static JsonNode? Encode(object? value, Type type, ComponentRegistry registry, HashSet<object>? stack = null)
    {
        if (value is null) return null;
        if (value is SharpComponent component) return component.IsAlive ? JsonValue.Create(component.ComponentId) : null;
        if (value is SharpEntity entity) return entity.IsAlive ? JsonValue.Create(entity.EntityId) : null;
        if (value is IAssetReference asset) return JsonValue.Create(asset.Id);
        if (type.IsEnum) return JsonValue.Create(value.ToString());
        float[]? vector = value switch
        {
            Vector2 v => [v.X, v.Y], Vector3 v => [v.X, v.Y, v.Z], Vector4 v => [v.X, v.Y, v.Z, v.W],
            Quaternion v => [v.X, v.Y, v.Z, v.W], Color v => [v.R, v.G, v.B, v.A], _ => null,
        };
        if (vector is not null) return new JsonArray(vector.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal)) return JsonSerializer.SerializeToNode(value, type);
        stack ??= new(ReferenceEqualityComparer.Instance);
        if (!stack.Add(value)) throw new InvalidDataException("Cyclic serialized object graph");
        try
        {
            if (Element(type) is { } element)
            {
                var array = new JsonArray();
                foreach (var item in (IEnumerable)value) array.Add(Encode(item, element, registry, stack));
                return array;
            }
            return registry.ReadFields(registry.Data(type).Fields, value, stack);
        }
        finally { stack.Remove(value); }
    }
    public static object? Decode(JsonNode? node, Type type, ComponentRegistry registry, RuntimeWorld? world = null)
    {
        if (Asset(type)) return Activator.CreateInstance(type, new object?[] { node?.GetValue<string>() });
        if (node is null)
        {
            if (type.IsValueType) throw new InvalidDataException($"Null is not valid for {type.Name}");
            return null;
        }
        if (IsReference(type))
        {
            var id = node.GetValue<string>();
            if (world is null) return null;
            object? target = type == typeof(SharpEntity) ? world.FindEntity(id) : world.FindComponent(id);
            if (target is null || !type.IsInstanceOfType(target)) throw new InvalidDataException($"Unresolved {type.Name} reference {id}");
            return target;
        }
        if (type.IsEnum)
        {
            var text = node.GetValue<string>();
            if (!Enum.TryParse(type, text, false, out var result) || !Enum.IsDefined(type, result!)) throw new InvalidDataException($"Invalid {type.Name} value {text}");
            return result;
        }
        if (VectorSize(type) is var count && count > 0)
        {
            if (node is not JsonArray array || array.Count != count) throw new InvalidDataException($"Expected {count} vector values");
            var v = array.Select(n => n!.Deserialize<float>()).ToArray();
            if (v.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Vector must be finite");
            if (type == typeof(Vector2)) return new Vector2(v[0], v[1]);
            if (type == typeof(Vector3)) return new Vector3(v[0], v[1], v[2]);
            if (type == typeof(Vector4)) return new Vector4(v[0], v[1], v[2], v[3]);
            if (type == typeof(Quaternion)) return new Quaternion(v[0], v[1], v[2], v[3]);
            return new Color(v[0], v[1], v[2], v[3]);
        }
        if (Element(type) is { } element)
        {
            var array = node as JsonArray ?? throw new InvalidDataException("Expected array");
            var values = array.Select(n => Decode(n, element, registry, world)).ToArray();
            if (type.IsArray) { var result = Array.CreateInstance(element, values.Length); for (int i = 0; i < values.Length; i++) result.SetValue(values[i], i); return result; }
            var list = (IList)Activator.CreateInstance(type)!;
            foreach (var item in values) list.Add(item);
            return list;
        }
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
        {
            var value = node.Deserialize(type);
            if (value is float f && !float.IsFinite(f) || value is double d && !double.IsFinite(d)) throw new InvalidDataException("Number must be finite");
            return value;
        }
        var data = registry.Data(type);
        var obj = node as JsonObject ?? throw new InvalidDataException("Expected data object");
        registry.ValidateFields(data.Fields, obj, null);
        var instance = data.Factory();
        foreach (var field in data.Fields) field.Write(instance, Decode(obj[field.Name], field.ValueType, registry, world));
        return instance;
    }
    public static void Validate(JsonNode? value, Type type, ComponentRegistry registry, Func<string, Type, bool>? reference)
    {
        if (Asset(type))
        {
            if (value is null) return;
            var id = value.GetValue<string>();
            if (!(id.StartsWith("engine://", StringComparison.Ordinal) || id.StartsWith("project://", StringComparison.Ordinal)) ||
                registry.AssetExists?.Invoke(id, type.GetGenericArguments()[0]) == false)
                throw new InvalidDataException($"Invalid or unavailable asset reference {id}");
            return;
        }
        if (IsReference(type))
        {
            if (value is not null && reference is not null && !reference(value.GetValue<string>(), type))
                throw new InvalidDataException($"Invalid {type.Name} reference {value}");
            if (value is not null) _ = value.GetValue<string>();
            return;
        }
        if (Element(type) is { } element && value is JsonArray array)
        { foreach (var item in array) Validate(item, element, registry, reference); return; }
        if (value is JsonObject obj && !type.IsPrimitive && VectorSize(type) == 0)
        { registry.ValidateFields(registry.Data(type).Fields, obj, reference); return; }
        _ = Decode(value, type, registry);
    }
    public static JsonNode? Remap(JsonNode? value, Type type, ComponentRegistry registry, Func<string, string?> map)
    {
        if (value is null) return null;
        if (IsReference(type)) return JsonValue.Create(map(value.GetValue<string>()));
        if (Element(type) is { } element && value is JsonArray array)
            return new JsonArray(array.Select(item => Remap(item, element, registry, map)).ToArray());
        if (value is JsonObject obj && !type.IsPrimitive && VectorSize(type) == 0)
        {
            var result = new JsonObject();
            foreach (var field in registry.Data(type).Fields) result[field.Name] = Remap(obj[field.Name], field.ValueType, registry, map);
            return result;
        }
        return value.DeepClone();
    }
    public static JsonObject Schema(Type type, ComponentRegistry registry)
    {
        var definitions = new JsonObject();
        var schema = BuildSchema(type, registry, definitions);
        if (definitions.Count != 0) schema["$defs"] = definitions;
        return schema;
    }
    private static JsonObject BuildSchema(Type type, ComponentRegistry registry, JsonObject definitions)
    {
        if (IsReference(type) || Asset(type))
        {
            var reference = new JsonObject { ["type"] = new JsonArray("string", "null"), ["x-sharpengine-reference"] = Asset(type) ? "asset" : type == typeof(SharpEntity) ? "entity" : "component" };
            if (Asset(type)) reference["x-sharpengine-asset-type"] = type.GetGenericArguments()[0].Name.Replace("Asset", "");
            if (typeof(SharpComponent).IsAssignableFrom(type)) reference["x-sharpengine-component-types"] = new JsonArray(registry.Types.Where(d => type.IsAssignableFrom(d.RuntimeType)).OrderBy(d => d.TypeId).Select(d => (JsonNode?)JsonValue.Create(d.TypeId)).ToArray());
            return reference;
        }
        if (type.IsEnum) return new() { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(type).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) };
        if (type == typeof(bool)) return new() { ["type"] = "boolean" };
        if (type == typeof(string)) return new() { ["type"] = new JsonArray("string", "null") };
        if (VectorSize(type) is var count && count > 0) return new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["minItems"] = count, ["maxItems"] = count, ["x-sharpengine-control"] = type.Name };
        if (type.IsPrimitive || type == typeof(decimal)) return new() { ["type"] = type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? "number" : "integer" };
        if (Element(type) is { } element) return new() { ["type"] = new JsonArray("array", "null"), ["items"] = BuildSchema(element, registry, definitions) };
        var key = type.FullName!;
        if (!definitions.ContainsKey(key))
        {
            definitions[key] = new JsonObject();
            var properties = new JsonObject();
            var data = registry.Data(type);
            foreach (var field in data.Fields) properties[field.Name] = BuildSchema(field.ValueType, registry, definitions);
            definitions[key] = new JsonObject { ["type"] = type.IsValueType ? JsonValue.Create("object") : new JsonArray("object", "null"), ["properties"] = properties,
                ["additionalProperties"] = false, ["required"] = new JsonArray(data.Fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()) };
        }
        return new() { ["$ref"] = "#/$defs/" + key };
    }
}
