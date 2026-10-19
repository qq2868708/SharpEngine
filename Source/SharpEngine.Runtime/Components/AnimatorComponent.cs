using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Playables;
using System.Text.Json.Nodes;

namespace SharpEngine.Components;

[ComponentTypeId("engine.animator"), ComponentSchemaVersion(2), DisallowMultipleComponent,
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

    protected override void OnEvaluate(float DeltaSeconds)
    {
        var Assets = World.Assets;
        if (BoundTarget != Target)
        {
            ReleaseTarget();
            BoundTarget = Target;
            Playable = null;
            Pose = null;
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
        if (Clip.Id is null)
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
            if (Playable is null || BoundAssets != Assets || BoundGeneration != Generation || BoundClip != Clip.Id || BoundMesh != Target.Mesh.Id || BoundLoop != Loop)
            {
                Playable = AnimatorContent.CreatePlayable(Assets, ComponentId, Target.ComponentId, Clip.Id, Target.Mesh.Id, Time, Playing, Loop);
                Playable.PoseEvaluated += ReceivePose;
                BoundAssets = Assets;
                BoundGeneration = Generation;
                BoundClip = Clip.Id;
                BoundMesh = Target.Mesh.Id;
                BoundLoop = Loop;
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
            if (DeltaSeconds > 0)
            {
                Playable.Advance((double)DeltaSeconds * Speed);
                Time = Playable.Time;
                Playing = Playable.State == PlayableState.Playing;
            }
            if (Pose is null || SampledTime != Playable.Time)
            {
                Output.Clear();
                Playable.Evaluate(new(Playable.Time, DeltaSeconds, 0, DeltaSeconds == 0), Output);
                SampledTime = Playable.Time;
                SampleCount++;
            }
            Target.SetPose(this, Pose);
            Status = Playable.State.ToString();
            Code = "ANIMATOR_READY";
        }
        catch (Exception Error) when (Error is IOException or InvalidDataException or KeyNotFoundException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Playable = null;
            Pose = null;
            Code = "ANIMATOR_BINDING_INVALID";
            Message = Error.Message;
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
        ReleaseTarget();
        Status = "Disabled";
        Code = "ANIMATOR_DISABLED";
    }

    protected override void OnUninitialize()
    {
        ReleaseTarget();
        Playable = null;
        Pose = null;
    }

    protected internal override ComponentFrameState CaptureFrameState()
    {
        return new AnimatorFrameState(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["entityId"] = Entity.EntityId,
            ["componentId"] = ComponentId,
            ["targetComponentId"] = Target?.ComponentId,
            ["clipId"] = Clip.Id,
            ["status"] = Status,
            ["code"] = Code,
            ["message"] = Message,
            ["duration"] = Code == "ANIMATOR_READY" ? Playable?.Duration : null,
            ["sampleTime"] = Code == "ANIMATOR_READY" ? Playable?.Time : null,
            ["sampleCount"] = SampleCount,
        });
    }

    public static JsonObject Migrate(int Version, JsonObject State, IReadOnlyList<ComponentRecord> Siblings)
    {
        if (Version != 1 || State.Count != 5 || !State.ContainsKey("Clip") || !State.ContainsKey("Time") || !State.ContainsKey("Speed") || !State.ContainsKey("Playing") || !State.ContainsKey("Loop"))
        {
            throw new ComponentValidationException("ANIMATOR_MIGRATION", "state", "Only the complete Animator v1 state can migrate to v2.");
        }
        var Mesh = Siblings.SingleOrDefault(Component => Component.TypeId == "engine.skeletal-mesh");
        if (Mesh is null)
        {
            throw new ComponentValidationException("ANIMATOR_MIGRATION_TARGET", "Target", "Animator v1 requires its original same-entity skeletal mesh.");
        }
        State["Target"] = Mesh.ComponentId;
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
    }
}
