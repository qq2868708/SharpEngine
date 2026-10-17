using SharpEngine.Runtime;

namespace SharpEngine.Editor;

public sealed class EditorModuleContext : ModuleContext
{
    private readonly EditorRuntime _runtime;

    internal EditorModuleContext(string projectRoot, string moduleId,
        EditorRuntime runtime, IInputRuntime input) : base(projectRoot, moduleId,
            "Editor", runtime.UiRuntime, input, runtime)
    {
        _runtime = runtime;
    }

    public EditorRuntime EditorRuntime => _runtime;
    public EditorContext Editor => _runtime.Context;

    public IDisposable RegisterEditorExtension(IEditorExtension extension) =>
        _runtime.RegisterExtension(extension);
}
