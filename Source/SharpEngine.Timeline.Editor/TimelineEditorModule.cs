using SharpEngine.Editor;
using SharpEngine.Modules;

[assembly: SharpEngineModule("SharpEngine.Timeline.Editor", "Editor")]

namespace SharpEngine.Timeline;

public sealed class TimelineEditorModule : ISharpEngineModule
{
    private IDisposable? Registration;

    public void Startup(ModuleContext Context)
    {
        var EditorContext = Context as EditorModuleContext;
        if (EditorContext is not null)
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
