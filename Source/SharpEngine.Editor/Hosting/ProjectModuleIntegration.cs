using SharpEngine.Runtime;

namespace SharpEngine.Editor.Hosting;

internal static class ProjectModuleIntegration
{
    internal static ProjectModuleHost? Load(EditorRuntime runtime, IInputRuntime input)
    {
        var host = ProjectModuleHost.Prepare(typeof(EditorRuntime).Assembly);
        if (host is null) return null;
        EditorRuntime.ModuleReloadPreparation? preparation = null;
        try
        {
            preparation = runtime.PrepareModuleReload(host.Assemblies);
            preparation.Commit();
            Start(host, runtime, input);
            runtime.SetProjectCodeAvailable(true, "Project code loaded");
            preparation.Accept();
            return host;
        }
        catch { host.Dispose(); throw; }
        finally { preparation?.Dispose(); }
    }

    internal static void Start(ProjectModuleHost host, EditorRuntime runtime, IInputRuntime input)
    {
        var root = Path.GetDirectoryName(Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST"))!;
        host.Start((id, type) => type == "Editor"
            ? new EditorModuleContext(root, id, runtime, input)
            : new ModuleContext(root, id, type, runtime.UiRuntime, input, runtime));
    }
}
