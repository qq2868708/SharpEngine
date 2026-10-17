namespace SharpEngine.Modules;

public enum InputDeviceKind
{
    Keyboard,
    Mouse,
    Gamepad,
}

public enum InputActionValueType
{
    Button,
    Axis1D,
    Axis2D,
}

public enum InputActionPhase
{
    Started,
    Performed,
    Canceled,
}

public enum InputRoute
{
    Editor,
    RuntimeUi,
    Gameplay,
}

public readonly record struct InputActionValue(float X, float Y = 0)
{
    public bool IsZero => MathF.Abs(X) <= 0.0001f && MathF.Abs(Y) <= 0.0001f;
}

public sealed record InputActionDefinition(
    int SchemaVersion,
    string Id,
    InputActionValueType ValueType,
    bool Continuous);

public sealed record InputBindingDefinition(
    string ActionId,
    string Control,
    float ScaleX,
    float ScaleY,
    IReadOnlyList<string> Modifiers,
    bool ExactModifiers);

public sealed record InputMappingContextDefinition(
    int SchemaVersion,
    string Id,
    InputRoute Route,
    int Priority,
    bool ActivateByDefault,
    IReadOnlyList<InputBindingDefinition> Bindings);

public sealed record InputActionEvent(
    ulong FrameId,
    ulong Sequence,
    string HostId,
    string ContextId,
    string ActionId,
    InputRoute Route,
    InputActionPhase Phase,
    InputActionValue Value,
    InputDeviceKind Device,
    string DeviceId,
    string Control);

public interface IInputRuntime
{
    IReadOnlyList<InputActionDefinition> Actions { get; }
    IReadOnlyList<InputMappingContextDefinition> MappingContexts { get; }
    IDisposable BindAction(InputRoute route, string actionId, int priority,
        Func<InputActionEvent, bool> handler);
    IDisposable ActivateContext(string contextId);
    bool IsContextActive(string contextId);
    string DescribeJson();
}
