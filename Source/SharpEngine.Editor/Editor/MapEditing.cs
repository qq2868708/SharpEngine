using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.UI;

namespace SharpEngine.Editor;

internal static class MapEditing
{
    internal sealed class ViewState
    {
        internal List<string> Order = new();
        internal string Fingerprint = string.Empty;
        internal bool Expanded = true;
    }

    internal static Type? ValueType(Type Type)
    {
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        if (!typeof(IDictionary).IsAssignableFrom(Type) || !Type.IsGenericType)
        {
            return null;
        }
        var Arguments = Type.GetGenericArguments();
        if (Arguments.Length != 2 || Arguments[0] != typeof(string))
        {
            return null;
        }
        return Arguments[1];
    }

    internal static List<string> ReconcileOrder(JsonObject? Map, IReadOnlyList<string>? Existing)
    {
        var Keys = Map?.Select(Property => Property.Key).ToHashSet(StringComparer.Ordinal) ??
            new HashSet<string>(StringComparer.Ordinal);
        var Result = Existing?.Where(Keys.Contains).Distinct(StringComparer.Ordinal).ToList() ??
            new List<string>();
        Result.AddRange(Keys.Except(Result, StringComparer.Ordinal)
            .OrderBy(Key => Key, StringComparer.Ordinal));
        return Result;
    }

    internal static void Rename(JsonObject Map, string OldKey, string NewKey)
    {
        if (!Map.ContainsKey(OldKey))
        {
            throw new InvalidDataException("MAP_KEY: entry no longer exists");
        }
        if (OldKey != NewKey && Map.ContainsKey(NewKey))
        {
            throw new InvalidDataException("MAP_KEY_DUPLICATE: another entry already uses this key");
        }
        if (OldKey == NewKey)
        {
            return;
        }
        var Entries = Map.Select(Property => (Property.Key, Value: Property.Value?.DeepClone())).ToArray();
        Map.Clear();
        foreach (var Entry in Entries)
        {
            Map[Entry.Key == OldKey ? NewKey : Entry.Key] = Entry.Value;
        }
    }

    internal static string ViewKey(string AssetId, string[] Path)
    {
        return AssetId + "|" + JsonSerializer.Serialize(Path);
    }

    internal static string UniqueKey(JsonObject Map)
    {
        return UniqueKey(Map, "New Key");
    }

    internal static string UniqueKey(JsonObject Map, string Preferred)
    {
        if (!Map.ContainsKey(Preferred))
        {
            return Preferred;
        }
        for (var Index = 2; ; Index++)
        {
            var Candidate = Preferred + " " + Index;
            if (!Map.ContainsKey(Candidate))
            {
                return Candidate;
            }
        }
    }

