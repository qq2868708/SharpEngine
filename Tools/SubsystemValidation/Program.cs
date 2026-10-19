using System.Reflection;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Editor;
using SharpEngine.Modules;
using SharpEngine.Subsystems;
using SubsystemFixtures;

var Root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var Count = 0;
void Check(bool Condition, string Message)
{
    if (!Condition)
    {
        throw new Exception(Message);
    }
    Count++;
}
void Reject(Action Action, string Code)
{
    try
    {
        Action();
    }
    catch (SubsystemException Error)
    {
        Check(Error.Code == Code, "structured rejection " + Code);
        return;
    }
    throw new Exception("Expected rejection " + Code);
}
JsonObject State(SubsystemCollection Collection, string Id)
{
    return Collection.ToJson()["subsystems"]!.AsArray().Single(Node => Node!["typeId"]!.GetValue<string>() == Id)!.AsObject();
}
var Components = ComponentRegistry.CreateBuiltin();
Components.RegisterAssembly(typeof(ComponentProbe).Assembly, "validation", "1");
var Scene = new SceneData();
var Entity = Scene.CreateEntity(Components, "Probe");
Scene.AddComponent(Components, Entity.EntityId, Components.Get(typeof(ComponentProbe)).TypeId);
using var Engine = new EngineLifetime();
using var Definitions = Engine.Registry.RegisterAssembly(typeof(EngineCounter).Assembly, "validation", "Editor");
var EngineObject = Engine.Subsystems.Get<EngineCounter>()!;
Check(EngineObject.IsInitialized && Engine.Subsystems.Get<InstanceCounter>() is null, "only matching scopes are instantiated eagerly");
Check(Engine.Registry.ToJson()["types"]!.AsArray().Any(Node => Node!["typeId"]!.GetValue<string>() == "validation.instance" && Node["scope"]!.GetValue<string>() == "Instance"),
    "discovery lists registered Instance types before an Instance scope exists");
Check(ReferenceEquals(EngineObject, Engine.Subsystems.Get<EngineCounter>()), "concrete engine type is a singleton within its owner");
using var OtherEngine = new EngineLifetime();
using var OtherDefinitions = OtherEngine.Registry.RegisterTypes("validation", "Runtime", typeof(EngineCounter));
Check(!ReferenceEquals(EngineObject, OtherEngine.Subsystems.Get<EngineCounter>()), "engine lifetimes are isolated without a global static singleton");
var BeforeDuplicate = Engine.Subsystems.ToJson().ToJsonString();
Reject(() => Engine.Registry.RegisterTypes("duplicate", "Runtime", typeof(EngineCounter)), "SUBSYSTEM_DUPLICATE");
Reject(() => Engine.Registry.RegisterTypes("invalid", "Runtime", typeof(string)), "SUBSYSTEM_DESCRIPTOR");
Reject(() => Engine.Registry.RegisterTypes("invalid", "Runtime", typeof(EditorCounter)), "SUBSYSTEM_EDITOR_MODULE");
Check(Engine.Subsystems.ToJson().ToJsonString() == BeforeDuplicate, "invalid registration preserves committed state and revisions");
Engine.Registry.ValidateModules(new[]
{
    (typeof(EngineCounter).Assembly, "validation", "Editor"),
});
Reject(() => Engine.Registry.ValidateModules(new[]
{
    (typeof(EngineCounter).Assembly, "other-module", "Editor"),
}), "SUBSYSTEM_DUPLICATE");
Check(Engine.Subsystems.ToJson().ToJsonString() == BeforeDuplicate && EngineObject.IsInitialized, "reload preflight validates replacement definitions without touching live instances");

