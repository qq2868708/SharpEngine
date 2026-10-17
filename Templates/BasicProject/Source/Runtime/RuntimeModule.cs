using SharpEngine.Modules;

namespace __PROJECT_NAMESPACE__;

public sealed class __PROJECT_NAMESPACE__Module : ISharpEngineModule
{
    private IDisposable? _mainScreenContract;

    public void Startup(ModuleContext context)
    {
        _mainScreenContract = context.Ui?.RegisterViewModel<MainScreenViewModel>(
            "game.main-screen", 1);
    }

    public void Shutdown()
    {
        _mainScreenContract?.Dispose();
        _mainScreenContract = null;
    }
}

public sealed class MainScreenViewModel
{
    public MainScreenState Screen { get; } = new();
    public PlayerHudState Player { get; } = new();
}

public sealed class MainScreenState
{
    public string Title { get; set; } = "__DISPLAY_NAME__";
    public string Status { get; set; } = "Connected";
}

public sealed class PlayerHudState
{
    public int Health { get; set; } = 100;
    public int Energy { get; set; } = 72;
}
