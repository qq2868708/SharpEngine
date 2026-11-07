using System.Globalization;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor;

namespace SharpEngine.Animation;

internal sealed class AnimationStateMachineAuthoringDocument
{
    private readonly JsonAssetAuthoringDocument Authoring;
    private readonly AssetRegistry Assets;
    private readonly string ExpectedAssetId;
    private AnimationStateMachineAsset? CachedStateMachine;
    private ulong CachedRevision;

    public AnimationStateMachineAuthoringDocument(AssetRegistry Assets, AssetDescriptor Asset, string Path)
    {
        this.Assets = Assets;
        ExpectedAssetId = Asset.Id;
        Authoring = new JsonAssetAuthoringDocument(Asset, Path, Validate);
    }

    public AnimationStateMachineAsset StateMachine
    {
        get
        {
            if (CachedStateMachine is null || CachedRevision != Authoring.Revision)
            {
                CachedStateMachine = AnimationStateMachineAsset.Parse(Authoring.Source);
                CachedRevision = Authoring.Revision;
            }
            return CachedStateMachine;
        }
    }
    public JsonObject Snapshot
    {
        get
        {
            return Authoring.Snapshot;
        }
    }
    public string Source
    {
        get
        {
            return Authoring.Source;
        }
    }
    public bool IsReadOnly
    {
        get
        {
            return Authoring.IsReadOnly;
        }
    }
    public bool IsDirty
    {
        get
        {
            return Authoring.IsDirty;
        }
    }
    public bool CanUndo
    {
        get
        {
            return !IsReadOnly && Authoring.CanUndo;
        }
    }
    public bool CanRedo
    {
        get
        {
            return !IsReadOnly && Authoring.CanRedo;
        }
    }
    public ulong Revision
    {
        get
        {
            return Authoring.Revision;
        }
    }
    public ulong SavepointRevision
    {
        get
        {
            return Authoring.SavepointRevision;
        }
    }
    public bool Undo() => CanUndo && Authoring.Undo();
    public bool Redo() => CanRedo && Authoring.Redo();
    public void Save() => Authoring.Save();

    public bool Edit(string Operation, out string Message)
    {
        return Authoring.Edit("state-machine", Root => Apply(Root, Operation), out Message);
    }

    private void Validate(JsonObject Root)
    {
        var Candidate = AnimationStateMachineAsset.Parse(Root.ToJsonString());
        if (Candidate.AssetId != ExpectedAssetId)
        {
            throw new InvalidDataException("The embedded assetId must match the asset manifest.");
        }
        foreach (var ClipId in Candidate.ClipIds)
        {
            var Clip = Assets.Find(ClipId);
            var Path = Clip is null ? null : Assets.ResolvePath(Clip);
            if (Clip?.Type != "SkeletalAnimation" || Path is null || !File.Exists(Path))
            {
                throw new InvalidDataException($"Clip '{ClipId}' must reference an existing SkeletalAnimation asset.");
            }
            try
            {
                _ = SkeletalAnimationContent.Load(Assets, ClipId);
            }
            catch (Exception Error) when (Error is IOException or InvalidDataException)
            {
                throw new InvalidDataException($"Clip '{ClipId}' could not be validated.");
            }
        }
        // Normalize supported v1 input once, inside the same validated document boundary.
        var Normalized = Candidate.ToJson();
        Root.Clear();
        foreach (var Field in Normalized)
        {
            Root[Field.Key] = Field.Value?.DeepClone();
        }
    }

