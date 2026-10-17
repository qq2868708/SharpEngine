namespace SharpEngine.Components;

[ComponentTypeId("engine.camera"), ComponentSchemaVersion(1), AddComponentMenu("Rendering/Camera")]
public sealed partial class CameraComponent : SharpComponent
{
    [Range(1, 179)] public float FieldOfView = 60;
    public float NearPlane = 0.1f;
    public float FarPlane = 1000;
}
