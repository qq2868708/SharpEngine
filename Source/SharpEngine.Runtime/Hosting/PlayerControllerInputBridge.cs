using System.Numerics;
using SharpEngine.Components;
using SharpEngine.Input;

namespace SharpEngine.Hosting;

/// <summary>Connects the sample gameplay action to player controllers without moving gameplay logic into input.</summary>
internal sealed class PlayerControllerInputBridge : IDisposable
{
    private const string MoveActionId = "engine://input/actions/demo.move";
    private const string GameplayContextId = "engine://input/contexts/demo-gameplay";

    private readonly RuntimeWorld RuntimeWorld;
    private readonly IDisposable ContextActivation;
    private readonly IDisposable MoveRegistration;
    private bool Disposed;

    private PlayerControllerInputBridge(RuntimeWorld RuntimeWorld, IInputRuntime Input)
    {
        this.RuntimeWorld = RuntimeWorld;
        ContextActivation = Input.ActivateContext(GameplayContextId);
        MoveRegistration = Input.BindAction(InputRoute.Gameplay, MoveActionId, int.MinValue,
            HandleMoveInput);
    }

    internal static PlayerControllerInputBridge? TryCreate(RuntimeWorld RuntimeWorld,
        IInputRuntime Input)
    {
        ArgumentNullException.ThrowIfNull(RuntimeWorld);
        ArgumentNullException.ThrowIfNull(Input);
        if (!Input.Actions.Any(Action => Action.Id == MoveActionId) ||
            !Input.MappingContexts.Any(Context => Context.Id == GameplayContextId))
        {
            return null;
        }
        return new PlayerControllerInputBridge(RuntimeWorld, Input);
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }
        Disposed = true;
        MoveRegistration.Dispose();
        ContextActivation.Dispose();
    }

    private bool HandleMoveInput(InputActionEvent Input)
    {
        if (Disposed)
        {
            return false;
        }
        var Move = Input.Phase == InputActionPhase.Canceled
            ? Vector2.Zero
            : new Vector2(Input.Value.X, Input.Value.Y);
        foreach (var Controller in RuntimeWorld.Entities
                     .SelectMany(Entity => Entity.GetComponents<PlayerControllerComponent>()))
        {
            if (Controller.IsAlive)
            {
                Controller.SetMoveInput(Move);
            }
        }
        return false;
    }
}
