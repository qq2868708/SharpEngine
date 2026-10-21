using System.Numerics;
using System.Text.Json.Nodes;

namespace SharpEngine.Components;

/// <summary>Declares that an entity can receive control intent from a controller.</summary>
[ComponentTypeId("engine.controllable"), ComponentSchemaVersion(1), DisallowMultipleComponent,
    AddComponentMenu("Gameplay/Controllable")]
public sealed partial class ControllableComponent : SharpComponent
{
    public bool ControlEnabled = true;

    private ControllerComponent? BoundController;
    private Vector2 MoveInput;
    private string Status = "Unbound";
    private string Code = "CONTROL_UNBOUND";
    private string? Message;

    internal bool TryBind(ControllerComponent Controller)
    {
        if (!ControlEnabled)
        {
            Status = "Disabled";
            Code = "CONTROL_DISABLED";
            Message = "ControlEnabled is false in the authoritative component state.";
            return false;
        }
        if (!Enabled || !Entity.ActiveInHierarchy)
        {
            Status = "Unavailable";
            Code = "CONTROL_TARGET_UNAVAILABLE";
            Message = "The target component or entity is disabled.";
            return false;
        }
        if (BoundController is null || !BoundController.IsAlive ||
            ReferenceEquals(BoundController, Controller) || Compare(Controller, BoundController) < 0)
        {
            BoundController = Controller;
            Status = "Bound";
            Code = "CONTROL_BOUND";
            Message = null;
            return true;
        }
        return ReferenceEquals(BoundController, Controller);
    }

    internal void Release(ControllerComponent Controller)
    {
        if (!ReferenceEquals(BoundController, Controller))
        {
            return;
        }
        BoundController = null;
        MoveInput = Vector2.Zero;
        Status = "Unbound";
        Code = "CONTROL_UNBOUND";
        Message = null;
    }

    internal bool SubmitMovement(ControllerComponent Controller, Vector2 Input)
    {
        if (!ReferenceEquals(BoundController, Controller) || !ControlEnabled || !Enabled || !Entity.ActiveInHierarchy)
        {
            return false;
        }
        MoveInput = NormalizeInput(Input);
        Status = "Bound";
        Code = "CONTROL_READY";
        Message = null;
        return true;
    }

    internal Vector2 ReadMovement()
    {
        return ControlEnabled && EffectiveEnabled ? MoveInput : Vector2.Zero;
    }

    /// <summary>
    /// Current frame movement intent, already normalized and gated by control availability.
    /// Project and plugin movement components read this read-only value instead of the raw input.
    /// </summary>
    public Vector2 MovementIntent
    {
        get
        {
            return ReadMovement();
        }
    }

    protected override void OnDisable()
    {
        BoundController = null;
        MoveInput = Vector2.Zero;
        Status = "Disabled";
        Code = "CONTROL_DISABLED";
    }

    protected override void OnUninitialize()
    {
        BoundController = null;
        MoveInput = Vector2.Zero;
    }

    protected internal override ComponentFrameState CaptureFrameState()
    {
        return new ControllableFrameState(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["entityId"] = Entity.EntityId,
            ["componentId"] = ComponentId,
            ["controllerComponentId"] = BoundController?.ComponentId,
            ["status"] = Status,
            ["code"] = Code,
            ["message"] = Message,
            ["moveInput"] = new JsonArray(MoveInput.X, MoveInput.Y),
        });
    }

    private static int Compare(ControllerComponent Left, ControllerComponent Right)
    {
        var Priority = Right.Priority.CompareTo(Left.Priority);
        return Priority != 0 ? Priority : string.CompareOrdinal(Left.ComponentId, Right.ComponentId);
    }

    internal static Vector2 NormalizeInput(Vector2 Input)
    {
        if (!float.IsFinite(Input.X) || !float.IsFinite(Input.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(Input), "Control input must be finite.");
        }
        var LengthSquared = Input.LengthSquared();
        if (LengthSquared <= 1 || !float.IsFinite(LengthSquared))
        {
            return Input;
        }
        return Vector2.Normalize(Input);
    }
}

/// <summary>Base component for an entity that drives a ControllableComponent.</summary>
public abstract partial class ControllerComponent : SharpComponent
{
    public SharpEntity? ControlledEntity;
    [Range(0, 1000)] public int Priority;

    private ControllableComponent? BoundTarget;
    private Vector2 MoveInput;
    private string Status = "Unbound";
    private string Code = "CONTROLLER_UNBOUND";
    private string? Message;

