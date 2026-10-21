using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Playables;
using System.Text.Json.Nodes;
using System.Numerics;

namespace SharpEngine.Components;

[ComponentTypeId("engine.animator"), ComponentSchemaVersion(3), DisallowMultipleComponent,
    AddComponentMenu("Animation/Animator")]
public sealed partial class AnimatorComponent : SharpComponent
{
    public SkeletalMeshComponent? Target;
    public AssetRef<SkeletalAnimationAsset> Clip;
    [Tooltip("Playable playhead in seconds. Seek and advancement follow the clip's loop mode.")]
    public double Time;
    public float Speed = 1;
    public bool Playing = true;
    public bool Loop = true;
    public AssetRef<AnimationStateMachineAsset> Controller;
    [Tooltip("Empty selects the controller's initial state. Changing a state resets Time at the next evaluation.")]
    public string StateId = "";
    public List<AnimatorParameter> Parameters = [];
    public MovementComponent? MovementSource;
    [Tooltip("Optional controller parameter bound to MovementSource speed, measured after movement evaluation.")]
    public string MovementSpeedParameter = "";

    private SkeletalMeshComponent? BoundTarget;
    private SkeletalAnimationPlayable? Playable;
    private AssetRegistry? BoundAssets;
    private int BoundGeneration;
    private string? BoundClip;
    private string? BoundMesh;
    private bool BoundLoop;
    private SkeletalPose? Pose;
    private double SampledTime = double.NaN;
    private ulong SampleCount;
    private string Status = "Idle";
    private string Code = "ANIMATOR_IDLE";
    private string? Message;
    private readonly ListPlayableOutputSink Output = new();
    private AnimatorControllerBinding? ControllerBinding;
    private string? BoundController;
    private int ControllerGeneration;
    private AssetRegistry? ControllerAssets;
    private Dictionary<string, double> EffectiveParameters = new(StringComparer.Ordinal);
    private string? TransitionId;
    private string? BoundStateId;
    private Matrix4x4 PendingRootDelta = Matrix4x4.Identity;
    private double PendingRootAdvance;
    private Vector3 AppliedRootTranslation;
    private bool RootApplied;
    private bool RootRedirectedByPlayer;
    private string RootCode = "ROOT_MOTION_DISABLED";
    private string? RootMessage;
    internal SharpEntity? RootMotionTarget
    {
        get;
        private set;
    }
    internal Matrix4x4 RootMotionDelta
    {
        get
        {
            return PendingRootDelta;
        }
    }
    internal SkeletalMeshComponent? RootMotionMesh
    {
        get
        {
            return BoundTarget;
        }
    }

    protected override void OnEvaluate(float DeltaSeconds)
    {
        if (DeltaSeconds == 0)
        {
            Evaluate(DeltaSeconds);
        }
    }

    internal void PrepareAnimationFrame(float DeltaSeconds) { Evaluate(DeltaSeconds); }

