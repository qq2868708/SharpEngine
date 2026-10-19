using SharpEngine.Modules;

[assembly: SharpEngineModule("SharpEngine.Networking", "Runtime")]

namespace SharpEngine.Networking;

public sealed class NetworkingModule : ISharpEngineModule
{
    public void Startup(ModuleContext Context)
    {
    }

    public void Shutdown()
    {
    }
}
