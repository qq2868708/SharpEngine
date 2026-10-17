using SharpEngine.Runtime;

[assembly: SharpEngineModule("SharpEngine.Test.ProjectPlugin.Runtime", "Runtime")]

namespace SharpEngine.Test.ProjectPlugin;

public sealed class ProjectTestPluginRuntimeModule : ISharpEngineModule
{
    public void Startup(ModuleContext context)
    {
    }

    public void Shutdown()
    {
    }
}
