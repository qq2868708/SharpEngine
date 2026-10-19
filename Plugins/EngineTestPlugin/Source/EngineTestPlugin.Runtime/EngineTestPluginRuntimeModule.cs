using SharpEngine.Modules;

[assembly: SharpEngineModule("SharpEngine.Test.EnginePlugin.Runtime", "Runtime")]

namespace SharpEngine.Test.EnginePlugin;

public sealed class EngineTestPluginRuntimeModule : ISharpEngineModule
{
    public void Startup(ModuleContext context)
    {
    }

    public void Shutdown()
    {
    }
}
