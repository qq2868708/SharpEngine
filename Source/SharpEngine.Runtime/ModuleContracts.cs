namespace SharpEngine.Runtime;

public interface ISharpEngineModule
{
    void Startup(ModuleContext context);
    void Shutdown();
}

public sealed record UiViewModelContract(string Id, int Version,
    string RuntimeType, string JsonSchema)
{
    public string Key => $"{Id}@{Version}";
}

public interface IUiRuntime
{
    IReadOnlyList<UiViewModelContract> ViewModelContracts { get; }
    IDisposable RegisterViewModel<T>(string id, int version) where T : class;
}

public class ModuleContext
{
    protected internal ModuleContext(string projectRoot, string moduleId,
        string moduleType, IUiRuntime? ui = null, IInputRuntime? input = null, IComponentRuntime? components = null)
    {
        ProjectRoot = projectRoot;
        ModuleId = moduleId;
        ModuleType = moduleType;
        Ui = ui;
        Input = input;
        Components = components;
    }

    public string ProjectRoot { get; }
    public string ModuleId { get; }
    public string ModuleType { get; }
    public IUiRuntime? Ui { get; }
    public IInputRuntime? Input { get; }
    public IComponentRuntime? Components { get; }
}

public interface IComponentRuntime
{
    SharpEngine.Components.RuntimeWorld? RuntimeWorld { get; }
}
