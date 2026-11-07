using System.Numerics;
using System.Text.Json.Nodes;

namespace SharpEngine.Animation;

/// <summary>One transient controller instance shared by Runtime and editor preview.</summary>
public sealed class AnimationStateMachinePlayer
{
    private readonly AnimationStateMachineAsset Asset;
    private readonly IReadOnlyDictionary<string, SkeletalAnimationAsset> Clips;
    private readonly IReadOnlyList<SkeletonBone> Bones;
    private AnimationStateDefinition? SourceState;
    private double SourceTime;
    private double BlendElapsed;
    private double BlendDuration;
    private string BlendCurve = "Linear";
    private double BlendWeight = 1;

    public AnimationStateMachinePlayer(AnimationStateMachineAsset Asset,
        IReadOnlyDictionary<string, SkeletalAnimationAsset> Clips, IReadOnlyList<SkeletonBone> Bones,
        string? StateId = null, double Time = 0)
    {
        ArgumentNullException.ThrowIfNull(Asset);
        ArgumentNullException.ThrowIfNull(Clips);
        ArgumentNullException.ThrowIfNull(Bones);
        this.Asset = Asset;
        this.Clips = Asset.ClipIds.ToDictionary(Id => Id, Id => Clips[Id], StringComparer.OrdinalIgnoreCase);
        this.Bones = Bones.ToArray();
        foreach (var Clip in this.Clips.Values)
        {
            SkeletonBinding.Validate(this.Bones, Clip.Bones, "State machine clip");
        }
        Seek(StateId ?? Asset.InitialStateId, Time);
    }

    public string StateId
    {
        get;
        private set;
    } = string.Empty;
    public double Time
    {
        get;
        private set;
    }
    public string? TransitionId
    {
        get;
        private set;
    }
    public bool IsBlending
    {
        get
        {
            return SourceState is not null;
        }
    }
    public ulong StateRevision
    {
        get;
        private set;
    }
    public Matrix4x4 RootDelta
    {
        get;
        private set;
    } = Matrix4x4.Identity;
    public bool RootMotionEnabled
    {
        get
        {
            return Clip(Asset.State(StateId)).RootMotion.Enabled ||
                (SourceState is not null && Clip(SourceState).RootMotion.Enabled);
        }
    }

    public void Seek(string StateId, double Time)
    {
        var State = Asset.State(StateId);
        ValidateTime(Time);
        this.StateId = StateId;
        this.Time = Normalize(State, Time);
        SourceState = null;
        TransitionId = null;
        BlendElapsed = 0;
        BlendDuration = 0;
        BlendWeight = 1;
        RootDelta = Matrix4x4.Identity;
        StateRevision++;
    }

    /// <summary>Explicit requests use the first declared edge, ignoring its automatic conditions.</summary>
    public void RequestState(string StateId)
    {
        _ = Asset.State(StateId);
        if (StateId == this.StateId)
        {
            return;
        }
        if (IsBlending)
        {
            throw new InvalidOperationException("Wait for the current transition to finish or reset the preview.");
        }
        var Edge = Asset.Transitions.FirstOrDefault(Transition =>
            Transition.FromStateId == this.StateId && Transition.ToStateId == StateId);
        Begin(StateId, Edge);
        RootDelta = Matrix4x4.Identity;
    }

    public void TriggerTransition(string TransitionId)
    {
        var Edge = Asset.Transitions.SingleOrDefault(Transition => Transition.TransitionId == TransitionId) ??
            throw new InvalidDataException("Unknown animation transition.");
        if (IsBlending || Edge.FromStateId != StateId)
        {
            throw new InvalidOperationException("A transition can only start from its source state while no blend is active.");
        }
        Begin(Edge.ToStateId, Edge);
        RootDelta = Matrix4x4.Identity;
    }

    /// <summary>Blend seconds use the controller clock; each state's speed controls its own clip.</summary>
    public void Advance(double DeltaSeconds, IReadOnlyDictionary<string, double> Parameters)
    {
        ValidateTime(DeltaSeconds);
        foreach (var Definition in Asset.Parameters)
        {
            var Found = Parameters.TryGetValue(Definition.ParameterId, out var Value);
            if (!Found || !double.IsFinite(Value))
            {
                throw new InvalidDataException("State machine parameters must contain every declared finite value.");
            }
        }
        RootDelta = Matrix4x4.Identity;
        if (DeltaSeconds == 0)
        {
            return;
        }
        var State = Asset.State(StateId);
        var Advance = DeltaSeconds * State.Speed;
        ValidateTime(Time + Advance);
        var TargetDelta = RootMotionSampler.Delta(Clip(State), Time, Advance, State.Loop);
        var NextTime = Time + Advance;
        Time = Normalize(State, NextTime);
        if (SourceState is not null)
        {
            var Remaining = Math.Max(0, BlendDuration - BlendElapsed);
            var BlendStep = Math.Min(DeltaSeconds, Remaining);
            var SourceAdvance = BlendStep * SourceState.Speed;
            ValidateTime(SourceTime + SourceAdvance);
            var SourceDelta = RootMotionSampler.Delta(Clip(SourceState), SourceTime, SourceAdvance, SourceState.Loop);
            SourceTime = Normalize(SourceState, SourceTime + SourceAdvance);
            var Before = BlendWeight;
            BlendElapsed = Math.Min(BlendDuration, BlendElapsed + BlendStep);
            var Progress = Math.Clamp(BlendElapsed / BlendDuration, 0, 1);
            BlendWeight = BlendCurve == "SmoothStep" ? Progress * Progress * (3 - 2 * Progress) : Progress;
            var TargetBlendDelta = RootMotionSampler.Delta(Clip(State),
                Normalize(State, NextTime - Advance), BlendStep * State.Speed, State.Loop);
            RootDelta = BlendTransform(SourceDelta, TargetBlendDelta, (Before + BlendWeight) * 0.5);
            if (DeltaSeconds > BlendStep)
            {
                var Tail = RootMotionSampler.Delta(Clip(State),
                    Normalize(State, NextTime - Advance + BlendStep * State.Speed),
                    (DeltaSeconds - BlendStep) * State.Speed, State.Loop);
                RootDelta = Tail * RootDelta;
            }
            if (BlendElapsed >= BlendDuration)
            {
                SourceState = null;
            }
        }
        else
        {
            RootDelta = TargetDelta;
        }
        if (SourceState is null)
        {
            var Edge = Asset.SelectTransition(StateId, NextTime / Clip(State).Duration, Parameters);
            if (Edge is not null)
            {
                Begin(Edge.ToStateId, Edge);
            }
        }
        StateRevision++;
    }

