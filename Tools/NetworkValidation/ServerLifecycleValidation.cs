using SharpEngine.Components;
using SharpEngine.Networking;
using SharpEngine.Subsystems;

internal static class ServerLifecycleValidation
{
    internal static void Run(Action<bool, string> Check)
    {
        var Registry = ComponentRegistry.CreateBuiltin();
        Registry.RegisterAssembly(typeof(ServerLifecycleComponent).Assembly, "validation.server-lifecycle", "1");
        Registry.ValidateRegistrations();
        var Scene = new SceneData();
        var Entity = Scene.CreateEntity(Registry, "Lifecycle probe");
        Scene.AddComponent(Registry, Entity.EntityId, Registry.Get(typeof(ServerLifecycleComponent)).TypeId);

        using var Engine = new EngineLifetime();
        using var Session = new DedicatedServerSession(new NetworkSchemaCatalog(), Engine: Engine);
        Check(Session.ActiveWorld is null && Session.Instance.Worlds.Count == 0,
            "A staged server exposes its instance before creating World components");
        using var Registration = Engine.Registry.RegisterTypes("validation.server-lifecycle", "Runtime",
            typeof(ServerEngineCounter), typeof(ServerWorldService));
        Session.StartWorld(Scene, Registry);
        var Component = Session.World.FindEntity(Entity.EntityId)!.GetComponent<ServerLifecycleComponent>()!;
        var Service = Session.World.Subsystems.Get<ServerWorldService>()!;
        Check(Component.SawInitializedService, "Server components initialize after module World subsystems");
        var Counter = Engine.Subsystems.Get<ServerEngineCounter>()!;
        Engine.Update(RuntimeWorld.FixedDeltaSeconds);
        Session.Tick(RuntimeWorld.FixedDeltaSeconds);
        Check(Counter.Updates == 1, "An externally owned Engine is not ticked again by a server session");
        using var Other = new DedicatedServerSession(new SceneData(), Registry, new NetworkSchemaCatalog(), Engine: Engine);
        Other.Tick(RuntimeWorld.FixedDeltaSeconds);
        Check(Counter.Updates == 1, "Two sessions sharing an Engine do not double-tick its subsystems");
        Session.Dispose();
        Check(Component.SawServiceDuringShutdown && !Service.IsInitialized && Counter.IsInitialized &&
            Session.ActiveWorld is null, "Components release before World services; the shared Engine remains alive");

        using var Owned = new DedicatedServerSession(new NetworkSchemaCatalog());
        using var OwnedRegistration = Owned.Instance.Engine.Registry.RegisterTypes(
            "validation.server-engine", "Runtime", typeof(ServerEngineCounter));
        Owned.StartWorld(new SceneData(), Registry);
        var OwnedCounter = Owned.Instance.Engine.Subsystems.Get<ServerEngineCounter>()!;
        Owned.Tick(RuntimeWorld.FixedDeltaSeconds);
        Owned.Tick(RuntimeWorld.FixedDeltaSeconds);
        Check(OwnedCounter.Updates == 2, "A standalone server session advances its owned Engine once per tick");
        var Rejected = false;
        try
        {
            Owned.StartWorld(new SceneData(), Registry);
        }
        catch (InvalidOperationException)
        {
            Rejected = true;
        }
        Check(Rejected && Owned.Instance.Worlds.Count == 1, "Starting twice cannot leak or replace a running World");

        using var Failed = new DedicatedServerSession(new NetworkSchemaCatalog());
        var Invalid = Scene.Clone();
        Invalid.Entities[0].Components.Clear();
        Rejected = false;
        try
        {
            Failed.StartWorld(Invalid, Registry);
        }
        catch (InvalidDataException)
        {
            Rejected = true;
        }
        Check(Rejected && Failed.ActiveWorld is null && Failed.Instance.Worlds.Count == 0,
            "Failed World startup preserves the staged session without a partial World");
    }
}

[Subsystem("validation.server-engine-counter")]
public sealed class ServerEngineCounter : EngineSubsystem
{
    public int Updates;
    protected override void OnUpdate(float DeltaSeconds) { Updates++; }
}

[Subsystem("validation.server-world-service")]
public sealed class ServerWorldService : WorldSubsystem
{
}

[ComponentTypeId("7f093ac6-4d12-46d8-a7aa-4373b76b4f4d"), ComponentSchemaVersion(1)]
public sealed partial class ServerLifecycleComponent : SharpComponent
{
    public bool SawInitializedService
    {
        get;
        private set;
    }
    public bool SawServiceDuringShutdown
    {
        get;
        private set;
    }
    protected override void OnInitialize()
    {
        SawInitializedService = World.Subsystems.Get<ServerWorldService>()?.IsInitialized == true;
    }
    protected override void OnUninitialize()
    {
        SawServiceDuringShutdown = World.Subsystems.Get<ServerWorldService>()?.IsInitialized == true;
    }
}
