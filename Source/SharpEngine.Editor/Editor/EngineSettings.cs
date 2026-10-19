using SharpEngine.Settings;

namespace SharpEngine.Editor;

[DevelopmentSettings("sharpengine.editor-navigation", SettingsScope.Engine, 1,
    DisplayName = "Navigation", Category = "Editor", Order = 100)]
public sealed class EngineSettings : DevelopmentSettings
{
    [Setting("invertYAxis", DisplayName = "Invert Y Axis",
        Description = "Reverse vertical viewport orbit input.", Order = 100)]
    public bool InvertYAxis { get; init; }
}
