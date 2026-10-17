using System.Numerics;

namespace SharpEngine.Components;

[ComponentTypeId("engine.transform"), ComponentSchemaVersion(1), DisallowMultipleComponent]
public sealed partial class TransformComponent : SharpComponent
{
    public Vector3 Position = Vector3.Zero;
    public Vector3 Rotation = Vector3.Zero;
    public Vector3 Scale = Vector3.One;
}