using var Instance = new RuntimeInstance(Engine: Engine);
var InstanceObject = Instance.Subsystems.Get<InstanceCounter>()!;
var First = Instance.CreateWorld(Scene, Components);
var Second = Instance.CreateWorld(new SceneData(), Components);
var FirstObject = First.Subsystems.Get<WorldCounter>()!;
var SecondObject = Second.Subsystems.Get<WorldCounter>()!;
Check(InstanceObject.IsInitialized && FirstObject.IsInitialized && !ReferenceEquals(FirstObject, SecondObject), "instance owns one subsystem while worlds own independent instances");
Check(ReferenceEquals(First.Subsystems.Get<InstanceCounter>(), InstanceObject) && ReferenceEquals(First.Subsystems.Get<EngineCounter>(), EngineObject), "world lookup walks fixed Instance and Engine parents");
Check(((ComponentProbe)First.FindEntity(Entity.EntityId)!.GetComponent<ComponentProbe>()!).SawInitializedSubsystem, "World subsystem initializes before component initialization");
var OldFrame = First.Frame;
var OldFrameJson = OldFrame.ToJson().ToJsonString();
Engine.Update(0.25f);
Instance.Tick(0.25f);
Check(EngineObject.Updates == 1 && InstanceObject.Updates == 1 && FirstObject.Updates == 1 && SecondObject.Updates == 1, "one host tick updates each fixed scope exactly once across multiple worlds");
Check(First.Entities.Count == 2 && First.Diagnostics.Count == 0 && State(First.Subsystems, "validation.world")["state"]!["entities"]!.GetValue<int>() == 2,
    "World subsystem mutations commit at the component boundary before the final snapshot");
Check(OldFrame.ToJson().ToJsonString() == OldFrameJson && First.Frame.ToJson()["subsystems"]!["subsystems"]![0]!["state"]!["updates"]!.GetValue<int>() == 1,
    "committed World frames freeze subsystem state with the scene");
var Revision = First.Revision;
var Observation = First.Subsystems.ToJson();
Observation["subsystems"]![0]!["state"]!["updates"] = 1000;
Check(State(First.Subsystems, "validation.world")["state"]!["updates"]!.GetValue<int>() == 1 && First.Revision == Revision && FirstObject.Updates == 1,
    "observation clones committed JSON and never invokes update or capture callbacks");
Check(State(Instance.Subsystems, "validation.instance")["schemaVersion"]!.GetValue<int>() == 2 && !First.Snapshot().Serialize().Contains("validation.world", StringComparison.Ordinal),
    "runtime subsystem state is versioned and excluded from authoritative scene persistence");

Evidence.Events.Clear();
First.Dispose();
Check(Evidence.Events.IndexOf("component.uninitialize") < Evidence.Events.IndexOf("world.uninitialize") && !FirstObject.IsInitialized && InstanceObject.IsInitialized,
    "components release before World subsystems while Instance remains alive");
var Replacement = Instance.CreateWorld(new SceneData(), Components);
Check(ReferenceEquals(Replacement.Subsystems.Get<InstanceCounter>(), InstanceObject) && !ReferenceEquals(Replacement.Subsystems.Get<WorldCounter>(), FirstObject),
    "replacing a World preserves Instance subsystems and recreates World subsystems");
using var OtherInstance = new RuntimeInstance(Engine: Engine);
Check(!ReferenceEquals(OtherInstance.Subsystems.Get<InstanceCounter>(), InstanceObject), "two game instances under one Engine remain isolated");

var BeforeFailure = Engine.Subsystems.ToJson().ToJsonString();
Reject(() => Engine.Registry.RegisterTypes("failure", "Runtime", typeof(GoodTemporary), typeof(FailingInitialize)), "SUBSYSTEM_INITIALIZATION_FAILED");
Check(Engine.Subsystems.Get<GoodTemporary>() is null && Engine.Subsystems.Get<FailingInitialize>() is null && FailingInitialize.Cleaned == 1 && GoodTemporary.Cleaned == 1 && Engine.Subsystems.ToJson().ToJsonString() == BeforeFailure,
    "failed initialization cleans partial resources and rolls back the complete registration batch");
var WorldBeforeFailure = Replacement.Subsystems.ToJson().ToJsonString();
Reject(() => Engine.Registry.RegisterTypes("world-failure", "Runtime", typeof(GoodTemporary), typeof(FailingWorldInitialize)), "SUBSYSTEM_INITIALIZATION_FAILED");
Check(Engine.Subsystems.Get<GoodTemporary>() is null && Replacement.Subsystems.Get<FailingWorldInitialize>() is null && GoodTemporary.Cleaned == 2 && FailingWorldInitialize.Cleaned == 1 &&
    Replacement.Subsystems.ToJson().ToJsonString() == WorldBeforeFailure && Engine.Subsystems.ToJson().ToJsonString() == BeforeFailure,
    "initialization failure rolls back Engine and World instances across all live scopes");
