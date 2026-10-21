using SharpEngine.Components;

namespace SharpEngine.CharacterMovement;

/// <summary>
/// Applies a queued upward impulse from the ground. Runs before the mode, so the airborne mode
/// integrates the resulting arc; a request is consumed once and never repeats implicitly.
/// </summary>
[ComponentTypeId("c58a7e13-2f46-4b90-8d7a-5e3b9f16c043"), ComponentSchemaVersion(1),
    AddComponentMenu("Gameplay/Character Movement/Jump Modifier"),
    RequireComponent(typeof(TransformComponent)), RequireComponent(typeof(ControllableComponent))]
public sealed partial class JumpMovementModifierComponent : SharpComponent, ICharacterMovementModifier
{
    [Range(0, 40)] public float JumpSpeed = 5;
    public int Priority = 0;

    private bool JumpRequested;

    int ICharacterMovementModifier.Priority
    {
        get
        {
            return Priority;
        }
    }

    /// <summary>Queues one jump. The next grounded frame consumes it; an airborne request is discarded.</summary>
    public void RequestJump()
    {
        World.CheckThread();
        JumpRequested = true;
    }

    public bool HasPendingJump
    {
        get
        {
            return JumpRequested;
        }
    }

    public void Modify(CharacterMovementFrame Frame, in CharacterMovementSettings Settings)
    {
        if (!JumpRequested)
        {
            return;
        }
        if (!Frame.Grounded || Frame.VerticalVelocity > 0 || JumpSpeed <= 0)
        {
            JumpRequested = false;
            Frame.Code = "CHARACTER_MOVEMENT_JUMP_REJECTED";
            Frame.Message = "A jump request only applies while the character is standing on the ground.";
            return;
        }
        JumpRequested = false;
        Frame.VerticalVelocity = JumpSpeed;
        Frame.Grounded = false;
        Frame.Code = "CHARACTER_MOVEMENT_JUMPED";
    }

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.JumpSpeed < 0)
        {
            Diagnostics.Error("CHARACTER_MOVEMENT_JUMP_PARAMETERS", nameof(JumpSpeed),
                "Jump speed must be nonnegative.");
        }
        if (State.Priority < 0)
        {
            Diagnostics.Error("CHARACTER_MOVEMENT_JUMP_PRIORITY", nameof(Priority),
                "Jump must run before speed caps, so its priority cannot be negative.");
        }
    }
}

/// <summary>Multiplies planar intent and speed while sprinting is enabled, so sprinting stays data-driven.</summary>
[ComponentTypeId("0e9b5d76-4a13-42cf-9b68-d27f4a8e2154"), ComponentSchemaVersion(1),
    AddComponentMenu("Gameplay/Character Movement/Sprint Modifier"),
    RequireComponent(typeof(TransformComponent)), RequireComponent(typeof(ControllableComponent))]
public sealed partial class SprintMovementModifierComponent : SharpComponent, ICharacterMovementModifier
{
    [Range(1, 4)] public float SpeedMultiplier = 1.75f;
    public int Priority = 50;

    // Session state: gameplay code enables sprint; it is never persisted with the scene.
    internal bool SprintEnabled = true;
    private bool Sprinting;

    int ICharacterMovementModifier.Priority
    {
        get
        {
            return Priority;
        }
    }

    /// <summary>Enables or disables sprinting for the live session without touching authoritative state.</summary>
    public void SetSprinting(bool Enabled)
    {
        World.CheckThread();
        SprintEnabled = Enabled;
        if (!Enabled)
        {
            Sprinting = false;
        }
    }

    public bool IsSprinting
    {
        get
        {
            return Sprinting;
        }
    }

    public void Modify(CharacterMovementFrame Frame, in CharacterMovementSettings Settings)
    {
        Sprinting = SprintEnabled && SpeedMultiplier > 1 && Frame.IntentMagnitude > 0;
        if (!Sprinting)
        {
            return;
        }
        Frame.SpeedMultiplier *= SpeedMultiplier;
        Frame.Code = "CHARACTER_MOVEMENT_SPRINTING";
    }

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.SpeedMultiplier < 1 || State.SpeedMultiplier > 4)
        {
            Diagnostics.Error("CHARACTER_MOVEMENT_SPRINT_PARAMETERS", nameof(SpeedMultiplier),
                "The sprint multiplier must stay between 1 and 4.");
        }
    }
}

/// <summary>Caps absolute planar speed regardless of the active mode.</summary>
[ComponentTypeId("5c1f8a74-9d26-4e38-b1a5-8f2d6c47e065"), ComponentSchemaVersion(1),
    DisallowMultipleComponent, AddComponentMenu("Gameplay/Character Movement/Speed Limit Modifier"),
    RequireComponent(typeof(TransformComponent)), RequireComponent(typeof(ControllableComponent))]
public sealed partial class CharacterSpeedLimitModifierComponent : SharpComponent, ICharacterMovementModifier
{
    [Range(0, 40)] public float MaxPlanarSpeed = 6;
    public int Priority = 100;

    int ICharacterMovementModifier.Priority
    {
        get
        {
            return Priority;
        }
    }

    public void Modify(CharacterMovementFrame Frame, in CharacterMovementSettings Settings)
    {
        var Limit = MathF.Max(0, MaxPlanarSpeed);
        Frame.SpeedCap = Frame.SpeedCap > 0 ? MathF.Min(Frame.SpeedCap, Limit) : Limit;
    }

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.MaxPlanarSpeed < 0)
        {
            Diagnostics.Error("CHARACTER_MOVEMENT_SPEED_LIMIT", nameof(MaxPlanarSpeed),
                "The planar speed limit must be nonnegative.");
        }
    }
}