    public void SetMoveInput(Vector2 Input)
    {
        if (!IsAlive)
        {
            throw new InvalidOperationException("Controller is not alive.");
        }
        World.CheckThread();
        MoveInput = ControllableComponent.NormalizeInput(Input);
    }

    public void ClearMoveInput()
    {
        SetMoveInput(Vector2.Zero);
    }

    protected override void OnUpdate(float DeltaSeconds)
    {
        ResolveTarget();
        if (BoundTarget is null)
        {
            MoveInput = Vector2.Zero;
            return;
        }
        if (!BoundTarget.SubmitMovement(this, MoveInput))
        {
            Status = "Unavailable";
            Code = "CONTROLLER_TARGET_UNAVAILABLE";
            Message = "The controlled entity is disabled or no longer accepts this controller.";
            BoundTarget = null;
            return;
        }
        Status = "Controlling";
        Code = "CONTROLLER_READY";
        Message = null;
    }

    protected override void OnDisable()
    {
        ReleaseTarget();
        Status = "Disabled";
        Code = "CONTROLLER_DISABLED";
    }

    protected override void OnUninitialize()
    {
        ReleaseTarget();
    }

    protected internal override ComponentFrameState CaptureFrameState()
    {
        return new ControllerFrameState(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["entityId"] = Entity.EntityId,
            ["componentId"] = ComponentId,
            ["controllerType"] = GetType().Name,
            ["controlledEntityId"] = ControlledEntity?.EntityId,
            ["targetComponentId"] = BoundTarget?.ComponentId,
            ["status"] = Status,
            ["code"] = Code,
            ["message"] = Message,
            ["moveInput"] = new JsonArray(MoveInput.X, MoveInput.Y),
        });
    }

    private void ResolveTarget()
    {
        if (ControlledEntity is null || !ControlledEntity.IsAlive)
        {
            ReleaseTarget();
            Status = "Unbound";
            Code = ControlledEntity is null ? "CONTROLLER_TARGET_EMPTY" : "CONTROLLER_TARGET_MISSING";
            Message = null;
            return;
        }
        var Target = ControlledEntity.GetComponent<ControllableComponent>();
        if (ReferenceEquals(Target, BoundTarget) && Target is not null && Target.IsAlive)
        {
            return;
        }
        ReleaseTarget();
        if (Target is null)
        {
            Status = "Unavailable";
            Code = "CONTROLLER_TARGET_NOT_CONTROLLABLE";
            Message = "ControlledEntity does not contain engine.controllable.";
            return;
        }
        if (Target.TryBind(this))
        {
            BoundTarget = Target;
            return;
        }
        Status = "Unavailable";
        Code = "CONTROLLER_TARGET_UNAVAILABLE";
        Message = "The controlled entity is disabled or owned by a higher priority controller.";
    }

    private void ReleaseTarget()
    {
        BoundTarget?.Release(this);
        BoundTarget = null;
    }
}

[ComponentTypeId("engine.player-controller"), ComponentSchemaVersion(2), DisallowMultipleComponent,
    AddComponentMenu("Gameplay/Player Controller")]
public sealed partial class PlayerControllerComponent : ControllerComponent
{
}

[ComponentTypeId("engine.ai-controller"), ComponentSchemaVersion(1), DisallowMultipleComponent,
    AddComponentMenu("Gameplay/AI Controller")]
public sealed partial class AiControllerComponent : ControllerComponent
{
}

[ComponentTypeId("engine.movement"), ComponentSchemaVersion(1), DisallowMultipleComponent,
    AddComponentMenu("Gameplay/Movement"), RequireComponent(typeof(TransformComponent)),
    RequireComponent(typeof(ControllableComponent))]
public sealed partial class MovementComponent : SharpComponent
{
    [Range(0, 100)] public float MaxSpeed = 5;
    [Range(0, 200)] public float Acceleration = 20;
    [Range(0, 200)] public float Deceleration = 30;

    private Vector3 Velocity;
    private Vector3 IntentVelocity;
    private Vector3 PendingDisplacement;
    /// <summary>Derived live velocity, finalized after all Evaluate callbacks. Not serialized.</summary>
    public Vector3 LinearVelocity
    {
        get
        {
            return Velocity;
        }
    }
    private string Status = "Idle";
    private string Code = "MOVEMENT_IDLE";
    private string? Message;
    private bool SuppressedByAuthority;

    internal Vector3 AnimationIntentVelocity
    {
        get
        {
            return IntentVelocity;
        }
    }

