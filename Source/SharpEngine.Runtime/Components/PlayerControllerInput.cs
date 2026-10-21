using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Input;
using SharpEngine.Mathematics;

namespace SharpEngine.Components;

public sealed partial class PlayerControllerComponent
{
    public AssetRef<InputMappingContextDefinition> InputContext = new("engine://input/contexts/player");
    public AssetRef<InputActionDefinition> MoveAction = new("engine://input/actions/player.move");
    public AssetRef<InputActionDefinition> LookAction;
    public CameraComponent? ViewCamera;
    public SkeletalMeshComponent? FacingMesh;
    public bool FaceMoveDirection = true;
    [Range(1, 1800)] public float TurnSpeed = 720;

    private readonly List<IDisposable> InputRegistrations = new();
    private string? InputSignature;
    private Vector2 InputMove;
    private Vector2 InputLook;
    private bool InputBound;
    private string InputCode = "PLAYER_INPUT_UNBOUND";
    private string? InputMessage;

    protected override void OnEnable() { SynchronizeInput(); }

    protected override void OnUpdate(float DeltaSeconds)
    {
        SynchronizeInput();
        if (InputBound && !ReferenceEquals(World.LocalPlayerController, this))
        {
            ClearPlayerInput();
        }
        if (InputBound && ReferenceEquals(World.LocalPlayerController, this))
        {
            var Camera = ViewCamera?.Entity.GetComponent<ThirdPersonCameraComponent>();
            Camera?.AddLookInput(InputLook);
            InputLook = Vector2.Zero;
            var Transform = ControlledEntity?.GetComponent<TransformComponent>();
            var Yaw = Camera?.Yaw ?? 0;
            var Orientation = QuaternionMath.Euler(new Vector3(0, Yaw, 0));
            var Move = Vector3.Transform(new Vector3(InputMove.X, 0, -InputMove.Y), Orientation);
            SetMoveInput(new Vector2(Move.X, Move.Z));
            if (Transform is not null && (!FaceMoveDirection || Move.LengthSquared() > 0.000001f))
            {
                var DesiredYaw = FaceMoveDirection
                    ? MathF.Atan2(-Move.X, -Move.Z) * Mathf.Rad2Deg : Yaw;
                var Desired = QuaternionMath.Euler(new Vector3(0, DesiredYaw, 0));
                var CurrentWorld = EntityTransformMath.WorldMatrix(ControlledEntity!);
                var Representable = TransformMath.TryDecompose(CurrentWorld, out var Position,
                    out var Current, out var Scale);
                if (Representable)
                {
                    var LocalPosition = Transform.Position;
                    var LocalScale = Transform.Scale;
                    var Rotation = FaceMoveDirection
                        ? QuaternionMath.RotateTowards(Current, Desired, TurnSpeed * DeltaSeconds)
                        : Desired;
                    EntityTransformMath.SetWorldMatrix(ControlledEntity!, TransformMath.Trs(Position, Rotation, Scale));
                    Transform.Position = LocalPosition;
                    Transform.Scale = LocalScale;
                }
            }
            if (FacingMesh is not null && FacingMesh.IsAlive && Move.LengthSquared() > 0.000001f)
            {
                var Model = FacingMesh.Entity;
                var ModelTransform = Model.GetComponent<TransformComponent>()!;
                var DesiredYaw = MathF.Atan2(-Move.X, -Move.Z) * Mathf.Rad2Deg;
                var Desired = QuaternionMath.Euler(new Vector3(0, DesiredYaw, 0));
                var CurrentWorld = EntityTransformMath.WorldMatrix(Model);
                var Representable = TransformMath.TryDecompose(CurrentWorld, out var Position,
                    out var Current, out var Scale);
                if (Representable)
                {
                    var LocalPosition = ModelTransform.Position;
                    var LocalScale = ModelTransform.Scale;
                    var Rotation = QuaternionMath.RotateTowards(Current, Desired, TurnSpeed * DeltaSeconds);
                    EntityTransformMath.SetWorldMatrix(Model, TransformMath.Trs(Position, Rotation, Scale));
                    ModelTransform.Position = LocalPosition;
                    ModelTransform.Scale = LocalScale;
                }
            }
        }
        base.OnUpdate(DeltaSeconds);
    }

    protected override void OnEvaluate(float DeltaSeconds) { SynchronizeInput(); }

    internal void ClearPlayerInput()
    {
        InputMove = Vector2.Zero;
        InputLook = Vector2.Zero;
        if (IsAlive)
        {
            ClearMoveInput();
        }
    }

