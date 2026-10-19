using SharpEngine.Input;
using SharpEngine.Modules;

namespace SharpEngine.Editor.Hosting;

internal static class ProjectModuleIntegration
{
    internal static ProjectModuleHost? Load(EditorRuntime Runtime, IInputRuntime Input,
        Action<int, string, string>? ReportProgress = null)
    {
        ReportProgress?.Invoke(78, "ProjectModules", "Verifying published project and plugin assemblies");
        var Host = ProjectModuleHost.Prepare((Id, Index, Count) =>
            ReportProgress?.Invoke(79 + Index * 6 / Count, Id, "Loading project module assembly"),
            typeof(EditorRuntime).Assembly);
        if (Host is null)
        {
            return null;
        }
        EditorRuntime.ModuleReloadPreparation? Preparation = null;
        try
        {
            ReportProgress?.Invoke(85, "Components", "Registering project component types");
            Preparation = Runtime.PrepareModuleReload(Host.Assemblies);
            Preparation.Commit();
            Start(Host, Runtime, Input, (Id, Index, Count) =>
                ReportProgress?.Invoke(87 + Index * 7 / Count, Id, "Starting project module"));
            Runtime.SetProjectCodeAvailable(true, "Project code loaded");
            Preparation.Accept();
            return Host;
        }
        catch
        {
            Host.Dispose();
            throw;
        }
        finally
        {
            Preparation?.Dispose();
        }
    }

    internal static void Start(ProjectModuleHost Host, EditorRuntime Runtime, IInputRuntime Input,
        Action<string, int, int>? OnStarting = null)
    {
        var Root = Path.GetDirectoryName(Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST"))!;
        Host.Start((Id, Type) => Type == "Editor"
            ? new EditorModuleContext(Root, Id, Runtime, Input)
            : new ModuleContext(Root, Id, Type, Runtime.UiRuntime, Input, Runtime), OnStarting);
    }
}
