namespace SharpEngine.Modules;

public interface ISharpEngineModule
{
    void Startup(ModuleContext context);
    void Shutdown();
}

public class ModuleContext
{
    protected internal ModuleContext(string projectRoot, string moduleId,
        string moduleType)
    {
        ProjectRoot = projectRoot;
        ModuleId = moduleId;
        ModuleType = moduleType;
    }

    public string ProjectRoot { get; }
    public string ModuleId { get; }
    public string ModuleType { get; }
}
