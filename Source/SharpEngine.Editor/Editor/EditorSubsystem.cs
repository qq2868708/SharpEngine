using System.Text.Json.Nodes;
using SharpEngine.Subsystems;

namespace SharpEngine.Editor;

[SubsystemScope(SubsystemScope.Editor)]
public abstract class EditorSubsystem : Subsystem
{
    protected EditorRuntime Editor
    {
        get
        {
            return (EditorRuntime)Subsystems.Owner;
        }
    }
}

public sealed partial class EditorRuntime : ISubsystemRuntime
{
    public EngineLifetime SubsystemLifetime
    {
        get;
    } = new();
    public SubsystemCollection EditorSubsystems
    {
        get;
        private set;
    } = null!;
    public RuntimeInstance? Instance
    {
        get;
        private set;
    }

    public JsonObject DescribeSubsystems()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["catalog"] = SubsystemLifetime.Registry.ToJson(),
            ["engine"] = SubsystemLifetime.Subsystems.ToJson(),
            ["editor"] = EditorSubsystems.ToJson(),
            ["instance"] = Instance?.Subsystems.ToJson(),
            ["world"] = _runtimeWorld?.Frame.ToJson()["subsystems"]?.DeepClone(),
        };
    }

    /// <summary>Release runtime owners before project modules and their registrations are unloaded.</summary>
    public void StopRuntimeInstance()
    {
        if (_runtimeWorld is not null)
        {
            EndComponentPlay();
        }
        Instance?.Dispose();
        Instance = null;
        PlayState = EditorPlayState.Stopped;
    }
}
