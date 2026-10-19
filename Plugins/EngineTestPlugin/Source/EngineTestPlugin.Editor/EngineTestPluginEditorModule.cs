using SharpEngine.Modules;

[assembly: SharpEngineModule("SharpEngine.Test.EnginePlugin.Editor", "Editor")]

namespace SharpEngine.Test.EnginePlugin;

public sealed class EngineTestPluginEditorModule : ISharpEngineModule
{
    public void Startup(ModuleContext context)
    {
    }

    public void Shutdown()
    {
    }
}
