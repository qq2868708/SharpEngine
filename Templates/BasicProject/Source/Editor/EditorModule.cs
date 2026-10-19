using SharpEngine.Runtime;
using SharpEngine.Editor;

namespace __PROJECT_NAMESPACE__.Editor;

public sealed class __PROJECT_NAMESPACE__EditorModule : ISharpEngineModule
{
    private IDisposable? Extension;

    public void Startup(ModuleContext Context)
    {
        var Editor = Context as EditorModuleContext;
        if (Editor is null)
        {
            throw new InvalidOperationException("Editor Module requires EditorModuleContext");
        }
        Extension = Editor.RegisterEditorExtension(new ProjectEditorExtension());
    }

    public void Shutdown()
    {
        Extension?.Dispose();
        Extension = null;
    }
}
