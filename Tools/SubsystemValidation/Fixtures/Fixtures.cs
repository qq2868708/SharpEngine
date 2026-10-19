using System.Text.Json.Nodes;
using SharpEngine.Editor;
using SharpEngine.Modules;
using SharpEngine.Subsystems;

[assembly: SharpEngineModule("validation.subsystems", "Editor")]

namespace SubsystemFixtures;

public static class Evidence
{
    public static readonly List<string> Events = new();
}

[Subsystem("validation.engine", 1)]
public sealed class EngineCounter : EngineSubsystem
{
    public int Updates;
    protected override void OnInitialize() { Evidence.Events.Add("engine.initialize"); }
    protected override void OnUpdate(float DeltaSeconds) { Updates++; }
    protected override void OnUninitialize() { Evidence.Events.Add("engine.uninitialize"); }
    protected override JsonObject CaptureState()
    {
        return new JsonObject
        {
            ["updates"] = Updates,
        };
    }
}

[Subsystem("validation.editor", 1)]
public sealed class EditorCounter : EditorSubsystem
{
    public int Updates;
    protected override void OnInitialize() { Evidence.Events.Add("editor.initialize"); }
    protected override void OnUpdate(float DeltaSeconds) { Updates++; }
    protected override void OnUninitialize() { Evidence.Events.Add("editor.uninitialize"); }
    protected override JsonObject CaptureState()
    {
        return new JsonObject
        {
            ["updates"] = Updates,
        };
    }
}

[Subsystem("validation.instance", 2)]
public sealed class InstanceCounter : InstanceSubsystem
{
    public int Updates;
    protected override void OnInitialize() { Evidence.Events.Add("instance.initialize"); }
    protected override void OnUpdate(float DeltaSeconds) { Updates++; }
    protected override void OnUninitialize()
    {
        Evidence.Events.Add("instance.worlds=" + Instance.Worlds.Count);
        Evidence.Events.Add("instance.uninitialize");
    }
    protected override JsonObject CaptureState()
    {
        return new JsonObject
        {
            ["updates"] = Updates,
            ["worlds"] = Instance.Worlds.Count,
        };
    }
}

[Subsystem("validation.world", 1)]
public sealed class WorldCounter : WorldSubsystem
{
    public int Updates;
    protected override void OnInitialize() { Evidence.Events.Add("world.initialize"); }
    protected override void OnUpdate(float DeltaSeconds)
    {
        Updates++;
        if (Updates == 1)
        {
            _ = World.CreateEntity("Subsystem entity");
        }
    }
    protected override void OnUninitialize() { Evidence.Events.Add("world.uninitialize"); }
    protected override JsonObject CaptureState()
    {
        return new JsonObject
        {
            ["updates"] = Updates,
            ["entities"] = World.Entities.Count,
        };
    }
}

public sealed class FixtureModule : ISharpEngineModule
{
    public static int Starts;
    public static int Stops;
    public void Startup(ModuleContext Context)
    {
        if (Context.Subsystems?.SubsystemLifetime.Subsystems.Get<EngineCounter>()?.IsInitialized != true)
        {
            throw new InvalidOperationException("Engine subsystem must be available before module Startup.");
        }
        Starts++;
    }
    public void Shutdown() { Stops++; }
}
