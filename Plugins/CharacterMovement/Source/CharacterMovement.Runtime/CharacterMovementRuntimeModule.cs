using SharpEngine.Modules;

[assembly: SharpEngineModule("SharpEngine.CharacterMovement.Runtime", "Runtime")]

namespace SharpEngine.CharacterMovement;

/// <summary>
/// Registers the Character Movement components. They are discovered through the generated component
/// catalog, so this module exists to own the plugin identity and to report its readiness once.
/// </summary>
public sealed class CharacterMovementRuntimeModule : ISharpEngineModule
{
    public void Startup(ModuleContext Context)
    {
        Context.Logger.Info("Character Movement runtime ready: " +
            "character movement, walking and falling modes, jump/sprint/speed-limit modifiers");
    }

    public void Shutdown()
    {
    }
}
