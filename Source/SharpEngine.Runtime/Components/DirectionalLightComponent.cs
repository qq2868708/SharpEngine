namespace SharpEngine.Components;

[ComponentTypeId("engine.directional-light"), ComponentSchemaVersion(1), AddComponentMenu("Rendering/Directional Light")]
public sealed partial class DirectionalLightComponent : SharpComponent
{
    public Color Color = Color.White;
    public float Intensity = 1;
}