    private void Evaluate(float DeltaSeconds)
    {
        RootMotionTarget = null;
        if (DeltaSeconds > 0)
        {
            PendingRootDelta = Matrix4x4.Identity;
            PendingRootAdvance = 0;
            AppliedRootTranslation = Vector3.Zero;
            RootApplied = false;
            RootRedirectedByPlayer = false;
            RootCode = "ROOT_MOTION_DISABLED";
            RootMessage = null;
        }
        var Assets = World.Assets;
        if (BoundTarget != Target)
        {
            ReleaseTarget();
            BoundTarget = Target;
            Playable = null;
            Pose = null;
            ControllerBinding = null;
            BoundStateId = null;
        }
        Message = null;
        Status = "Idle";
        Code = "ANIMATOR_IDLE";
        if (Target is null || !Target.IsAlive)
        {
            Status = "Unbound";
            Code = "ANIMATOR_TARGET_MISSING";
            return;
        }
        if (!Target.Enabled || !Target.Entity.ActiveInHierarchy)
        {
            Target.ClearPose(this);
            Status = "Disabled";
            Code = "ANIMATOR_TARGET_DISABLED";
            return;
        }
        Target.SetPose(this, null);
        if (Clip.Id is null && Controller.Id is null)
        {
            Playable = null;
            Pose = null;
            return;
        }
        Status = "Unavailable";
        Code = Assets is null ? "ANIMATOR_ASSETS_UNAVAILABLE" : "ANIMATOR_MESH_MISSING";
        if (Assets is null || Target.Mesh.Id is null)
        {
            return;
        }
        try
        {
            var Generation = AnimatorContent.Generation(Assets);
            var ClipId = Clip.Id;
            var ClipLoop = Loop;
            var RootFromTime = Time;
            var RootAdvance = 0d;
            if (Controller.Id is not null)
            {
                if (ControllerBinding is null || ControllerAssets != Assets || BoundController != Controller.Id ||
                    ControllerGeneration != Generation || BoundMesh != Target.Mesh.Id)
                {
                    var Candidate = AnimatorControllerBinding.Load(Assets, Controller.Id, Target.Mesh.Id);
                    // Validate live selections before committing a reloaded asset.
                    _ = Candidate.Asset.State(StateId == "" ? Candidate.Asset.InitialStateId : StateId);
                    _ = Candidate.Asset.ResolveParameters(Parameters);
                    ValidateMovementBinding(Candidate.Asset);
                    ControllerBinding = Candidate;
                    ControllerAssets = Assets;
                    BoundController = Controller.Id;
                    ControllerGeneration = Generation;
                }
                var Graph = ControllerBinding.Asset;
                var State = Graph.State(StateId == "" ? Graph.InitialStateId : StateId);
                var StateChanged = BoundStateId is not null && BoundStateId != State.StateId;
                var Values = Graph.ResolveParameters(Parameters);
                ValidateMovementBinding(Graph);
                if (MovementSource is not null && MovementSpeedParameter != "")
                {
                    Values[MovementSpeedParameter] = MovementSource.IsAlive && MovementSource.Enabled && MovementSource.Entity.ActiveInHierarchy
                        ? MovementSource.AnimationIntentVelocity.Length() : 0;
                }
                var NextTime = StateChanged ? 0 : Time;
                RootFromTime = NextTime;
                if (DeltaSeconds > 0 && Playing)
                {
                    RootAdvance = (double)DeltaSeconds * Speed * State.Speed;
                    NextTime += RootAdvance;
                    if (!double.IsFinite(NextTime))
                    {
                        throw new InvalidDataException("Animation advancement must remain finite.");
                    }
                    var Duration = ControllerBinding.Clips[State.ClipId].Duration;
                    var Transition = Graph.SelectTransition(State.StateId, NextTime / Duration, Values);
                    TransitionId = Transition?.TransitionId;
                    if (Transition is not null)
                    {
                        State = Graph.State(Transition.ToStateId);
                        NextTime = 0;
                        RootFromTime = 0;
                        RootAdvance = 0;
                    }
                    else
                    {
                        NextTime = State.Loop ? NextTime % Duration : Math.Min(NextTime, Duration);
                    }
                }
                StateId = State.StateId;
                BoundStateId = State.StateId;
                Time = NextTime;
                EffectiveParameters = Values;
                ClipId = State.ClipId;
                ClipLoop = State.Loop;
            }
            else
            {
                EffectiveParameters.Clear();
                TransitionId = null;
                ControllerBinding = null;
                BoundStateId = null;
            }
            if (Playable is null || BoundAssets != Assets || BoundGeneration != Generation || BoundClip != ClipId || BoundMesh != Target.Mesh.Id || BoundLoop != ClipLoop)
            {
                Playable = AnimatorContent.CreatePlayable(Assets, ComponentId, Target.ComponentId, ClipId!, Target.Mesh.Id, Time, Playing, ClipLoop);
                Playable.PoseEvaluated += ReceivePose;
                BoundAssets = Assets;
                BoundGeneration = Generation;
                BoundClip = ClipId;
                BoundMesh = Target.Mesh.Id;
                BoundLoop = ClipLoop;
                SampledTime = double.NaN;
                Pose = null;
            }
            if (Playing)
            {
                Playable.Play();
            }
            else
            {
                Playable.Pause();
            }
            Playable.Seek(Time);
            if (DeltaSeconds > 0 && Controller.Id is null)
            {
                RootFromTime = Playable.Time;
                RootAdvance = Playing ? (double)DeltaSeconds * Speed : 0;
                Playable.Advance((double)DeltaSeconds * Speed);
                Time = Playable.Time;
                Playing = Playable.State == PlayableState.Playing;
            }
            if (Pose is null || SampledTime != Playable.Time)
            {
                if (DeltaSeconds == 0)
                {
                    PendingRootDelta = Matrix4x4.Identity;
                    PendingRootAdvance = 0;
                    AppliedRootTranslation = Vector3.Zero;
                    RootApplied = false;
                    RootCode = "ROOT_MOTION_DISABLED";
                }
                Output.Clear();
                Playable.Evaluate(new(Playable.Time, DeltaSeconds, 0, DeltaSeconds == 0), Output);
                SampledTime = Playable.Time;
                SampleCount++;
            }
            if (Playable.Asset.RootMotion.Enabled)
            {
                var Candidate = MovementSource?.Entity ?? Entity;
                var Ancestor = Target.Entity;
                while (Ancestor is not null && Ancestor != Candidate)
                {
                    Ancestor = Ancestor.Parent;
                }
                if (Ancestor is null)
                {
                    throw new InvalidDataException("Root motion target must own the animated mesh hierarchy");
                }
                RootMotionTarget = Candidate;
                if (DeltaSeconds > 0)
                {
                    PendingRootAdvance = RootAdvance;
                    PendingRootDelta = RootMotionSampler.Delta(Playable.Asset, RootFromTime, RootAdvance, ClipLoop);
                }
                if (RootCode == "ROOT_MOTION_DISABLED")
                {
                    RootCode = "ROOT_MOTION_READY";
                }
                Target.SetPose(this, RootMotionSampler.RemoveRootMotion(Playable.Asset, Pose!));
            }
            else
            {
                RootCode = "ROOT_MOTION_DISABLED";
                RootApplied = false;
                AppliedRootTranslation = Vector3.Zero;
                Target.SetPose(this, Pose);
            }
            Status = Playable.State.ToString();
            var RootFailed = RootCode is "ROOT_MOTION_CONFLICT" or "ROOT_MOTION_TRANSFORM_INVALID";
            Code = RootFailed ? RootCode : "ANIMATOR_READY";
            if (RootFailed)
            {
                Message = RootMessage;
            }
        }
        catch (Exception Error) when (Error is IOException or InvalidDataException or KeyNotFoundException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // A failed staged reload freezes the last committed pose on the same mesh.
            if (Controller.Id is not null && BoundAssets == Assets && BoundMesh == Target.Mesh.Id)
            {
                Target.SetPose(this, Pose);
            }
            else
            {
                Playable = null;
                Pose = null;
            }
            Code = "ANIMATOR_BINDING_INVALID";
            Message = Error.Message;
            RootMotionTarget = null;
            RootCode = "ROOT_MOTION_BINDING_INVALID";
        }
    }

