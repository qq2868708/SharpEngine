using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Mathematics;
using SharpEngine.Spatial;

namespace SharpEngine.CharacterMovement;

/// <summary>
/// Owns character locomotion for one entity through composition: the grounded
/// <see cref="ICharacterMovementMode"/> integrates intent, ordered <see cref="ICharacterMovementModifier"/>
/// components adjust each frame, and committed root motion is handed here by the engine.
/// The entity transform has exactly one writer, this component.
/// </summary>
[ComponentTypeId("6f0c3d1a-8b2e-4f7c-9a51-2c4e8d3b7a10"), ComponentSchemaVersion(1),
    DisallowMultipleComponent, AddComponentMenu("Gameplay/Character Movement"),
    RequireComponent(typeof(TransformComponent)), RequireComponent(typeof(ControllableComponent))]
public sealed partial class CharacterMovementComponent : SharpComponent, IMovementAuthority
{
    [Tooltip("Planar intent smaller than this is treated as no input.")]
    [Range(0, 1)] public float InputDeadZone = 0.1f;

    [Tooltip("Downward probe distance used to resolve the grounded state. Zero disables probing.")]
    [Range(0, 5)] public float GroundProbeDistance = 0.3f;

    [Tooltip("Minimum surface normal Y that still counts as walkable ground.")]
    [Range(-1, 1)] public float GroundMinNormalY = 0.5f;

    [Tooltip("Collider layer mask for the ground probe. Negative means every layer.")]
    public int GroundLayerMask = -1;

    [Tooltip("Mode component id that integrates grounded frames.")]
    public string GroundModeId = "walking";

    [Tooltip("Mode component id that integrates airborne frames.")]
    public string AirModeId = "falling";

    [Tooltip("When enabled the character consumes animation root motion instead of the engine default.")]
    public bool ApplyRootMotion = true;

    [Range(0, 10)] public float RootMotionScale = 1;

    [Tooltip("Apply the planar rotation carried by root motion deltas.")]
    public bool ApplyRootMotionRotation = true;

    [Tooltip("Suppress planar root motion while there is no movement intent.")]
    public bool RootMotionRequiresIntent;

    private Vector3 PendingWorldDisplacement;
    private Vector3 PendingRootTranslation;
    private float PendingRootYawDegrees;
    private float VerticalVelocity;
    private Vector3 Velocity;
    private bool Grounded = true;
    private bool HasFrame;
    private float IntentMagnitude;
    private string ActiveModeId = "";
    private string? AppliedRootMotionSource;
    private string Code = "CHARACTER_MOVEMENT_IDLE";
    private string? Message;

    public Vector3 LinearVelocity
    {
        get
        {
            return Velocity;
        }
    }

    public bool IsGrounded
    {
        get
        {
            return Grounded;
        }
    }

    public string StatusCode
    {
        get
        {
            return Code;
        }
    }

    public string StatusMessage
    {
        get
        {
            return Message ?? "";
        }
    }

    /// <summary>Root motion displacement claimed this frame, after scale and intent filtering.</summary>
    public Vector3 AppliedRootMotion
    {
        get;
        private set;
    }