    private void SynchronizeInput()
    {
        var Input = World.Instance.Input;
        var Available = ControlledEntity?.GetComponent<ControllableComponent>();
        var CanControl = Available is not null && Available.ControlEnabled && Available.EffectiveEnabled;
        var Signature = $"{InputContext.Id}|{MoveAction.Id}|{LookAction.Id}|{ViewCamera?.ComponentId}|{FacingMesh?.ComponentId}|{Priority}|{ControlledEntity?.EntityId}|{CanControl}";
        if (Signature == InputSignature)
        {
            return;
        }
        DisposeInput();
        InputSignature = Signature;
        if (!CanControl)
        {
            InputCode = "PLAYER_INPUT_TARGET_UNAVAILABLE";
            return;
        }
        if (Input is null)
        {
            InputCode = "PLAYER_INPUT_SERVICE_UNAVAILABLE";
            return;
        }
        if (InputContext.Id is null || (MoveAction.Id is null && LookAction.Id is null))
        {
            InputCode = "PLAYER_INPUT_UNBOUND";
            return;
        }
        try
        {
            var Context = Input.MappingContexts.SingleOrDefault(Context => Context.Id == InputContext.Id)
                ?? throw new InvalidDataException("Player input context was not registered");
            if (Context.Route != InputRoute.Gameplay)
            {
                throw new InvalidDataException("Player input context must use the Gameplay route");
            }
            void ValidateAction(string? ActionId)
            {
                if (ActionId is null)
                {
                    return;
                }
                var Definition = Input.Actions.SingleOrDefault(Action => Action.Id == ActionId);
                if (Definition?.ValueType != InputActionValueType.Axis2D ||
                    !Context.Bindings.Any(Binding => Binding.ActionId == ActionId))
                {
                    throw new InvalidDataException("Player Move/Look must reference Axis2D actions bound by the selected context");
                }
            }
            ValidateAction(MoveAction.Id);
            ValidateAction(LookAction.Id);
            if (FacingMesh is not null)
            {
                var Ancestor = FacingMesh.Entity.Parent;
                while (Ancestor is not null && Ancestor != ControlledEntity)
                {
                    Ancestor = Ancestor.Parent;
                }
                if (Ancestor is null)
                {
                    throw new InvalidDataException("Player FacingMesh must belong to a child of ControlledEntity");
                }
            }
            if (LookAction.Id is not null &&
                ViewCamera?.Entity.GetComponent<ThirdPersonCameraComponent>() is null)
            {
                throw new InvalidDataException("Player Look requires a third-person ViewCamera");
            }
            InputRegistrations.Add(Input.ActivateContext(Context.Id));
            if (MoveAction.Id is not null)
            {
                InputRegistrations.Add(Input.BindAction(InputRoute.Gameplay, MoveAction.Id, Priority, Event =>
                {
                    if (!AcceptsInput(Event))
                    {
                        return false;
                    }
                    InputMove = Event.Phase == InputActionPhase.Canceled ? Vector2.Zero :
                        new Vector2(Event.Value.X, Event.Value.Y);
                    return true;
                }));
            }
            if (LookAction.Id is not null)
            {
                InputRegistrations.Add(Input.BindAction(InputRoute.Gameplay, LookAction.Id, Priority, Event =>
                {
                    if (!AcceptsInput(Event))
                    {
                        return false;
                    }
                    if (Event.Phase == InputActionPhase.Performed)
                    {
                        InputLook += new Vector2(Event.Value.X, Event.Value.Y);
                    }
                    return true;
                }));
            }
            InputBound = true;
            InputCode = "PLAYER_INPUT_READY";
            InputMessage = null;
        }
        catch (Exception Error) when (Error is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            DisposeInput();
            InputCode = "PLAYER_INPUT_BINDING_INVALID";
            InputMessage = Error.Message;
        }
    }

    private bool AcceptsInput(InputActionEvent Event)
    {
        return IsAlive && Enabled && Entity.ActiveInHierarchy &&
            ControlledEntity is not null && ControlledEntity.IsAlive && ControlledEntity.ActiveInHierarchy &&
            ControlledEntity.GetComponent<ControllableComponent>() is { ControlEnabled: true, EffectiveEnabled: true } &&
            ReferenceEquals(World.LocalPlayerController, this) &&
            Event.ContextId == InputContext.Id &&
            (World.Instance.InputHostId is null || Event.HostId == World.Instance.InputHostId);
    }

    private void DisposeInput()
    {
        for (var Index = InputRegistrations.Count - 1; Index >= 0; Index--)
        {
            InputRegistrations[Index].Dispose();
        }
        InputRegistrations.Clear();
        InputBound = false;
        ClearPlayerInput();
    }

    protected override void OnDisable()
    {
        DisposeInput();
        InputSignature = null;
        base.OnDisable();
    }

    protected override void OnUninitialize()
    {
        DisposeInput();
        base.OnUninitialize();
    }

    protected internal override ComponentFrameState CaptureFrameState()
    {
        var State = base.CaptureFrameState()!.ToJson();
        State["schemaVersion"] = 2;
        State["input"] = new JsonObject
        {
            ["contextId"] = InputContext.Id,
            ["moveActionId"] = MoveAction.Id,
            ["lookActionId"] = LookAction.Id,
            ["hostId"] = World.Instance.InputHostId,
            ["bound"] = InputBound,
            ["selected"] = ReferenceEquals(World.LocalPlayerController, this),
            ["code"] = InputCode,
            ["message"] = InputMessage,
        };
        State["viewCameraEntityId"] = ViewCamera?.Entity.EntityId;
        State["facingMeshComponentId"] = FacingMesh?.ComponentId;
        return new ControllerFrameState(State);
    }

    public static JsonObject Migrate(int Version, JsonObject State, IReadOnlyList<ComponentRecord> Siblings)
    {
        if (Version != 1 || State.Count != 2 ||
            !State.ContainsKey("ControlledEntity") || !State.ContainsKey("Priority"))
        {
            throw new ComponentValidationException("PLAYER_CONTROLLER_MIGRATION", "state", "Only complete Player Controller v1 state can migrate to v2.");
        }
        State["InputContext"] = "engine://input/contexts/player";
        State["MoveAction"] = "engine://input/actions/player.move";
        State["LookAction"] = null;
        State["ViewCamera"] = null;
        State["FacingMesh"] = null;
        State["FaceMoveDirection"] = true;
        State["TurnSpeed"] = 720;
        return State;
    }
}
