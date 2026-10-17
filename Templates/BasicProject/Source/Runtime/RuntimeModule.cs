using SharpEngine.Runtime;

namespace __PROJECT_NAMESPACE__;

public sealed class __PROJECT_NAMESPACE__Module : ISharpEngineModule
{
    private IDisposable? _mainScreenContract;
    private IDisposable? _moveInput;
    private IDisposable? _primaryInput;

    public void Startup(ModuleContext context)
    {
        _mainScreenContract = context.Ui?.RegisterViewModel<MainScreenViewModel>(
            "game.main-screen", 1);
        _moveInput = context.Input?.BindAction(InputRoute.Gameplay,
            "project://input/actions/gameplay.move", 0, input =>
            {
                // Consume the shared Action here; connect Performed values to your gameplay.
                if (input.Phase != InputActionPhase.Performed)
                    Console.WriteLine($"[Input] Move {input.Phase}: {input.Value.X}, {input.Value.Y}");
                return true;
            });
        _primaryInput = context.Input?.BindAction(InputRoute.Gameplay,
            "project://input/actions/gameplay.primary", 0, input =>
            {
                if (input.Phase == InputActionPhase.Performed)
                    Console.WriteLine("[Input] Primary");
                return true;
            });
    }

    public void Shutdown()
    {
        _primaryInput?.Dispose();
        _primaryInput = null;
        _moveInput?.Dispose();
        _moveInput = null;
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