    internal static string Fingerprint(JsonNode? Map)
    {
        var Canonical = Canonicalize(Map);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Canonical?.ToJsonString() ?? "null")));
    }

    internal static JsonObject CanonicalizeDataAssetMaps(JsonObject Root,
        IReadOnlyList<DataAssetPropertyDescriptor> Properties)
    {
        var Result = Root.DeepClone().AsObject();
        foreach (var Property in Properties)
        {
            if (ValueType(Property.PropertyType) is null ||
                !Result.TryGetPropertyValue(Property.JsonName, out var Value))
            {
                continue;
            }
            Result[Property.JsonName] = Canonicalize(Value);
        }
        return Result;
    }

    internal static JsonNode? GetAt(JsonNode? Root, IEnumerable<string> Path)
    {
        var Current = Root;
        foreach (var Segment in Path)
        {
            Current = Current is JsonArray Array
                ? Array[int.Parse(Segment, CultureInfo.InvariantCulture)]
                : Current?[Segment];
        }
        return Current;
    }

    internal static void SetAt(ref JsonNode? Root, IReadOnlyList<string> Path, JsonNode? Value)
    {
        if (Path.Count == 0)
        {
            Root = Value?.DeepClone();
            return;
        }
        var Parent = GetAt(Root, Path.Take(Path.Count - 1));
        var Last = Path[^1];
        if (Parent is JsonObject Object)
        {
            Object[Last] = Value?.DeepClone();
            return;
        }
        if (Parent is JsonArray Array)
        {
            Array[int.Parse(Last, CultureInfo.InvariantCulture)] = Value?.DeepClone();
            return;
        }
        throw new InvalidDataException("MAP_PATH: parent is not a JSON container");
    }

    internal static void SetValue(JsonObject Map, string[] Path, string Operation,
        string Text, Type MapValueType, Func<Type, string, Type> ResolveMemberType)
    {
        MapValueType = Nullable.GetUnderlyingType(MapValueType) ?? MapValueType;
        if (Path.Length == 0 || !Map.ContainsKey(Path[0]))
        {
            throw new InvalidDataException("MAP_PATH: value path is missing");
        }

        var ResolvedType = MapValueType;
        foreach (var Segment in Path.Skip(1))
        {
            var NestedMapValue = ValueType(ResolvedType);
            if (NestedMapValue is not null)
            {
                ResolvedType = NestedMapValue;
                continue;
            }
            var ElementType = CollectionEditing.ElementType(ResolvedType);
            if (ElementType is not null)
            {
                _ = int.Parse(Segment, CultureInfo.InvariantCulture);
                ResolvedType = ElementType;
                continue;
            }
            ResolvedType = ResolveMemberType(ResolvedType, Segment);
        }
        ResolvedType = Nullable.GetUnderlyingType(ResolvedType) ?? ResolvedType;

        var Current = GetAt(Map, Path);
        if (Operation.StartsWith("axis,", StringComparison.Ordinal))
        {
            SetAxis(Current, ResolvedType, Operation, Text);
            return;
        }
        if (Operation == "color")
        {
            SetColor(Map, Path, Current, ResolvedType, Text);
            return;
        }

        JsonNode? Root = Map;
        SetAt(ref Root, Path, ParseValue(ResolvedType, Text));
    }

    internal static double[]? ReadAxes(Type Type, JsonNode? Value)
    {
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        var Count = AxisCount(Type);
        if (Count == 0)
        {
            return null;
        }
        if (Value is JsonArray Array && Array.Count == Count &&
            Array.All(Node => Node is JsonValue))
        {
            return Array.Select(Node => Node!.Deserialize<double>()).ToArray();
        }
        var Names = AxisNames(Type);
        var Object = Value as JsonObject;
        if (Object is not null && Enumerable.Range(0, Count)
            .All(Index => FindAxis(Object, Names[Index]) is JsonValue))
        {
            return Enumerable.Range(0, Count)
                .Select(Index => FindAxis(Object, Names[Index])!.Deserialize<double>()).ToArray();
        }
        return null;
    }

    internal static string ValueActionMarkup(string Label, string Id, Type Type, JsonNode? Value,
        string Action, Func<int, string> AxisAction, bool ReadOnly,
        IEnumerable<(string Id, string Label)>? ReferenceOptions = null)
    {
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        if (CollectionEditing.ElementType(Type) is not null)
        {
            return UiValueEditors.Multiline(Label, new UiValueInput(Id,
                Value?.ToJsonString() ?? "null", string.Empty, true), 3,
                "Nested collection Map values are read-only");
        }
        if (Type == typeof(bool))
        {
            var Checked = Value?.GetValue<bool>() == true;
            return UiValueEditors.Boolean(Label, Id, Checked,
                Action + (!Checked ? "true" : "false"), ReadOnly);
        }
        if (Type.IsEnum)
        {
            return UiValueEditors.Choice(Label, Id,
                Value?.GetValue<string>() ?? string.Empty,
                Enum.GetNames(Type).Select(Name => (Name, Name)), Action, ReadOnly);
        }
        if (ValueType(Type) is not null)
        {
            return UiValueEditors.Multiline(Label, new UiValueInput(Id,
                Value?.ToJsonString() ?? "{}", string.Empty, true), 4,
                "Nested Map values are rendered as child maps");
        }
        var IsAssetRef = Type.IsGenericType && Type.GetGenericTypeDefinition() == typeof(AssetRef<>);
        var Axes = ReadAxes(Type, Value);
        if (Axes is not null)
        {
            var Inputs = Axes.Select((Axis, Index) => new UiValueInput(Id + "-" + Index,
                UiValueEditors.Format(Axis), AxisAction(Index), ReadOnly, "number")).ToArray();
            return Type == typeof(Color)
                ? UiValueEditors.Color(Label, Inputs, ReadOnly ? string.Empty : Action)
                : UiValueEditors.Vector(Label, Inputs, Type == typeof(System.Numerics.Vector2)
                    ? "XY" : Type == typeof(System.Numerics.Vector3) ? "XYZ" : "XYZW");
        }
        if (IsAssetRef)
        {
            return UiValueEditors.Reference(Label, Id,
                Value?.GetValue<string>() ?? string.Empty,
                ReferenceOptions ?? [(string.Empty, "None")], Action, ReadOnly);
        }
        var Kind = Type == typeof(string) || Type == typeof(Guid) || Type == typeof(Uri)
            ? "text" : Type.IsPrimitive || Type == typeof(decimal) ? "number" : "json";
        return UiValueEditors.Text(Label, new UiValueInput(Id,
            Type == typeof(string) || Type == typeof(Guid) || Type == typeof(Uri)
                ? Value?.GetValue<string>() ?? string.Empty
                : Value?.ToJsonString() ?? "null",
            Action, ReadOnly, Kind));
    }

    internal static string KeyInput(string Id, string Key, string Action, bool ReadOnly)
    {
        return UiValueEditors.Input(new UiValueInput(Id, Key, Action, ReadOnly, "text"));
    }

    private static JsonNode? Canonicalize(JsonNode? Value)
    {
        if (Value is JsonObject Object)
        {
            var Result = new JsonObject();
            foreach (var Property in Object.OrderBy(Property => Property.Key,
                         StringComparer.Ordinal))
            {
                Result[Property.Key] = Canonicalize(Property.Value);
            }
            return Result;
        }
        if (Value is JsonArray Array)
        {
            return new JsonArray(Array.Select(Canonicalize).ToArray());
        }
        return Value?.DeepClone();
    }

    private static JsonNode? ParseValue(Type Type, string Text)
    {
        Type = Nullable.GetUnderlyingType(Type) ?? Type;
        if (Type == typeof(string) || Type == typeof(Guid) || Type == typeof(Uri) ||
            Type.IsEnum || Type.IsGenericType && Type.GetGenericTypeDefinition() == typeof(AssetRef<>))
        {
            return Type.IsGenericType && Type.GetGenericTypeDefinition() == typeof(AssetRef<>) &&
                Text.Length == 0 ? null : JsonValue.Create(Text);
        }
        if (Type == typeof(bool))
        {
            return JsonValue.Create(bool.Parse(Text));
        }
        return JsonNode.Parse(Text);
    }

    private static void SetAxis(JsonNode? Value, Type Type, string Operation, string Text)
    {
        var Axis = int.Parse(Operation[5..], CultureInfo.InvariantCulture);
        var Axes = ReadAxes(Type, Value) ??
            throw new InvalidDataException("MAP_AXIS: target is not a vector");
        if (Axis < 0 || Axis >= Axes.Length)
        {
            throw new InvalidDataException("MAP_AXIS: axis is outside the value dimension");
        }
        var Number = float.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!float.IsFinite(Number))
        {
            throw new InvalidDataException("MAP_AXIS: value must be finite");
        }
        if (Value is JsonArray Array)
        {
            Array[Axis] = Number;
            return;
        }
        if (Value is JsonObject Object)
        {
            Object[AxisPropertyName(Object, Type, Axis)] = Number;
            return;
        }
        throw new InvalidDataException("MAP_AXIS: target is not a vector");
    }

    private static void SetColor(JsonObject Map, string[] Path, JsonNode? Current,
        Type Type, string Text)
    {
        if (Type != typeof(Color))
        {
            throw new InvalidDataException("MAP_COLOR: target is not a Color");
        }
        var Channels = JsonNode.Parse(Text) as JsonArray;
        if (Channels is null || Channels.Count != 4 || Channels.Any(Channel =>
                Channel is null || !float.IsFinite(Channel.Deserialize<float>())))
        {
            throw new InvalidDataException("MAP_COLOR: expected four finite RGBA channels");
        }
        if (Current is JsonObject Object)
        {
            for (var Index = 0; Index < 4; Index++)
            {
                Object[AxisPropertyName(Object, typeof(Color), Index)] = Channels[Index]!.DeepClone();
            }
            return;
        }
        JsonNode? Root = Map;
        SetAt(ref Root, Path, Channels);
    }

    private static int AxisCount(Type Type)
    {
        if (Type == typeof(System.Numerics.Vector2))
        {
            return 2;
        }
        if (Type == typeof(System.Numerics.Vector3))
        {
            return 3;
        }
        if (Type == typeof(System.Numerics.Vector4) || Type == typeof(System.Numerics.Quaternion) ||
            Type == typeof(Color))
        {
            return 4;
        }
        return 0;
    }

    private static string[] AxisNames(Type Type)
    {
        return Type == typeof(Color) ? ["r", "g", "b", "a"] : ["x", "y", "z", "w"];
    }

    private static string AxisName(Type Type, int Index)
    {
        return AxisNames(Type)[Index];
    }

    private static string AxisPropertyName(JsonObject Object, Type Type, int Index)
    {
        var Name = AxisName(Type, Index);
        if (Object.ContainsKey(Name))
        {
            return Name;
        }
        var UpperName = Name.ToUpperInvariant();
        if (Object.ContainsKey(UpperName))
        {
            return UpperName;
        }
        return Name;
    }

    private static JsonNode? FindAxis(JsonObject Object, string Name)
    {
        if (Object.TryGetPropertyValue(Name, out var Value))
        {
            return Value;
        }
        Object.TryGetPropertyValue(Name.ToUpperInvariant(), out Value);
        return Value;
    }

}
