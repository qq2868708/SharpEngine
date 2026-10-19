using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Components;

namespace SharpEngine.Animation;

public sealed record AnimationParameterDefinition(string ParameterId, double DefaultValue);
public sealed record AnimationStateDefinition(string StateId, string ClipId, bool Loop, double Speed);
public sealed record AnimationCondition(string ParameterId, string Comparison, double Value);
public sealed record AnimationTransitionDefinition(string TransitionId, string FromStateId,
    string ToStateId, double? ExitTime, IReadOnlyList<AnimationCondition> Conditions);

/// <summary>Ordered, immutable animation decisions. Playables remain domain independent.</summary>
public sealed class AnimationStateMachineAsset
{
    public const int CurrentSchemaVersion = 1;
    private readonly JsonObject Document;

    public string AssetId
    {
        get;
    }
    public string InitialStateId
    {
        get;
    }
    public IReadOnlyList<AnimationParameterDefinition> Parameters
    {
        get;
    }
    public IReadOnlyList<AnimationStateDefinition> States
    {
        get;
    }
    public IReadOnlyList<AnimationTransitionDefinition> Transitions
    {
        get;
    }
    public IReadOnlyList<string> ClipIds
    {
        get
        {
            return States.Select(State => State.ClipId).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Id => Id, StringComparer.Ordinal).ToArray();
        }
    }

    private AnimationStateMachineAsset(JsonObject Document)
    {
        Exact(Document, "schemaVersion", "kind", "assetId", "name", "initialStateId", "parameters", "states", "transitions");
        if (Document["schemaVersion"]!.GetValue<int>() != CurrentSchemaVersion || Text(Document, "kind") != "animation-state-machine")
        {
            throw new InvalidDataException("Unsupported animation state machine schema.");
        }
        AssetId = Text(Document, "assetId");
        _ = Text(Document, "name");
        InitialStateId = Text(Document, "initialStateId");
        Parameters = Array.AsReadOnly(Document["parameters"]!.AsArray().Select(Node =>
        {
            var Item = Node!.AsObject();
            Exact(Item, "parameterId", "defaultValue");
            return new AnimationParameterDefinition(Text(Item, "parameterId"), Number(Item, "defaultValue"));
        }).ToArray());
        States = Array.AsReadOnly(Document["states"]!.AsArray().Select(Node =>
        {
            var Item = Node!.AsObject();
            Exact(Item, "stateId", "clipId", "loop", "speed");
            return new AnimationStateDefinition(Text(Item, "stateId"), Text(Item, "clipId"),
                Item["loop"]!.GetValue<bool>(), Number(Item, "speed"));
        }).ToArray());
        Transitions = Array.AsReadOnly(Document["transitions"]!.AsArray().Select(Node =>
        {
            var Item = Node!.AsObject();
            Exact(Item, "transitionId", "fromStateId", "toStateId", "exitTime", "conditions");
            var Conditions = Array.AsReadOnly(Item["conditions"]!.AsArray().Select(ConditionNode =>
            {
                var Condition = ConditionNode!.AsObject();
                Exact(Condition, "parameterId", "comparison", "value");
                return new AnimationCondition(Text(Condition, "parameterId"), Text(Condition, "comparison"), Number(Condition, "value"));
            }).ToArray());
            return new AnimationTransitionDefinition(Text(Item, "transitionId"), Text(Item, "fromStateId"),
                Text(Item, "toStateId"), Item["exitTime"] is null ? null : Number(Item, "exitTime"), Conditions);
        }).ToArray());
        Unique(Parameters.Select(Parameter => Parameter.ParameterId), "parameter");
        Unique(States.Select(State => State.StateId), "state");
        Unique(Transitions.Select(Transition => Transition.TransitionId), "transition");
        _ = State(InitialStateId);
        foreach (var State in States)
        {
            if (State.Speed <= 0)
            {
                throw new InvalidDataException("State speed must be positive.");
            }
        }
        var ParameterIds = Parameters.Select(Parameter => Parameter.ParameterId).ToHashSet(StringComparer.Ordinal);
        foreach (var Transition in Transitions)
        {
            _ = State(Transition.FromStateId);
            _ = State(Transition.ToStateId);
            if (Transition.ExitTime < 0 || Transition.ExitTime > 1 || Transition.FromStateId == Transition.ToStateId)
            {
                throw new InvalidDataException("Transitions require distinct states and an exitTime in [0, 1] or null.");
            }
            foreach (var Condition in Transition.Conditions)
            {
                if (!ParameterIds.Contains(Condition.ParameterId) || !Comparisons.Contains(Condition.Comparison))
                {
                    throw new InvalidDataException("Transition condition references an unknown parameter or comparison.");
                }
            }
        }
        this.Document = (JsonObject)Document.DeepClone();
    }

    private static readonly string[] Comparisons =
    [
        "Greater", "GreaterOrEqual", "Less", "LessOrEqual", "Equal", "NotEqual",
    ];

    public AnimationStateDefinition State(string StateId)
    {
        return States.SingleOrDefault(State => State.StateId == StateId) ??
            throw new InvalidDataException($"Unknown animation state '{StateId}'.");
    }

    public AnimationTransitionDefinition? SelectTransition(string StateId, double NormalizedTime,
        IReadOnlyDictionary<string, double> Values)
    {
        return Transitions.FirstOrDefault(Transition => Transition.FromStateId == StateId &&
            (Transition.ExitTime is null || NormalizedTime >= Transition.ExitTime.Value) &&
            Transition.Conditions.All(Condition => Compare(Values[Condition.ParameterId], Condition.Comparison, Condition.Value)));
    }

    internal Dictionary<string, double> ResolveParameters(IEnumerable<AnimatorParameter> Overrides)
    {
        var Values = Parameters.ToDictionary(Parameter => Parameter.ParameterId, Parameter => Parameter.DefaultValue, StringComparer.Ordinal);
        var Seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Parameter in Overrides)
        {
            if (Parameter is null || !Values.ContainsKey(Parameter.ParameterId) || !Seen.Add(Parameter.ParameterId) || !double.IsFinite(Parameter.Value))
            {
                throw new InvalidDataException("Animator parameters must be unique, finite and declared by the controller.");
            }
            Values[Parameter.ParameterId] = Parameter.Value;
        }
        return Values;
    }

    public JsonObject ToJson() { return (JsonObject)Document.DeepClone(); }
    public string Serialize(bool Indented = true) { return Document.ToJsonString(new JsonSerializerOptions { WriteIndented = Indented }); }

    public static AnimationStateMachineAsset Parse(string Json, string Source = "<memory>")
    {
        try
        {
            using var Parsed = JsonDocument.Parse(Json);
            RejectDuplicates(Parsed.RootElement);
            return new AnimationStateMachineAsset(JsonNode.Parse(Json)!.AsObject());
        }
        catch (Exception Error) when (Error is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException or NullReferenceException)
        {
            throw new InvalidDataException($"Invalid animation state machine '{Source}': {Error.Message}", Error);
        }
    }

    private static bool Compare(double Left, string Comparison, double Right)
    {
        return Comparison switch
        {
            "Greater" => Left > Right,
            "GreaterOrEqual" => Left >= Right,
            "Less" => Left < Right,
            "LessOrEqual" => Left <= Right,
            "Equal" => Left == Right,
            "NotEqual" => Left != Right,
            _ => throw new InvalidDataException("Unknown animation comparison."),
        };
    }

    private static void Exact(JsonObject Value, params string[] Fields)
    {
        if (Value.Count != Fields.Length || Fields.Any(Field => !Value.ContainsKey(Field)))
        {
            throw new InvalidDataException("Unexpected or missing fields in animation state machine.");
        }
    }

    private static string Text(JsonObject Value, string Field)
    {
        var Result = Value[Field]!.GetValue<string>();
        if (string.IsNullOrWhiteSpace(Result) || Result != Result.Trim())
        {
            throw new InvalidDataException($"'{Field}' must be nonempty text without surrounding whitespace.");
        }
        return Result;
    }

    private static double Number(JsonObject Value, string Field)
    {
        var Result = Value[Field]!.GetValue<double>();
        if (!double.IsFinite(Result))
        {
            throw new InvalidDataException($"'{Field}' must be finite.");
        }
        return Result;
    }

    private static void Unique(IEnumerable<string> Ids, string Kind)
    {
        var Seen = new HashSet<string>(StringComparer.Ordinal);
        if (Ids.Any(Id => !Seen.Add(Id)))
        {
            throw new InvalidDataException($"Duplicate animation {Kind} ID.");
        }
    }

    private static void RejectDuplicates(JsonElement Element)
    {
        if (Element.ValueKind == JsonValueKind.Object)
        {
            var Names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var Property in Element.EnumerateObject())
            {
                if (!Names.Add(Property.Name))
                {
                    throw new InvalidDataException($"Duplicate JSON field '{Property.Name}'.");
                }
                RejectDuplicates(Property.Value);
            }
        }
        else if (Element.ValueKind == JsonValueKind.Array)
        {
            foreach (var Item in Element.EnumerateArray())
            {
                RejectDuplicates(Item);
            }
        }
    }
}

