using System.Numerics;
using SharpEngine.Components;
using SharpEngine.Mathematics;

namespace SharpEngine.CharacterMovement;

/// <summary>
/// Base declaration for one locomotion mode. A character runs exactly the enabled mode whose
/// <see cref="ModeId"/> matches its grounded or airborne configuration, so modes are swapped by data.
/// </summary>
public abstract partial class CharacterMovementModeComponent : SharpComponent, ICharacterMovementMode
{
    public string ModeId
    {
        get
        {
            return CharacterMovementModes.ModeIdOf(GetType()) ??
                throw new InvalidOperationException("A movement mode requires CharacterMovementModeId");
        }
    }

    public abstract void Step(CharacterMovementFrame Frame, in CharacterMovementSettings Settings);
}

/// <summary>Grounded locomotion: accelerates toward planar intent and brakes toward rest.</summary>
[ComponentTypeId("1b7e9c42-5d38-4a06-8f2b-73e1c9d45b21"), ComponentSchemaVersion(1), CharacterMovementModeId("walking"),
    DisallowMultipleComponent, AddComponentMenu("Gameplay/Character Movement/Walking Mode"),
    RequireComponent(typeof(TransformComponent)), RequireComponent(typeof(ControllableComponent))]
public sealed partial class WalkingMovementModeComponent : CharacterMovementModeComponent
{
    [Range(0, 40)] public float MaxSpeed = 6;
    [Range(0, 200)] public float Acceleration = 20;
    [Range(0, 200)] public float Braking = 25;

    private Vector3 PlanarVelocity;


    public override void Step(CharacterMovementFrame Frame, in CharacterMovementSettings Settings)
    {
        if (!Frame.Grounded)
        {
            // Falling owns airborne integration; the grounded mode only reports the hand-off.
            Frame.Code = "CHARACTER_MOVEMENT_AIRBORNE";
            return;
        }
        Frame.VerticalVelocity = 0;
        var Limit = MathF.Max(0, MaxSpeed) * MathF.Max(0, Frame.SpeedMultiplier);
        if (Frame.SpeedCap > 0)
        {
            Limit = MathF.Min(Limit, Frame.SpeedCap);
        }
        Frame.MaxSpeed = Limit;
        var Desired = Frame.IntentDirection * (Limit * Math.Clamp(Frame.IntentMagnitude, 0, 1));
        var Rate = Desired.LengthSquared() > 0.000001f ? Acceleration : Braking;
        PlanarVelocity = MoveTowards(PlanarVelocity, Desired, MathF.Max(0, Rate) * Frame.DeltaSeconds);
        var Displacement = PlanarVelocity * Frame.DeltaSeconds;
        if (Displacement.LengthSquared() > 0.000001f)
        {
            Frame.Code = "CHARACTER_MOVEMENT_WALKING";
            Frame.Displacement += Displacement;
        }
        else
        {
            Frame.Code = "CHARACTER_MOVEMENT_IDLE";
        }
    }

    internal Vector3 GroundVelocity
    {
        get
        {
            return PlanarVelocity;
        }
    }

    protected override void OnDisable()
    {
        PlanarVelocity = Vector3.Zero;
    }

    protected override void OnUninitialize()
    {
        PlanarVelocity = Vector3.Zero;
    }

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.MaxSpeed < 0 || State.Acceleration < 0 || State.Braking < 0)
        {
            Diagnostics.Error("CHARACTER_MOVEMENT_WALKING_PARAMETERS", nameof(MaxSpeed),
                "Walking speed, acceleration, and braking must be nonnegative.");
        }
    }

    private static Vector3 MoveTowards(Vector3 Current, Vector3 Target, float MaxDelta)
    {
        var Difference = Target - Current;
        var Distance = Difference.Length();
        if (Distance <= MaxDelta || Distance <= 0.000001f)
        {
            return Target;
        }
        return Current + Difference / Distance * MaxDelta;
    }
}

/// <summary>Airborne locomotion: gravity, terminal velocity, and bounded air control.</summary>
[ComponentTypeId("a3d24f68-7c19-4e85-b027-6f9d1e3c8a32"), ComponentSchemaVersion(1), CharacterMovementModeId("falling"),
    DisallowMultipleComponent, AddComponentMenu("Gameplay/Character Movement/Falling Mode"),
    RequireComponent(typeof(TransformComponent)), RequireComponent(typeof(ControllableComponent))]
public sealed partial class FallingMovementModeComponent : CharacterMovementModeComponent
{
    [Range(0, 100)] public float Gravity = 9.81f;
    [Range(0, 200)] public float TerminalVelocity = 50;
    [Range(0, 1)] public float AirControl = 0.25f;
    [Range(0, 40)] public float MaxAirSpeed = 6;

    private Vector3 PlanarVelocity;


    public override void Step(CharacterMovementFrame Frame, in CharacterMovementSettings Settings)
    {
        var Limit = MathF.Max(0, MaxAirSpeed) * MathF.Max(0, Frame.SpeedMultiplier);
        if (Frame.SpeedCap > 0)
        {
            Limit = MathF.Min(Limit, Frame.SpeedCap);
        }
        Frame.MaxSpeed = Limit;
        var Desired = Frame.IntentDirection * (Limit * Math.Clamp(Frame.IntentMagnitude, 0, 1));
        var Control = Math.Clamp(AirControl, 0, 1) * Frame.DeltaSeconds * 4;
        PlanarVelocity += (Desired - PlanarVelocity) * Control;
        var Vertical = Frame.VerticalVelocity - MathF.Max(0, Gravity) * Frame.DeltaSeconds;
        Vertical = MathF.Max(Vertical, -MathF.Max(0, TerminalVelocity));
        Frame.VerticalVelocity = Vertical;
        Frame.Displacement += new Vector3(PlanarVelocity.X, Vertical, PlanarVelocity.Z) * Frame.DeltaSeconds;
        Frame.Code = "CHARACTER_MOVEMENT_FALLING";
    }

    internal Vector3 AirVelocity
    {
        get
        {
            return PlanarVelocity;
        }
    }

    protected override void OnDisable()
    {
        PlanarVelocity = Vector3.Zero;
    }

    protected override void OnUninitialize()
    {
        PlanarVelocity = Vector3.Zero;
    }

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.Gravity < 0 || State.TerminalVelocity < 0 || State.AirControl < 0 ||
            State.AirControl > 1 || State.MaxAirSpeed < 0)
        {
            Diagnostics.Error("CHARACTER_MOVEMENT_FALLING_PARAMETERS", nameof(Gravity),
                "Gravity, terminal velocity, air control (0..1), and air speed must be inside their ranges.");
        }
    }
}