Reject(() => Engine.Registry.RegisterTypes("construction-failure", "Runtime", typeof(FailingConstructor)), "SUBSYSTEM_CONSTRUCTION_FAILED");
Check(Engine.Subsystems.Get<FailingConstructor>() is null && Engine.Subsystems.ToJson().ToJsonString() == BeforeFailure, "constructor failure preserves live definitions and snapshots");
using (var Scope = new ModuleRegistrationScope())
{
    using (Scope.Enter())
    {
        Engine.Registry.RegisterTypes("generation", "Runtime", typeof(ReloadCounter));
    }
    Check(Replacement.Subsystems.Get<ReloadCounter>()?.IsInitialized == true, "module generation registration populates existing scopes");
}
Check(Replacement.Subsystems.Get<ReloadCounter>() is null && ReloadCounter.Cleaned == 2, "module generation disposal removes live subsystem instances in every world");
using (var Reload = Engine.Registry.RegisterTypes("generation", "Runtime", typeof(ReloadCounter)))
{
    Check(Replacement.Subsystems.Get<ReloadCounter>()?.IsInitialized == true, "a subsequent module generation can register the same stable id");
}

using (var Fault = Engine.Registry.RegisterTypes("fault", "Runtime", typeof(FailingUpdate)))
{
    Engine.Update(0);
    Check(State(Engine.Subsystems, "validation.fault")["code"]!.GetValue<string>() == "SUBSYSTEM_UPDATE_FAILED" && EngineObject.Updates == 2,
        "one failing subsystem reports a diagnostic while independent updates continue");
}
var OffThreadRejected = Task.Run(() =>
{
    try
    {
        Engine.Subsystems.ToJson();
    }
    catch (InvalidOperationException)
    {
        return true;
    }
    return false;
}).GetAwaiter().GetResult();
Check(OffThreadRejected, "reads and mutations enforce owner thread access");

Evidence.Events.Clear();
Instance.Dispose();
Check(!InstanceObject.IsInitialized && !SecondObject.IsInitialized && EngineObject.IsInitialized && Evidence.Events.Last() == "instance.uninitialize",
    "Instance teardown destroys all remaining Worlds before Instance subsystems");
Check(Evidence.Events.Contains("instance.worlds=0", StringComparer.Ordinal), "owner remains accessible during Instance subsystem uninitialization");
using (var IsolatedEngine = new EngineLifetime())
{
    using var FailureDefinition = IsolatedEngine.Registry.RegisterTypes("world-failure", "Runtime", typeof(FailingWorldInitialize));
    using var IsolatedInstance = new RuntimeInstance(Engine: IsolatedEngine);
    Reject(() => IsolatedInstance.CreateWorld(new SceneData(), Components), "SUBSYSTEM_INITIALIZATION_FAILED");
    Check(IsolatedInstance.Worlds.Count == 0, "failed World scope initialization detaches the partially constructed World from its Instance");
}
using (var ImplicitWorld = new RuntimeWorld(new SceneData(), Components))
{
    var ImplicitInstance = ImplicitWorld.Instance;
    var ImplicitEngine = ImplicitInstance.Engine;
    ImplicitWorld.Dispose();
    var Closed = false;
    try
    {
        ImplicitEngine.Update(0);
    }
    catch (ObjectDisposedException)
    {
        Closed = true;
    }
    Check(Closed, "standalone RuntimeWorld owns and releases its implicit Instance and Engine lifetimes");
}

