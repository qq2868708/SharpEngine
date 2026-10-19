using System.Numerics;

namespace SharpEngine.Components;

public abstract partial class ColliderComponent : SharpComponent
{
    public Vector3 Center = Vector3.Zero;
    [Range(0, 31)] public int Layer;
    public bool IsTrigger;
    public bool BlocksNavigation = true;
}

[ComponentTypeId("engine.box-collider"), ComponentSchemaVersion(1),
    AddComponentMenu("Collision/Box"), RequireComponent(typeof(TransformComponent))]
public sealed partial class BoxColliderComponent : ColliderComponent
{
    public Vector3 Size = Vector3.One;

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.Size.X <= 0 || State.Size.Y <= 0 || State.Size.Z <= 0)
        {
            Diagnostics.Error("COLLIDER_SIZE", nameof(Size), "Box dimensions must be positive.");
        }
    }
}

[ComponentTypeId("engine.sphere-collider"), ComponentSchemaVersion(1),
    AddComponentMenu("Collision/Sphere"), RequireComponent(typeof(TransformComponent))]
public sealed partial class SphereColliderComponent : ColliderComponent
{
    public float Radius = 0.5f;

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.Radius <= 0)
        {
            Diagnostics.Error("COLLIDER_RADIUS", nameof(Radius), "Sphere radius must be positive.");
        }
    }
}

[ComponentTypeId("engine.capsule-collider"), ComponentSchemaVersion(1),
    AddComponentMenu("Collision/Capsule"), RequireComponent(typeof(TransformComponent))]
public sealed partial class CapsuleColliderComponent : ColliderComponent
{
    public float Radius = 0.5f;
    [Tooltip("Total local Y height, including both hemispheres.")]
    public float Height = 2;

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.Radius <= 0)
        {
            Diagnostics.Error("COLLIDER_RADIUS", nameof(Radius), "Capsule radius must be positive.");
        }
        if (State.Height < 2 * State.Radius)
        {
            Diagnostics.Error("COLLIDER_HEIGHT", nameof(Height), "Capsule height must be at least twice its radius.");
        }
    }
}