    private void Apply(JsonObject Root, string Operation)
    {
        var Parts = Operation.Split(':', 5);
        var Kind = Parts[0];
        var MinimumParts = Kind switch
        {
            "name" or "initial" => 2,
            "state" or "parameter" or "transition" => 4,
            "condition" => 5,
            "remove-condition" => 3,
            "remove-state" or "remove-parameter" or "remove-transition" or
                "move-up" or "move-down" or "add-condition" => 2,
            _ => 1,
        };
        if (Parts.Length < MinimumParts)
        {
            throw new InvalidDataException("The animation state machine operation is incomplete.");
        }
        if (Kind == "name")
        {
            Root["name"] = Operation["name:".Length..];
            return;
        }
        if (Kind == "initial")
        {
            Root["initialStateId"] = Operation["initial:".Length..];
            return;
        }
        if (Kind == "add-state")
        {
            var ClipId = Root["states"]!.AsArray().FirstOrDefault()?["clipId"]?.GetValue<string>() ??
                Assets.Assets.FirstOrDefault(Asset => Asset.Type == "SkeletalAnimation")?.Id ??
                throw new InvalidDataException("A SkeletalAnimation asset is required before adding a state.");
            Root["states"]!.AsArray().Add(new JsonObject
            {
                ["stateId"] = UniqueId(Root["states"]!.AsArray(), "stateId", "State"),
                ["clipId"] = ClipId,
                ["loop"] = true,
                ["speed"] = 1,
            });
            return;
        }
        if (Kind == "add-parameter")
        {
            Root["parameters"]!.AsArray().Add(new JsonObject
            {
                ["parameterId"] = UniqueId(Root["parameters"]!.AsArray(), "parameterId", "Parameter"),
                ["defaultValue"] = 0,
            });
            return;
        }
        if (Kind == "add-transition")
        {
            var States = Root["states"]!.AsArray();
            if (States.Count < 2)
            {
                throw new InvalidDataException("A transition requires at least two states.");
            }
            Root["transitions"]!.AsArray().Add(new JsonObject
            {
                ["transitionId"] = UniqueId(Root["transitions"]!.AsArray(), "transitionId", "Transition"),
                ["fromStateId"] = States[0]!["stateId"]!.GetValue<string>(),
                ["toStateId"] = States[1]!["stateId"]!.GetValue<string>(),
                ["exitTime"] = 1,
                ["blendDuration"] = 0.2,
                ["blendCurve"] = "SmoothStep",
                ["conditions"] = new JsonArray(),
            });
            return;
        }

        var Index = Parts.Length > 1 ? int.Parse(Parts[1], CultureInfo.InvariantCulture) : -1;
        var Collection = Kind switch
        {
            "state" or "remove-state" => "states",
            "parameter" or "remove-parameter" => "parameters",
            "transition" or "remove-transition" or "move-up" or "move-down" or
                "add-condition" or "remove-condition" or "condition" => "transitions",
            _ => throw new InvalidDataException("Unknown animation state machine operation."),
        };
        var Items = Root[Collection]!.AsArray();
        if (Index < 0 || Index >= Items.Count)
        {
            throw new InvalidDataException("The selected row no longer exists.");
        }
        var Item = Items[Index]!.AsObject();
        if (Kind == "remove-state")
        {
            if (Items.Count == 1)
            {
                throw new InvalidDataException("A state machine must retain at least one state.");
            }
            var StateId = Item["stateId"]!.GetValue<string>();
            var Transitions = Root["transitions"]!.AsArray();
            for (var TransitionIndex = Transitions.Count - 1; TransitionIndex >= 0; TransitionIndex--)
            {
                var Transition = Transitions[TransitionIndex]!;
                if (Transition["fromStateId"]!.GetValue<string>() == StateId ||
                    Transition["toStateId"]!.GetValue<string>() == StateId)
                {
                    Transitions.RemoveAt(TransitionIndex);
                }
            }
            Items.RemoveAt(Index);
            if (Root["initialStateId"]!.GetValue<string>() == StateId)
            {
                Root["initialStateId"] = Items[0]!["stateId"]!.GetValue<string>();
            }
            return;
        }
        if (Kind is "remove-parameter" or "remove-transition")
        {
            Items.RemoveAt(Index);
            return;
        }
        if (Kind is "move-up" or "move-down")
        {
            var Destination = Index + (Kind == "move-up" ? -1 : 1);
            if (Destination < 0 || Destination >= Items.Count)
            {
                return;
            }
            Items.RemoveAt(Index);
            Items.Insert(Destination, Item);
            return;
        }
        if (Kind == "add-condition")
        {
            var Parameter = Root["parameters"]!.AsArray().FirstOrDefault() ??
                throw new InvalidDataException("Add a parameter before adding a condition.");
            Item["conditions"]!.AsArray().Add(new JsonObject
            {
                ["parameterId"] = Parameter["parameterId"]!.GetValue<string>(),
                ["comparison"] = "Greater",
                ["value"] = 0,
            });
            return;
        }
        if (Kind is "condition" or "remove-condition")
        {
            var ConditionIndex = int.Parse(Parts[2], CultureInfo.InvariantCulture);
            var Conditions = Item["conditions"]!.AsArray();
            if (ConditionIndex < 0 || ConditionIndex >= Conditions.Count)
            {
                throw new InvalidDataException("The condition no longer exists.");
            }
            if (Kind == "remove-condition")
            {
                Conditions.RemoveAt(ConditionIndex);
                return;
            }
            SetField(Conditions[ConditionIndex]!.AsObject(), Parts[3], Parts[4],
                ["parameterId", "comparison", "value"]);
            return;
        }

        var Field = Parts[2];
        var Value = string.Join(':', Parts.Skip(3));
        if (Kind == "state" && Field == "stateId")
        {
            RenameReferences(Root, Item["stateId"]!.GetValue<string>(), Value, true);
        }
        if (Kind == "parameter" && Field == "parameterId")
        {
            RenameReferences(Root, Item["parameterId"]!.GetValue<string>(), Value, false);
        }
        SetField(Item, Field, Value, Kind switch
        {
            "state" => ["stateId", "clipId", "loop", "speed"],
            "parameter" => ["parameterId", "defaultValue"],
            _ => ["transitionId", "fromStateId", "toStateId", "exitTime", "blendDuration", "blendCurve"],
        });
    }

    private static void RenameReferences(JsonObject Root, string Before, string After, bool State)
    {
        if (State && Root["initialStateId"]!.GetValue<string>() == Before)
        {
            Root["initialStateId"] = After;
        }
        foreach (var Transition in Root["transitions"]!.AsArray())
        {
            JsonNode[] Rows = State ? [Transition!] :
                Transition!["conditions"]!.AsArray().Select(Node => Node!).ToArray();
            string[] Keys = State ? ["fromStateId", "toStateId"] : ["parameterId"];
            foreach (var Row in Rows)
            {
                foreach (var Key in Keys)
                {
                    if (Row[Key]!.GetValue<string>() == Before)
                    {
                        Row[Key] = After;
                    }
                }
            }
        }
    }

    private static void SetField(JsonObject Item, string Field, string Value, string[] Allowed)
    {
        if (!Allowed.Contains(Field, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Unknown state machine field.");
        }
        Item[Field] = Field switch
        {
            "loop" => JsonValue.Create(bool.Parse(Value)),
            "exitTime" when string.IsNullOrWhiteSpace(Value) || Value == "null" => null,
            "speed" or "defaultValue" or "exitTime" or "value" or "blendDuration" =>
                JsonValue.Create(double.Parse(Value, NumberStyles.Float, CultureInfo.InvariantCulture)),
            _ => JsonValue.Create(Value),
        };
    }

    private static string UniqueId(JsonArray Items, string Key, string Prefix)
    {
        var Index = 1;
        while (Items.Any(Item => Item![Key]!.GetValue<string>() == Prefix + Index))
        {
            Index++;
        }
        return Prefix + Index;
    }
}