var PreviousRoot = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT");
var PreviousManifest = Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST");
Environment.SetEnvironmentVariable("SHARPENGINE_ROOT", Root);
Environment.SetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST", null);
var Editor = new EditorRuntime(0, 800, 600, 0);
try
{
    Editor.Initialize();
    // Exercise the real module host's discovery and automatic registration path.
    var HostType = typeof(RuntimeWorld).Assembly.GetType("SharpEngine.Hosting.EngineModuleHost")!;
    using var Host = (IDisposable)HostType.GetMethod("Prepare", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
        new object[]
        {
            "Editor", new[] { typeof(FixtureModule).Assembly },
        })!;
    Func<string, string, ModuleContext> Context = (Id, Type) => new TestModuleContext(Editor, Id, Type);
    HostType.GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Host, new object?[]
    {
        Context, null,
    });
    var EditorObject = Editor.EditorSubsystems.Get<EditorCounter>()!;
    var EditorEngine = Editor.SubsystemLifetime.Subsystems.Get<EngineCounter>()!;
    Check(FixtureModule.Starts == 1 && EditorObject.IsInitialized && Editor.Instance is null, "module host automatically discovers Engine and Editor subsystems before Startup");
    Editor.InvokeUiAction("editor.play");
    var PlayInstance = Editor.Instance!;
    var PlayCounter = PlayInstance.Subsystems.Get<InstanceCounter>()!;
    Editor.Update(0.016f);
    Editor.InvokeUiAction("editor.pause");
    Editor.Update(0.016f);
    Check(EditorObject.Updates == 2 && EditorEngine.Updates == 2 && PlayCounter.Updates == 1, "Engine and Editor scopes continue updating while Play Instance is paused");
    Editor.InvokeUiAction("editor.command:core.step");
    Check(PlayCounter.Updates == 2 && Editor.World!.RuntimeFrame!.Revision == Editor.RuntimeWorld!.Revision, "Editor single-step advances Instance and republishes the complete World frame");
    Editor.InvokeUiAction("editor.stop");
    Check(!PlayCounter.IsInitialized && Editor.Instance is null && EditorObject.IsInitialized && EditorEngine.IsInitialized, "Stop releases Play scopes while retaining Editor and Engine scopes");
    Editor.InvokeUiAction("editor.play");
    Check(Editor.Instance!.Id != PlayInstance.Id && !ReferenceEquals(Editor.Instance.Subsystems.Get<InstanceCounter>(), PlayCounter), "a new Play session reconstructs fresh Instance subsystems");
    Check(Editor.DescribeComponentsJson().Contains("\"subsystems\"", StringComparison.Ordinal) && Editor.DescribeSubsystems()["world"] is not null, "existing component discovery exposes versioned committed subsystem snapshots");
    Editor.StopRuntimeInstance();
    Host.Dispose();
    Check(FixtureModule.Stops == 1 && !EditorObject.IsInitialized && !EditorEngine.IsInitialized && Editor.EditorSubsystems.Get<EditorCounter>() is null,
        "module shutdown unregisters its subsystem definitions and instances");
}
finally
{
    Editor.Shutdown();
    Environment.SetEnvironmentVariable("SHARPENGINE_ROOT", PreviousRoot);
    Environment.SetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST", PreviousManifest);
}
Console.WriteLine($"[SubsystemValidation] PASS: {Count} assertions; owner lifetimes, module registration, committed snapshots and Editor Play/Stop/Step");

[ComponentTypeId("b629f98c-dd7b-479d-9377-ed67b8a70fd0"), ComponentSchemaVersion(1)]
public sealed partial class ComponentProbe : SharpComponent
{
    [NonSerialized]
    public bool SawInitializedSubsystem;
    protected override void OnInitialize() { SawInitializedSubsystem = World.Subsystems.Get<WorldCounter>()?.IsInitialized == true; }
    protected override void OnUninitialize() { Evidence.Events.Add("component.uninitialize"); }
}

[Subsystem("validation.temporary", 1)]
public sealed class GoodTemporary : EngineSubsystem
{
    public static int Cleaned;
    protected override void OnUninitialize() { Cleaned++; }
}

[Subsystem("validation.zz-failure", 1)]
public sealed class FailingInitialize : EngineSubsystem
{
    public static int Cleaned;
    protected override void OnInitialize() { throw new InvalidOperationException("expected initialization failure"); }
    protected override void OnUninitialize() { Cleaned++; }
}

[Subsystem("validation.reload", 1)]
public sealed class ReloadCounter : WorldSubsystem
{
    public static int Cleaned;
    protected override void OnUninitialize() { Cleaned++; }
}

[Subsystem("validation.fault", 1)]
public sealed class FailingUpdate : EngineSubsystem
{
    protected override void OnUpdate(float DeltaSeconds) { throw new InvalidOperationException("expected update failure"); }
}

[Subsystem("validation.world-failure", 1)]
public sealed class FailingWorldInitialize : WorldSubsystem
{
    public static int Cleaned;
    protected override void OnInitialize() { throw new InvalidOperationException("expected World initialization failure"); }
    protected override void OnUninitialize() { Cleaned++; }
}

[Subsystem("validation.constructor-failure", 1)]
public sealed class FailingConstructor : EngineSubsystem
{
    public FailingConstructor() { throw new InvalidOperationException("expected construction failure"); }
}

public sealed class TestModuleContext(EditorRuntime Editor, string Id, string Type)
    : ModuleContext(RootPath, Id, Type, components: Editor)
{
    private const string RootPath = "validation";
}
