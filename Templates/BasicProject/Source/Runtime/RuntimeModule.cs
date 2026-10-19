using SharpEngine.Input;
using SharpEngine.Modules;

namespace __PROJECT_NAMESPACE__;

public sealed class __PROJECT_NAMESPACE__Module : ISharpEngineModule
{
    private IDisposable? MainScreenContract;
    private IDisposable? MoveInput;
    private IDisposable? PrimaryInput;

    public void Startup(ModuleContext Context)
    {
        MainScreenContract = Context.Ui?.RegisterViewModel<MainScreenViewModel>(
            "game.main-screen", 1);
        MoveInput = Context.Input?.BindAction(InputRoute.Gameplay,
            "project://input/actions/gameplay.move", 0, Input =>
            {
                // Consume the shared Action here; connect Performed values to your gameplay.
                if (Input.Phase != InputActionPhase.Performed)
                {
                    Context.Logger.Info($"Move {Input.Phase}: {Input.Value.X}, {Input.Value.Y}");
                }
                return true;
            });
        PrimaryInput = Context.Input?.BindAction(InputRoute.Gameplay,
            "project://input/actions/gameplay.primary", 0, Input =>
            {
                if (Input.Phase == InputActionPhase.Performed)
                {
                    Context.Logger.Info("Primary");
                }
                return true;
            });
    }

    public void Shutdown()
    {
        PrimaryInput?.Dispose();
        PrimaryInput = null;
        MoveInput?.Dispose();
        MoveInput = null;
        MainScreenContract?.Dispose();
        MainScreenContract = null;
    }
}

public sealed class MainScreenViewModel
{
    public MainScreenState Screen
    {
        get;
    } = new();

    public PlayerHudState Player
    {
        get;
    } = new();
}

public sealed class MainScreenState
{
    public string Title
    {
        get;
        set;
    } = "__DISPLAY_NAME__";

    public string Status
    {
        get;
        set;
    } = "Connected";
}

public sealed class PlayerHudState
{
    public int Health
    {
        get;
        set;
    } = 100;

    public int Energy
    {
        get;
        set;
    } = 72;
}
