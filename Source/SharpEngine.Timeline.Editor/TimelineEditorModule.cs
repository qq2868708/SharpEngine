using SharpEngine.Modules;

[assembly: SharpEngineModule("SharpEngine.Timeline.Editor", "Editor")]

namespace SharpEngine.Timeline;

public sealed class TimelineEditorModule : ISharpEngineModule
{
    public void Startup(ModuleContext context)
    {
    }

    public void Shutdown()
    {
    }
}
