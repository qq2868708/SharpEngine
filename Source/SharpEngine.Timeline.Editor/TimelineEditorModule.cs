using SharpEngine.Editor;
using SharpEngine.Modules;

[assembly: SharpEngineModule("SharpEngine.Timeline.Editor", "Editor")]

namespace SharpEngine.Timeline;

public sealed class TimelineEditorModule : ISharpEngineModule
{
    private IDisposable? Registration;

    public void Startup(ModuleContext context)
    {
        if (context is EditorModuleContext EditorContext)
        {
            Registration = EditorContext.RegisterEditorExtension(new TimelineEditorExtension());
        }
    }

    public void Shutdown()
    {
        Registration?.Dispose();
        Registration = null;
    }
}
