using SharpEngine.Runtime;

namespace __PROJECT_NAMESPACE__;

[DevelopmentSettings("__PROJECT_ID__.gameplay", SettingsScope.Project, 1,
    DisplayName = "Gameplay", Category = "Game")]
public sealed class GameDevelopmentSettings : DevelopmentSettings
{
    [Setting("moveSpeed", DisplayName = "Move Speed", Minimum = 0,
        Description = "Default world-space movement speed.")]
    public float MoveSpeed { get; init; } = 600.0f;
}
