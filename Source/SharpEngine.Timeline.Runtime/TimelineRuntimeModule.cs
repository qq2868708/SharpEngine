using SharpEngine.Runtime;

[assembly: SharpEngineModule("SharpEngine.Timeline.Runtime", "Runtime")]

namespace SharpEngine.Timeline;

public sealed class TimelineRuntimeModule : ISharpEngineModule
{
    public void Startup(ModuleContext context)
    {
    }

    public void Shutdown()
    {
    }
}