    internal void CompleteRootMotion(Vector3 Translation, bool RedirectedByPlayer = false)
    {
        AppliedRootTranslation = Translation;
        RootApplied = PendingRootAdvance > 0;
        RootRedirectedByPlayer = RedirectedByPlayer;
        RootCode = RootApplied ? "ROOT_MOTION_APPLIED" : "ROOT_MOTION_READY";
        RootMessage = null;
    }

    internal void RejectRootMotion(string DiagnosticCode, string DiagnosticMessage)
    {
        RootApplied = false;
        AppliedRootTranslation = Vector3.Zero;
        RootCode = DiagnosticCode;
        Code = DiagnosticCode;
        Message = DiagnosticMessage;
        RootMessage = DiagnosticMessage;
    }

    private void ValidateMovementBinding(AnimationStateMachineAsset Graph)
    {
        if (MovementSpeedParameter != "" && !Graph.Parameters.Any(Parameter => Parameter.ParameterId == MovementSpeedParameter))
        {
            throw new InvalidDataException("MovementSpeedParameter must name a declared controller parameter.");
        }
    }

    private void ReceivePose(SkeletalPose Pose) { this.Pose = Pose; }

    private void ReleaseTarget()
    {
        if (BoundTarget?.IsAlive == true)
        {
            BoundTarget.ClearPose(this);
        }
        BoundTarget = null;
    }

