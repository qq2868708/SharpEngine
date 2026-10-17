using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpEngine.Modules;

namespace SharpEngine.UI;

internal sealed class UiRuntimeService : IUiRuntime
{
    private readonly Dictionary<string, UiViewModelContract> _contracts =
        new(StringComparer.Ordinal);

    public IReadOnlyList<UiViewModelContract> ViewModelContracts
    {
        get
        {
            lock (_contracts)
                return _contracts.Values.OrderBy(value => value.Key,
                    StringComparer.Ordinal).ToArray();
        }
    }

    public IDisposable RegisterViewModel<T>(string id, int version) where T : class
    {
        var normalized = (id ?? string.Empty).Trim();
        if (!Regex.IsMatch(normalized, "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$",
                RegexOptions.CultureInvariant))
            throw new ArgumentException(
                "UI ViewModel IDs use lower-case dot or dash separated tokens", nameof(id));
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version),
            "UI ViewModel versions are positive integers");
        var contract = new UiViewModelContract(normalized, version,
            typeof(T).FullName ?? typeof(T).Name, BuildSchema(typeof(T)));
        lock (_contracts)
        {
            if (!_contracts.TryAdd(contract.Key, contract))
                throw new InvalidOperationException(
                    $"UI ViewModel contract '{contract.Key}' is already registered");
        }
        return ModuleRegistrationScope.Track(new Registration(() =>
        {
            lock (_contracts)
            {
                if (_contracts.TryGetValue(contract.Key, out var current) &&
                    ReferenceEquals(current, contract))
                    _contracts.Remove(contract.Key);
            }
        }));
    }

    private static string BuildSchema(Type type)
    {
        var schema = SchemaFor(type, new HashSet<Type>());
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["title"] = type.Name;
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject SchemaFor(Type source, HashSet<Type> visiting)
    {
        var type = Nullable.GetUnderlyingType(source) ?? source;
        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid))
            return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type.IsEnum)
            return new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(Enum.GetNames(type)
                    .Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            };
        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
            type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong))
            return new JsonObject { ["type"] = "integer" };
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return new JsonObject { ["type"] = "number" };
        var enumerable = type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);
        if (enumerable)
        {
            var itemType = type.IsArray ? type.GetElementType() :
                type.GetInterfaces().Append(type)
                    .FirstOrDefault(candidate => candidate.IsGenericType &&
                        candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    ?.GetGenericArguments()[0];
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = itemType is null
                    ? new JsonObject() : SchemaFor(itemType, visiting),
            };
        }
        if (!visiting.Add(type))
            throw new InvalidOperationException(
                $"UI ViewModel '{type.FullName}' contains a recursive property graph");
        var properties = new JsonObject();
        foreach (var property in type.GetProperties(BindingFlags.Instance |
                     BindingFlags.Public).Where(value => value.GetMethod?.IsPublic == true &&
                     value.GetIndexParameters().Length == 0).OrderBy(value => value.Name,
                     StringComparer.Ordinal))
            properties[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] =
                SchemaFor(property.PropertyType, visiting);
        visiting.Remove(type);
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