    protected override void OnEvaluate(float DeltaSeconds)
    {
        if (SuppressedByAuthority)
        {
            // A movement authority owns this entity; keep reporting the override instead of
            // recomputing a displacement that would never be committed.
            IntentVelocity = Vector3.Zero;
            PendingDisplacement = Vector3.Zero;
            Velocity = Vector3.Zero;
            Status = "Overridden";
            Code = "MOVEMENT_OVERRIDDEN";
            return;
        }
        var Controllable = Entity.GetComponent<ControllableComponent>()!;
        var Input = Controllable.ReadMovement();
        var Desired = new Vector3(Input.X, 0, Input.Y) * MaxSpeed;
        var Rate = Desired.LengthSquared() > 0.000001f ? Acceleration : Deceleration;
        if (DeltaSeconds > 0)
        {
            IntentVelocity = MoveTowards(IntentVelocity, Desired, Rate * DeltaSeconds);
            PendingDisplacement = IntentVelocity * DeltaSeconds;
        }
        Status = Velocity.LengthSquared() > 0.000001f ? "Moving" : "Idle";
        Code = Status == "Moving" ? "MOVEMENT_MOVING" : "MOVEMENT_IDLE";
        Message = Controllable.ControlEnabled ? null : "ControllableComponent is disabled by its authoritative state.";
    }

    protected override void OnDisable()
    {
        Velocity = Vector3.Zero;
        IntentVelocity = Vector3.Zero;
        PendingDisplacement = Vector3.Zero;
        Status = "Disabled";
        Code = "MOVEMENT_DISABLED";
        Message = null;
    }

    protected override void OnUninitialize()
    {
        Velocity = Vector3.Zero;
        IntentVelocity = Vector3.Zero;
        PendingDisplacement = Vector3.Zero;
    }

    internal void CommitMovement(bool RootDriven, Vector3 WorldPositionBefore, float DeltaSeconds)
    {
        if (!RootDriven && PendingDisplacement != Vector3.Zero)
        {
            var Transform = Entity.GetComponent<TransformComponent>()!;
            var Displacement = PendingDisplacement;
            if (Entity.Parent is not null)
            {
                var Invertible = SharpEngine.Mathematics.TransformMath.TryInverse(
                    EntityTransformMath.WorldMatrix(Entity.Parent), out var ParentInverse);
                if (!Invertible)
                {
                    throw new InvalidDataException("Movement parent transform must be invertible");
                }
                Displacement = Vector3.TransformNormal(Displacement, ParentInverse);
            }
            Transform.Position += Displacement;
        }
        PendingDisplacement = Vector3.Zero;
        Velocity = DeltaSeconds > 0
            ? (EntityTransformMath.WorldMatrix(Entity).Translation - WorldPositionBefore) / DeltaSeconds
            : Vector3.Zero;
        Status = Velocity.LengthSquared() > 0.000001f ? "Moving" : "Idle";
        Code = Status == "Moving" ? "MOVEMENT_MOVING" : "MOVEMENT_IDLE";
    }

    /// <summary>Another movement authority owns this entity's transform; publish that instead of moving.</summary>
    internal void SuppressForAuthority(bool Suppressed)
    {
        SuppressedByAuthority = Suppressed;
        if (!Suppressed)
        {
            Code = "MOVEMENT_IDLE";
            Status = "Idle";
            Message = null;
            return;
        }
        PendingDisplacement = Vector3.Zero;
        IntentVelocity = Vector3.Zero;
        Velocity = Vector3.Zero;
        Status = "Overridden";
        Code = "MOVEMENT_OVERRIDDEN";
        Message = "A movement authority owns this entity's transform.";
    }

    protected internal override ComponentFrameState CaptureFrameState()
    {
        return new MovementFrameState(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["entityId"] = Entity.EntityId,
            ["componentId"] = ComponentId,
            ["status"] = Status,
            ["code"] = Code,
            ["message"] = Message,
            ["velocity"] = new JsonArray(Velocity.X, Velocity.Y, Velocity.Z),
            ["intentVelocity"] = new JsonArray(IntentVelocity.X, IntentVelocity.Y, IntentVelocity.Z),
        });
    }

    private static Vector3 MoveTowards(Vector3 Current, Vector3 Target, float MaxDelta)
    {
        var Difference = Target - Current;
        var Distance = Difference.Length();
        if (Distance <= MaxDelta || Distance <= 0.000001f)
        {
            return Target;
        }
        return Current + Difference * (MaxDelta / Distance);
    }

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.MaxSpeed < 0 || State.Acceleration <= 0 || State.Deceleration <= 0)
        {
            Diagnostics.Error("MOVEMENT_PARAMETERS", nameof(MaxSpeed), "Movement speed and response values must be nonnegative, with positive acceleration and deceleration.");
        }
    }
}