    protected override void OnDisable()
    {
        RootMotionTarget = null;
        ReleaseTarget();
        Status = "Disabled";
        Code = "ANIMATOR_DISABLED";
    }

    protected override void OnUninitialize()
    {
        ReleaseTarget();
        Playable = null;
        Pose = null;
        ControllerBinding = null;
    }

    protected internal override ComponentFrameState CaptureFrameState()
    {
        return new AnimatorFrameState(new JsonObject
        {
            ["schemaVersion"] = 3,
            ["entityId"] = Entity.EntityId,
            ["componentId"] = ComponentId,
            ["targetComponentId"] = Target?.ComponentId,
            ["clipId"] = Controller.Id is null ? Clip.Id : BoundClip,
            ["controllerId"] = Controller.Id,
            ["stateId"] = Controller.Id is null ? null : StateId,
            ["transitionId"] = TransitionId,
            ["parameters"] = new JsonObject(EffectiveParameters.OrderBy(Entry => Entry.Key, StringComparer.Ordinal)
                .Select(Entry => new KeyValuePair<string, JsonNode?>(Entry.Key, JsonValue.Create(Entry.Value)))),
            ["status"] = Status,
            ["code"] = Code,
            ["message"] = Message,
            ["duration"] = Code == "ANIMATOR_READY" ? Playable?.Duration : null,
            ["sampleTime"] = Code == "ANIMATOR_READY" ? Playable?.Time : null,
            ["sampleCount"] = SampleCount,
            ["rootMotion"] = new JsonObject
            {
                ["enabled"] = Playable?.Asset.RootMotion.Enabled ?? false,
                ["boneId"] = Playable?.Asset.RootMotion.BoneId,
                ["targetEntityId"] = RootMotionTarget?.EntityId,
                ["applied"] = RootApplied,
                ["redirectedByPlayer"] = RootRedirectedByPlayer,
                ["code"] = RootCode,
                ["worldTranslation"] = new JsonArray(AppliedRootTranslation.X, AppliedRootTranslation.Y, AppliedRootTranslation.Z),
            },
        });
    }

    public static JsonObject Migrate(int Version, JsonObject State, IReadOnlyList<ComponentRecord> Siblings)
    {
        var Fields = Version == 1 ? new[] { "Clip", "Time", "Speed", "Playing", "Loop" } :
            new[] { "Target", "Clip", "Time", "Speed", "Playing", "Loop" };
        if ((Version != 1 && Version != 2) || State.Count != Fields.Length || Fields.Any(Field => !State.ContainsKey(Field)))
        {
            throw new ComponentValidationException("ANIMATOR_MIGRATION", "state", "Only complete Animator v1/v2 state can migrate to v3.");
        }
        if (Version == 1)
        {
            var Mesh = Siblings.SingleOrDefault(Component => Component.TypeId == "engine.skeletal-mesh");
            if (Mesh is null)
            {
                throw new ComponentValidationException("ANIMATOR_MIGRATION_TARGET", "Target", "Animator v1 requires its original same-entity skeletal mesh.");
            }
            State["Target"] = Mesh.ComponentId;
        }
        State["Controller"] = null;
        State["StateId"] = "";
        State["Parameters"] = new JsonArray();
        State["MovementSource"] = null;
        State["MovementSpeedParameter"] = "";
        return State;
    }

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.Time < 0)
        {
            Diagnostics.Error("ANIMATOR_TIME", nameof(Time), "Animation time must be nonnegative.");
        }
        if (State.Speed < 0)
        {
            Diagnostics.Error("ANIMATOR_SPEED", nameof(Speed), "Playback speed must be nonnegative.");
        }
        var Parameters = State.Parameters;
        if (State.StateId is null || State.MovementSpeedParameter is null || Parameters is null ||
            Parameters.Any(Parameter => Parameter is null || string.IsNullOrWhiteSpace(Parameter.ParameterId) || !double.IsFinite(Parameter.Value)) ||
            Parameters.Select(Parameter => Parameter.ParameterId).Distinct(StringComparer.Ordinal).Count() != Parameters.Count)
        {
            Diagnostics.Error("ANIMATOR_PARAMETERS", nameof(Parameters), "State IDs must be strings and parameter overrides must have unique nonempty IDs and finite values.");
        }
    }
}