    public void BeginMovementFrame(float DeltaSeconds)
    {
        World.CheckThread();
        PendingWorldDisplacement = Vector3.Zero;
        PendingRootTranslation = Vector3.Zero;
        PendingRootYawDegrees = 0;
        AppliedRootMotionSource = null;
        HasFrame = false;
        ActiveModeId = "";
        Velocity = Vector3.Zero;
        Message = null;
        if (DeltaSeconds <= 0 || !float.IsFinite(DeltaSeconds))
        {
            Code = "CHARACTER_MOVEMENT_IDLE";
            return;
        }
        var Controllable = Entity.GetComponent<ControllableComponent>();
        if (Controllable is null || !Controllable.Enabled || !Controllable.Entity.ActiveInHierarchy)
        {
            Code = "CHARACTER_MOVEMENT_TARGET_UNAVAILABLE";
            Message = "Character Movement requires an enabled Controllable component on the same entity.";
            return;
        }
        var Settings = BuildSettings();
        var Frame = new CharacterMovementFrame(WorldPosition(Entity), WorldRotation(Entity))
        {
            DeltaSeconds = DeltaSeconds,
            VerticalVelocity = VerticalVelocity,
        };
        var Intent = Controllable.MovementIntent;
        Frame.SpeedMultiplier = 1;
        var Magnitude = new Vector2(Intent.X, Intent.Y).Length();
        if (Magnitude > Settings.InputDeadZone && float.IsFinite(Magnitude) && Magnitude > 0.000001f)
        {
            Frame.IntentDirection = new Vector3(Intent.X, 0, Intent.Y) / Magnitude;
            Frame.IntentMagnitude = MathF.Min(1, Magnitude);
        }
        IntentMagnitude = Frame.IntentMagnitude;
        Frame.Grounded = ResolveGrounded(Settings);
        foreach (var Modifier in Modifiers())
        {
            Modifier.Modify(Frame, in Settings);
        }
        var Mode = ResolveMode(Frame.Grounded ? GroundModeId : AirModeId);
        if (Mode is null)
        {
            Code = "CHARACTER_MOVEMENT_MODE_UNAVAILABLE";
            Message = "Character Movement needs exactly one enabled mode component with id '" +
                (Frame.Grounded ? GroundModeId : AirModeId) + "'.";
            VerticalVelocity = Frame.VerticalVelocity;
            Grounded = Frame.Grounded;
            return;
        }
        ActiveModeId = Mode.ModeId;
        Mode.Step(Frame, in Settings);
        if (Frame.SpeedCap > 0 && Frame.MaxSpeed > 0)
        {
            Frame.MaxSpeed = MathF.Min(Frame.MaxSpeed, Frame.SpeedCap);
        }
        if (!IsFinite(Frame.Displacement))
        {
            Code = "CHARACTER_MOVEMENT_DISPLACEMENT_INVALID";
            Message = "A movement mode produced a non-finite displacement.";
            VerticalVelocity = 0;
            return;
        }
        VerticalVelocity = float.IsFinite(Frame.VerticalVelocity) ? Frame.VerticalVelocity : 0;
        Grounded = Frame.Grounded;
        PendingWorldDisplacement = Frame.Displacement;
        Velocity = Frame.DeltaSeconds > 0 ? Frame.Displacement / Frame.DeltaSeconds : Vector3.Zero;
        HasFrame = true;
        Code = Frame.Code;
        Message = Frame.Message;
    }

    public bool ConsumeRootMotion(in RootMotionContribution Contribution)
    {
        World.CheckThread();
        // Declining before a frame exists lets the engine fall back while the mode is unavailable.
        if (!HasFrame)
        {
            return false;
        }
        // The authority always claims the delta once it owns the entity, so the engine never
        // re-applies root motion behind the character's back; the settings decide the result.
        AppliedRootMotionSource = Contribution.AnimatorComponentId;
        if (!ApplyRootMotion || RootMotionScale <= 0)
        {
            return true;
        }
        var Translation = Contribution.WorldTranslation * RootMotionScale;
        if (RootMotionRequiresIntent && IntentMagnitude <= 0)
        {
            Translation.X = 0;
            Translation.Z = 0;
        }
        if (!IsFinite(Translation))
        {
            return true;
        }
        PendingRootTranslation += Translation;
        AppliedRootMotion += Translation;
        if (ApplyRootMotionRotation)
        {
            var Forward = Vector3.TransformNormal(new Vector3(0, 0, -1), Contribution.WorldDelta);
            var Yaw = MathF.Atan2(Forward.X, -Forward.Z) * (180 / MathF.PI);
            if (float.IsFinite(Yaw))
            {
                PendingRootYawDegrees += Yaw;
            }
        }
        return true;
    }

