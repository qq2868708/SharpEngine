namespace SharpEngine.Components;

[ComponentTypeId("engine.sky-light"), ComponentSchemaVersion(1),
    AddComponentMenu("Rendering/Sky Light")]
public sealed partial class SkyLightComponent : SharpComponent
{
    public Color Color = Color.White;
    public float Intensity = 1;

    public static void Validate(in GeneratedState State,
        ComponentDiagnosticSink Diagnostics)
    {
        if (State.Intensity < 0)
        {
            Diagnostics.Error("SKY_LIGHT_INTENSITY_NEGATIVE", nameof(Intensity),
                "Sky Light intensity must be nonnegative");
        }
    }
}
