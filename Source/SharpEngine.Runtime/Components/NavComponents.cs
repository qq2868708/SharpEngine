using System.Numerics;

namespace SharpEngine.Components;

[ComponentTypeId("engine.nav-surface"), ComponentSchemaVersion(1), DisallowMultipleComponent,
    AddComponentMenu("Navigation/Surface"), RequireComponent(typeof(TransformComponent))]
public sealed partial class NavSurfaceComponent : SharpComponent
{
    [Tooltip("Walkable rectangle on the local XZ plane, centered on the entity origin.")]
    public Vector2 Size = new(20, 20);
    public float CellSize = 1;
    public uint ObstacleMask = uint.MaxValue;

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.Size.X <= 0 || State.Size.Y <= 0 || State.CellSize <= 0)
        {
            Diagnostics.Error("NAV_GRID_SIZE", nameof(CellSize), "Surface dimensions and cell size must be positive.");
        }
        else if (Math.Ceiling(State.Size.X / State.CellSize) * Math.Ceiling(State.Size.Y / State.CellSize) > 16384)
        {
            Diagnostics.Error("NAV_GRID_BUDGET", nameof(CellSize), "A navigation surface supports at most 16384 cells.");
        }
    }
}

public enum NavAgentStatus
{
    Idle,
    Moving,
    Arrived,
    Blocked,
    Unavailable,
}

[ComponentTypeId("engine.nav-agent"), ComponentSchemaVersion(1), DisallowMultipleComponent,
    AddComponentMenu("Navigation/Agent"), RequireComponent(typeof(TransformComponent))]
public sealed partial class NavAgentComponent : SharpComponent
{
    public NavSurfaceComponent? Surface;
    [Tooltip("World-space capsule radius; the entity origin is at its feet.")]
    public float Radius = 0.3f;
    public float Height = 1.8f;
    public float Speed = 3;
    public float StoppingDistance = 0.05f;
    public Vector3 Destination = Vector3.Zero;
    public bool HasDestination;

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.Radius <= 0 || State.Height < 2 * State.Radius)
        {
            Diagnostics.Error("NAV_AGENT_SHAPE", nameof(Height), "Agent requires a positive radius and height >= 2 * radius.");
        }
        if (State.Speed < 0 || State.StoppingDistance < 0)
        {
            Diagnostics.Error("NAV_AGENT_MOVEMENT", nameof(Speed), "Speed and stopping distance must be nonnegative.");
        }
    }
}
