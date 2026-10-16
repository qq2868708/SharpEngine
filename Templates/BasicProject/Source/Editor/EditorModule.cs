using SharpEngine.Modules;
using SharpEngine.Editor;

namespace __PROJECT_NAMESPACE__.Editor;

public sealed class __PROJECT_NAMESPACE__EditorModule : ISharpEngineModule
{
    private IDisposable? _extension;

    public void Startup(ModuleContext context)
    {
        if (context is not EditorModuleContext editor)
            throw new InvalidOperationException("Editor Module requires EditorModuleContext");
        _extension = editor.RegisterEditorExtension(new ProjectEditorExtension());
    }

    public void Shutdown()
    {
        _extension?.Dispose();
        _extension = null;
    }
}
