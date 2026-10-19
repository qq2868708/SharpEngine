using System.Text.Json.Nodes;

namespace SharpEngine.Components;

public sealed class ControllableFrameState : ComponentFrameState
{
    private readonly JsonObject State;

    internal ControllableFrameState(JsonObject State)
    {
        this.State = (JsonObject)State.DeepClone();
    }

    public override JsonObject ToJson()
    {
        return (JsonObject)State.DeepClone();
    }
}

public sealed class ControllerFrameState : ComponentFrameState
{
    private readonly JsonObject State;

    internal ControllerFrameState(JsonObject State)
    {
        this.State = (JsonObject)State.DeepClone();
    }

    public override JsonObject ToJson()
    {
        return (JsonObject)State.DeepClone();
    }
}

public sealed class MovementFrameState : ComponentFrameState
{
    private readonly JsonObject State;

    internal MovementFrameState(JsonObject State)
    {
        this.State = (JsonObject)State.DeepClone();
    }

    public override JsonObject ToJson()
    {
        return (JsonObject)State.DeepClone();
    }
}