    /// <summary>Runtime strips each clip's root before blending; preview retains enabled source trajectories.</summary>
    public SkeletalPose Sample(bool Preview = false)
    {
        SkeletalPose Pose(AnimationStateDefinition State, double SampleTime)
        {
            var Animation = Clip(State);
            var Result = Animation.Sample(SampleTime);
            return Preview && Animation.RootMotion.Enabled ? Result : RootMotionSampler.RemoveRootMotion(Animation, Result);
        }
        var Target = Pose(Asset.State(StateId), Time);
        var TargetBones = Target.Bones.ToDictionary(Bone => Bone.BoneId, Bone => Bone.Transform, StringComparer.Ordinal);
        var SourceBones = SourceState is null ? null :
            Pose(SourceState, SourceTime).Bones.ToDictionary(Bone => Bone.BoneId, Bone => Bone.Transform, StringComparer.Ordinal);
        var Result = Bones.Select(Bone =>
        {
            var To = TargetBones.GetValueOrDefault(Bone.BoneId, Bone.BindTransform);
            var From = SourceBones?.GetValueOrDefault(Bone.BoneId, Bone.BindTransform) ?? To;
            return (Bone.BoneId, Blend(From, To, BlendWeight));
        }).ToArray();
        return new SkeletalPose(Time, Array.AsReadOnly(Result));
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["stateRevision"] = StateRevision,
            ["stateId"] = StateId,
            ["time"] = Time,
            ["clipId"] = Asset.State(StateId).ClipId,
            ["transitionId"] = TransitionId,
            ["blending"] = IsBlending,
            ["sourceStateId"] = SourceState?.StateId,
            ["sourceTime"] = SourceState is null ? null : JsonValue.Create(SourceTime),
            ["blendDuration"] = BlendDuration,
            ["blendElapsed"] = BlendElapsed,
            ["blendCurve"] = BlendCurve,
            ["blendWeight"] = BlendWeight,
        };
    }

    private void Begin(string StateId, AnimationTransitionDefinition? Edge)
    {
        SourceState = Edge?.BlendDuration > 0 ? Asset.State(this.StateId) : null;
        SourceTime = Time;
        this.StateId = StateId;
        Time = 0;
        TransitionId = Edge?.TransitionId;
        BlendElapsed = 0;
        BlendDuration = Edge?.BlendDuration ?? 0;
        BlendCurve = Edge?.BlendCurve ?? "Linear";
        BlendWeight = SourceState is null ? 1 : 0;
        StateRevision++;
    }

    private SkeletalAnimationAsset Clip(AnimationStateDefinition State) { return Clips[State.ClipId]; }
    private double Normalize(AnimationStateDefinition State, double Time)
    {
        var Duration = Clip(State).Duration;
        return State.Loop ? Time % Duration : Math.Min(Time, Duration);
    }
    private static void ValidateTime(double Time)
    {
        if (!double.IsFinite(Time) || Time < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Time), "Animation time must be finite and nonnegative.");
        }
    }
    private static BoneTransform Blend(BoneTransform From, BoneTransform To, double Weight)
    {
        return new BoneTransform(Vector3.Lerp(From.Position, To.Position, (float)Weight),
            Quaternion.Normalize(Quaternion.Slerp(From.Rotation, To.Rotation, (float)Weight)),
            Vector3.Lerp(From.Scale, To.Scale, (float)Weight));
    }
    private static Matrix4x4 BlendTransform(Matrix4x4 From, Matrix4x4 To, double Weight)
    {
        var FromValid = Matrix4x4.Decompose(From, out var FromScale, out var FromRotation, out var FromPosition);
        var ToValid = Matrix4x4.Decompose(To, out var ToScale, out var ToRotation, out var ToPosition);
        if (!FromValid || !ToValid)
        {
            throw new InvalidDataException("Root motion blend requires decomposable transforms.");
        }
        var Value = Blend(new(FromPosition, FromRotation, FromScale), new(ToPosition, ToRotation, ToScale), Weight);
        return Matrix4x4.CreateScale(Value.Scale) * Matrix4x4.CreateFromQuaternion(Value.Rotation) *
            Matrix4x4.CreateTranslation(Value.Position);
    }
}