    public void EndMovementFrame(float DeltaSeconds)
    {
        World.CheckThread();
        if (!HasFrame)
        {
            return;
        }
        try
        {
            var Displacement = PendingWorldDisplacement + PendingRootTranslation;
            var Transform = Entity.GetComponent<TransformComponent>()!;
            if (Displacement != Vector3.Zero)
            {
                var Local = Displacement;
                if (Entity.Parent is not null)
                {
                    var Invertible = TransformMath.TryInverse(WorldMatrix(Entity.Parent), out var ParentInverse);
                    if (!Invertible)
                    {
                        Code = "CHARACTER_MOVEMENT_PARENT_INVALID";
                        Message = "The parent transform cannot convert world displacement.";
                        return;
                    }
                    Local = Vector3.TransformNormal(Displacement, ParentInverse);
                }
                if (!IsFinite(Local))
                {
                    Code = "CHARACTER_MOVEMENT_PARENT_INVALID";
                    Message = "World displacement is not representable in the local parent space.";
                    return;
                }
                Transform.Position += Local;
            }
            if (PendingRootYawDegrees != 0)
            {
                Transform.Rotation.Y += PendingRootYawDegrees;
            }
            Velocity = DeltaSeconds > 0 ? Displacement / DeltaSeconds : Vector3.Zero;
            if (AppliedRootMotionSource is not null)
            {
                Code = "CHARACTER_MOVEMENT_ROOT_MOTION";
            }
        }
        finally
        {
            ResetFrame();
        }
    }

    protected override void OnDisable()
    {
        ResetFrame();
        VerticalVelocity = 0;
        Code = "CHARACTER_MOVEMENT_DISABLED";
    }

    protected override void OnUninitialize()
    {
        ResetFrame();
        VerticalVelocity = 0;
    }

