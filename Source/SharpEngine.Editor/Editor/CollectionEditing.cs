using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SharpEngine.Editor;

/// <summary>Serialized collection operations, shared by editor adapters. No UI or history ownership.</summary>
internal static class CollectionEditing
{
    internal static Type? ElementType(Type Type)
    {
        if (Type.IsArray && Type.GetArrayRank() == 1)
        {
            return Type.GetElementType();
        }
        if (Type.IsGenericType && Type.GetGenericTypeDefinition() == typeof(List<>))
        {
            return Type.GetGenericArguments()[0];
        }
        return null;
    }

    internal static string Fingerprint(JsonNode? Value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Value?.ToJsonString() ?? "null")));
    }

    internal static JsonArray Apply(JsonNode? Value, string Operation, Func<JsonNode?> Default)
    {
        if (Value is not null && Value is not JsonArray)
        {
            throw new InvalidDataException("COLLECTION_TYPE: an array or null is required");
        }
        var Items = Value as JsonArray ?? new JsonArray();
        if (Operation == "add")
        {
            Items.Add(Default());
            return Items;
        }
        var Parts = Operation.Split(',');
        var Index = Parts.Length == 2 ? int.Parse(Parts[1], CultureInfo.InvariantCulture) : -1;
        if (Index < 0 || Index >= Items.Count)
        {
            throw new InvalidDataException("COLLECTION_INDEX: item no longer exists");
        }
        if (Parts[0] == "remove")
        {
            Items.RemoveAt(Index);
        }
        else if (Parts[0] is "up" or "down")
        {
            var Target = Index + (Parts[0] == "up" ? -1 : 1);
            if (Target < 0 || Target >= Items.Count)
            {
                throw new InvalidDataException("COLLECTION_INDEX: move is outside the collection");
            }
            var Item = Items[Index];
            Items.RemoveAt(Index);
            Items.Insert(Target, Item);
        }
        else
        {
            throw new InvalidDataException("COLLECTION_OPERATION: unsupported operation");
        }
        return Items;
    }
}
