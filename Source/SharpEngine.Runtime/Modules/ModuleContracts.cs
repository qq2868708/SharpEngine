using SharpEngine.Components;
using SharpEngine.UI;
using SharpEngine.Input;
using SharpEngine.Logging;

namespace SharpEngine.Modules;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class SharpEngineModuleAttribute(string id, string type) : Attribute
{
    public string Id { get; } = string.IsNullOrWhiteSpace(id)
        ? throw new ArgumentException("A module id is required", nameof(id))
        : id.Trim();

    public string Type { get; } = type is "Runtime" or "Editor"
        ? type
        : throw new ArgumentException("Module type must be Runtime or Editor", nameof(type));
}

public interface ISharpEngineModule
{
    void Startup(ModuleContext context);
    void Shutdown();
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
        Logger = SharpLog.For(moduleId);
    }

    public string ProjectRoot { get; }
    public string ModuleId { get; }
    public string ModuleType { get; }
    public IUiRuntime? Ui { get; }
    public IInputRuntime? Input { get; }
    public IComponentRuntime? Components { get; }
    public SharpEngine.Subsystems.ISubsystemRuntime? Subsystems
    {
        get
        {
            return Components as SharpEngine.Subsystems.ISubsystemRuntime;
        }
    }
    /// <summary>Structured logger scoped to this module id.</summary>
    public ISharpLogger Logger { get; }
}

