using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using SharpEngine.Components;

namespace SharpEngine.Assets;

/// <summary>Uses the existing JSON contract; expanding a struct does not opt new members into persistence.</summary>
internal static class DataAssetStructs
{
    internal static IReadOnlyList<JsonPropertyInfo> Members(Type Type)
    {
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        if (!Type.IsValueType || Type.IsPrimitive || Type.IsEnum ||
            Type == typeof(Color) || Type == typeof(decimal) || Type == typeof(Guid) ||
            Type == typeof(DateTime) || Type == typeof(DateTimeOffset) ||
            Type == typeof(TimeSpan) ||
            Type.IsGenericType && Type.GetGenericTypeDefinition() == typeof(AssetRef<>))
        {
            return Array.Empty<JsonPropertyInfo>();
        }
        var Contract = DataAssetSerializer.ValueContract(Type);
        if (Contract.Kind != JsonTypeInfoKind.Object)
        {
            return Array.Empty<JsonPropertyInfo>();
        }
        return Contract.Properties.Where(Member => Member.Get is not null)
            .OrderBy(Member => Member.Order)
            .ThenBy(Member => Member.Name, StringComparer.Ordinal).ToArray();
    }

    internal static DataPropertyAttribute? Constraints(JsonPropertyInfo Member)
    {
        return Member.AttributeProvider?.GetCustomAttributes(typeof(DataPropertyAttribute), true)
            .Cast<DataPropertyAttribute>().SingleOrDefault();
    }

    internal static string DisplayName(JsonPropertyInfo Member)
    {
        return (Member.AttributeProvider as System.Reflection.MemberInfo)?.Name ?? Member.Name;
    }

    internal static JsonNode? Normalize(JsonNode? Value, Type Type)
    {
        if (Value is null)
        {
            return null;
        }
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        var Result = Value.DeepClone();
        var IsMap = DataAssetSchema.TryMapValue(Type, out var EntryType);
        var Map = Result as JsonObject;
        if (IsMap && Map is not null)
        {
            var Sorted = new JsonObject();
            foreach (var Entry in Map.OrderBy(Entry => Entry.Key, StringComparer.Ordinal))
            {
                Sorted[Entry.Key] = Normalize(Entry.Value, EntryType!);
            }
            return Sorted;
        }
        var ElementType = Type.IsArray ? Type.GetElementType() :
            Type.IsGenericType && Type.GetGenericTypeDefinition() == typeof(List<>)
                ? Type.GetGenericArguments()[0] : null;
        var Array = Result as JsonArray;
        if (ElementType is not null && Array is not null)
        {
            for (var Index = 0; Index < Array.Count; Index++)
            {
                Array[Index] = Normalize(Array[Index], ElementType);
            }
            return Array;
        }
        var Object = Result as JsonObject;
        if (Object is not null)
        {
            foreach (var Member in Members(Type))
            {
                if (Object.ContainsKey(Member.Name))
                {
                    Object[Member.Name] = Normalize(Object[Member.Name], Member.PropertyType);
                }
            }
        }
        return Result;
    }
}