    protected override ComponentFrameState CaptureFrameState()
    {
        return new CharacterMovementFrameState(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["entityId"] = Entity.EntityId,
            ["componentId"] = ComponentId,
            ["groundModeId"] = GroundModeId,
            ["airModeId"] = AirModeId,
            ["activeModeId"] = ActiveModeId,
            ["grounded"] = Grounded,
            ["verticalVelocity"] = VerticalVelocity,
            ["velocity"] = new JsonArray(Velocity.X, Velocity.Y, Velocity.Z),
            ["intentMagnitude"] = IntentMagnitude,
            ["rootMotionSourceComponentId"] = AppliedRootMotionSource,
            ["code"] = Code,
            ["message"] = Message,
        });
    }

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (string.IsNullOrWhiteSpace(State.GroundModeId) || string.IsNullOrWhiteSpace(State.AirModeId))
        {
            Diagnostics.Error("CHARACTER_MOVEMENT_MODE", nameof(GroundModeId),
                "Character Movement requires non-empty grounded and airborne mode ids.");
        }
        if (State.InputDeadZone < 0 || State.InputDeadZone > 1 ||
            State.GroundProbeDistance < 0 || State.GroundProbeDistance > 5 ||
            State.GroundMinNormalY < -1 || State.GroundMinNormalY > 1 ||
            State.RootMotionScale < 0)
        {
            Diagnostics.Error("CHARACTER_MOVEMENT_PARAMETERS", nameof(InputDeadZone),
                "Dead zone, probe distance, ground normal, and root motion scale must be inside their ranges.");
        }
    }

    private CharacterMovementSettings BuildSettings()
    {
        return new CharacterMovementSettings(InputDeadZone, GroundProbeDistance, GroundMinNormalY,
            GroundLayerMask, ApplyRootMotion, RootMotionScale, ApplyRootMotionRotation,
            RootMotionRequiresIntent);
    }

    private ICharacterMovementMode? ResolveMode(string ModeId)
    {
        var Candidates = Available()
            .OfType<ICharacterMovementMode>()
            .Where(Mode => string.Equals(Mode.ModeId, ModeId, StringComparison.Ordinal))
            .OrderBy(Mode => ((SharpComponent)Mode).ComponentId, StringComparer.Ordinal)
            .ToArray();
        return Candidates.Length == 1 ? Candidates[0] : null;
    }

    private IReadOnlyList<ICharacterMovementModifier> Modifiers()
    {
        return Available()
            .OfType<ICharacterMovementModifier>()
            .OrderBy(Modifier => Modifier.Priority)
            .ThenBy(Modifier => ((SharpComponent)Modifier).ComponentId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The public component surface a plugin may query without engine internals.</summary>
    private SharpComponent[] Available()
    {
        return Entity.GetComponents<SharpComponent>()
            .Where(Component => Component.Enabled && Component.Entity.ActiveInHierarchy)
            .OrderBy(Component => Component.ComponentId, StringComparer.Ordinal)
            .ToArray();
    }

    private bool ResolveGrounded(in CharacterMovementSettings Settings)
    {
        // Rising characters are airborne by definition; only a settled character probes for ground.
        if (VerticalVelocity > 0)
        {
            return false;
        }
        if (Settings.GroundProbeDistance <= 0)
        {
            return true;
        }
        var Origin = WorldPosition(Entity) + new Vector3(0, 0.05f, 0);
        var Mask = Settings.GroundLayerMask < 0 ? uint.MaxValue : (uint)Settings.GroundLayerMask;
        var Hit = CharacterMovementQueries.Collision(World).Raycast(Origin, -Vector3.UnitY,
            Settings.GroundProbeDistance, Mask, IncludeTriggers: false, IgnoreEntityId: Entity.EntityId);
        return Hit is not null && Hit.Normal.Y >= Settings.GroundMinNormalY;
    }

    private void ResetFrame()
    {
        PendingWorldDisplacement = Vector3.Zero;
        PendingRootTranslation = Vector3.Zero;
        PendingRootYawDegrees = 0;
        AppliedRootMotionSource = null;
        HasFrame = false;
    }

    /// <summary>Row-vector world transform: local * parent, matching the engine's TransformMath contract.</summary>
    internal static Matrix4x4 WorldMatrix(SharpEntity Entity)
    {
        var Result = Matrix4x4.Identity;
        for (var Current = Entity; Current is not null; Current = Current.Parent)
        {
            var Transform = Current.GetComponent<TransformComponent>();
            if (Transform is not null)
            {
                Result *= TransformMath.Trs(Transform.Position, Transform.Rotation, Transform.Scale);
            }
        }
        return Result;
    }

    internal static Vector3 WorldPosition(SharpEntity Entity)
    {
        return WorldMatrix(Entity).Translation;
    }

    internal static Vector3 WorldRotation(SharpEntity Entity)
    {
        var Matrix = WorldMatrix(Entity);
        if (!TransformMath.TryDecompose(Matrix, out _, out var Rotation, out _))
        {
            return Vector3.Zero;
        }
        return QuaternionMath.ToEuler(Rotation);
    }

    private static bool IsFinite(Vector3 Value)
    {
        return float.IsFinite(Value.X) && float.IsFinite(Value.Y) && float.IsFinite(Value.Z);
    }
}

/// <summary>Published derived state; authoritative settings stay in the component record.</summary>
public sealed class CharacterMovementFrameState : ComponentFrameState
{
    private readonly JsonObject State;

    internal CharacterMovementFrameState(JsonObject State)
    {
        this.State = (JsonObject)State.DeepClone();
    }

    public override JsonObject ToJson()
    {
        return (JsonObject)State.DeepClone();
    }
}

/// <summary>
/// One committed collision query per world revision. The cache is keyed by the world instance, so
/// parallel worlds in one process never share geometry, and it never outlives its world.
/// </summary>
internal static class CharacterMovementQueries
{
    private sealed class Entry
    {
        internal ulong Revision = ulong.MaxValue;
        internal CollisionSnapshot? Snapshot;
    }

    private static readonly ConditionalWeakTable<RuntimeWorld, Entry> Cache = new();

    internal static CollisionSnapshot Collision(RuntimeWorld World)
    {
        var Entry = Cache.GetOrCreateValue(World);
        if (Entry.Snapshot is not null && Entry.Revision == World.Revision)
        {
            return Entry.Snapshot;
        }
        Entry.Snapshot = World.Collision;
        Entry.Revision = World.Revision;
        return Entry.Snapshot;
    }
}