public static class AnimationStateMachineContent
{
    public static AnimationStateMachineAsset Load(AssetRegistry Assets, string Id)
    {
        var Descriptor = Assets.Find(Id) ?? throw new KeyNotFoundException($"Unregistered animation state machine '{Id}'.");
        if (!string.Equals(Descriptor.Type, "AnimationStateMachine", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Asset '{Id}' must be an AnimationStateMachine.");
        }
        var Path = Assets.ResolvePath(Descriptor) ?? throw new InvalidDataException($"Unresolved animation state machine '{Id}'.");
        var Asset = AnimationStateMachineAsset.Parse(File.ReadAllText(Path), Path);
        if (!string.Equals(Asset.AssetId, Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Animation state machine asset ID disagrees with its manifest.");
        }
        return Asset;
    }

    /// <summary>Validate every state dependency, including inactive states, before Runtime or Cook.</summary>
    public static void ValidateDependencies(AssetRegistry Assets, string Id, string MeshId)
    {
        _ = AnimatorControllerBinding.Load(Assets, Id, MeshId);
    }
}

internal sealed record AnimatorControllerBinding(AnimationStateMachineAsset Asset,
    IReadOnlyDictionary<string, SkeletalAnimationAsset> Clips)
{
    internal static AnimatorControllerBinding Load(AssetRegistry Assets, string Id, string MeshId)
    {
        var Asset = AnimationStateMachineContent.Load(Assets, Id);
        // Stage the entire controller before making any of it live.
        var Clips = Asset.ClipIds.ToDictionary(ClipId => ClipId, ClipId => AnimatorContent.Resolve(Assets, ClipId, MeshId), StringComparer.OrdinalIgnoreCase);
        return new AnimatorControllerBinding(Asset, Clips);
    }
}
