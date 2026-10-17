using SharpEngine.Runtime;

[assembly: SharpEngineModule("SharpEngine.Test.ProjectPlugin.Editor", "Editor")]

namespace SharpEngine.Test.ProjectPlugin;

public sealed class ProjectTestPluginEditorModule : ISharpEngineModule
{
    public void Startup(ModuleContext context)
    {
    }

    public void Shutdown()
    {
    }
}
