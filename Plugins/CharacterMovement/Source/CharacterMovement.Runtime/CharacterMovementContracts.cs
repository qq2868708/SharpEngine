using System.Numerics;
using System.Reflection;

namespace SharpEngine.CharacterMovement;

/// <summary>Stable mode identity declared on the mode component type.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CharacterMovementModeIdAttribute(string id) : Attribute
{
    public string Id { get; } = string.IsNullOrWhiteSpace(id)
        ? throw new ArgumentException("A movement mode id is required", nameof(id))
        : id.Trim();
}

/// <summary>
/// Immutable per-frame snapshot of the authoritative configuration. Modes and modifiers read this
/// instead of the component, so one mode implementation can be shared by any character.
/// </summary>
public readonly record struct CharacterMovementSettings(
    float InputDeadZone,
    float GroundProbeDistance,
    float GroundMinNormalY,
    int GroundLayerMask,
    bool ApplyRootMotion,
    float RootMotionScale,
    bool ApplyRootMotionRotation,
    bool RootMotionRequiresIntent);

/// <summary>
/// Mutable working set for one movement frame. Ordered modifiers run first and may change intent,
/// the speed cap, or the vertical velocity. The selected mode then integrates and owns the
/// horizontal velocity, and the authority commits the resulting displacement at the frame end.
/// </summary>
public sealed class CharacterMovementFrame
{
    public CharacterMovementFrame(Vector3 WorldPosition, Vector3 WorldRotationDegrees)
    {
        this.WorldPosition = WorldPosition;
        this.WorldRotationDegrees = WorldRotationDegrees;
    }

    /// <summary>Normalized world-space planar intent direction. Zero when there is no intent.</summary>
    public Vector3 IntentDirection
    {
        get;
        set;
    }

    /// <summary>Planar intent magnitude in the 0..1 range.</summary>
    public float IntentMagnitude
    {
        get;
        set;
    }

    /// <summary>Vertical velocity carried between frames by the movement authority.</summary>
    public float VerticalVelocity
    {
        get;
        set;
    }

    public Vector3 WorldPosition
    {
        get;
    }

    public Vector3 WorldRotationDegrees
    {
        get;
    }

    public bool Grounded
    {
        get;
        set;
    } = true;

    /// <summary>Absolute planar speed cap requested by modifiers. Zero means "no absolute cap".</summary>
    public float SpeedCap
    {
        get;
        set;
    }

    /// <summary>Multiplier applied by modifiers to the mode's own speed. Defaults to 1.</summary>
    public float SpeedMultiplier
    {
        get;
        set;
    } = 1;

    /// <summary>Planar speed resolved by the active mode for this frame.</summary>
    public float MaxSpeed
    {
        get;
        set;
    }

    /// <summary>World-space displacement this frame, including root motion. Committed by the authority.</summary>
    public Vector3 Displacement
    {
        get;
        set;
    }

    public float DeltaSeconds
    {
        get;
        internal set;
    }

    public string Code
    {
        get;
        set;
    } = "CHARACTER_MOVEMENT_READY";

    public string? Message
    {
        get;
        set;
    }
}

/// <summary>
/// A movement mode owns integration for one locomotion state, such as walking or falling.
/// Exactly one enabled mode with the requested <see cref="ModeId"/> runs for a frame.
/// </summary>
public interface ICharacterMovementMode
{
    string ModeId { get; }

    void Step(CharacterMovementFrame Frame, in CharacterMovementSettings Settings);
}

/// <summary>
/// A composable adjustment applied in ascending <see cref="Priority"/> order, then by component id.
/// Modifiers run after the intent is known and before the mode integrates.
/// </summary>
public interface ICharacterMovementModifier
{
    int Priority { get; }

    void Modify(CharacterMovementFrame Frame, in CharacterMovementSettings Settings);
}

/// <summary>Reads <see cref="CharacterMovementModeIdAttribute"/> without instantiating a mode.</summary>
public static class CharacterMovementModes
{
    public static string? ModeIdOf(Type ModeType)
    {
        ArgumentNullException.ThrowIfNull(ModeType);
        return ModeType.GetCustomAttribute<CharacterMovementModeIdAttribute>()?.Id;
    }
}
