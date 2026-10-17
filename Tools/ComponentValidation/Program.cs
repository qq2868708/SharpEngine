using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Editor;
using SharpEngine.World;

int count = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); count++; }
void Reject(Action action, string message) { try { action(); } catch { Check(true, message); return; } throw new Exception("Expected rejection: " + message); }
var registry = ComponentRegistry.CreateBuiltin();
registry.RegisterAssembly(typeof(Probe).Assembly, "validation", "generation-1");
registry.ValidateRegistrations();
Check(registry.Get(typeof(Composite)).Schema(registry)["$defs"] is JsonObject, "recursive data schemas publish JSON Schema definitions");
var cycleRegistry = ComponentRegistry.CreateBuiltin();
cycleRegistry.Register(new("test.a", 1, typeof(Other), "", false, [typeof(Fault)], [], () => new Other(), _ => new()), "test", "test");
cycleRegistry.Register(new("test.b", 1, typeof(Fault), "", false, [typeof(Other)], [], () => new Fault(), _ => new()), "test", "test");
Reject(() => cycleRegistry.ValidateRegistrations(), "dependency cycles rejected before instantiation");
Check(registry.Types.Count == 11, "built-in and generated project descriptors register");
var probeType = registry.Get(typeof(Probe));
Check(probeType.CreateDefaultState(registry)["_private"]!.GetValue<int>() == 17, "private field defaults generated without component construction");
var data = new SceneData { Name = "Validation" };
var a = data.CreateEntity(registry, "A");
var b = data.CreateEntity(registry, "B", a.EntityId);
var p = data.AddComponent(registry, a.EntityId, probeType.TypeId).Last();
var other = data.AddComponent(registry, b.EntityId, registry.Get(typeof(Other)).TypeId).Last();
p.State["Other"] = other.ComponentId;
p.State["EntityTarget"] = b.EntityId;
data.Validate(registry);
Check(SceneData.Parse(data.Serialize(), registry).Serialize() == data.Serialize(), "v3 deterministic round trip preserves all fields and IDs");
Reject(() => SceneData.Parse("<world version=\"2\"/>", registry), "legacy XML rejected");
Reject(() => SceneData.Parse(data.Serialize().Replace("\"schemaVersion\": 3", "\"schemaVersion\": 2"), registry), "old schema rejected");
Reject(() => SceneData.Parse(data.Serialize().Replace("\"schemaVersion\": 3", "\"schemaVersion\": 3, \"schemaVersion\": 3"), registry), "duplicate JSON field rejected");
var document = new WorldDocument(data.Clone(), registry);
var authoring = new WorldAuthoringSession(document);
var patch = authoring.PatchComponent(a.EntityId, p.ComponentId, new() { ["Health"] = 42 }, authoring.DocumentRevision);
Check(patch.Success && authoring.IsDirty, "authoring patch commits revision and dirty state");
Check(!authoring.PatchComponent(a.EntityId, p.ComponentId, new() { ["Health"] = 43 }, 1).Success, "stale authoring revision rejected");
Check(authoring.Undo(authoring.DocumentRevision).Success && !authoring.IsDirty, "component undo restores savepoint");
Check(authoring.Redo(authoring.DocumentRevision).Success, "component redo restores patch");
Check(!authoring.PatchComponent(a.EntityId, p.ComponentId, new() { ["Health"] = -1 }, authoring.DocumentRevision).Success, "range violation is atomic");
Check(document.Data.Entities[0].Components.Last().State["Health"]!.GetValue<int>() == 42, "invalid patch preserved prior state");
Probe.Events.Clear();
using (var runtime = new RuntimeWorld(document.Data, registry))
{
    var live = (Probe)runtime.FindComponent(p.ComponentId)!;
    var composite = runtime.AddComponent<Composite>(a.EntityId, new Composite.InitialState { Count = 4 }).Value;
    var invalidLimits = runtime.Patch(composite.ComponentId, new() { ["Min"] = 20, ["Max"] = 10 }, runtime.Revision);
    Check(invalidLimits.Error is not null && composite.Min == 0, "generated static validator rejects cross-field violations atomically");
    var cyclicData = new SampleNode(); cyclicData.Next = cyclicData;
    Reject(() => ComponentValue.Encode(cyclicData, typeof(SampleNode), registry), "cyclic data instances rejected while recursive type definitions are supported");
    Check(composite.Count == 4 && composite.BaseValue == 5 && composite.A!.Seed == 7, "typed initial state, inherited private fields and nested defaults are generated");
    Check(composite.Point.Value == 0 && composite.Values.SequenceEqual(new[] { 1, 2 }), "nested structs and list defaults reconstruct");
    composite.A = composite.B = new SampleData();
    using (var rebuilt = new RuntimeWorld(runtime.Snapshot(), registry))
    {
        // Capture above is the latest committed boundary; publish direct field changes first below.
        Check(rebuilt.FindComponent(composite.ComponentId) is Composite, "generated inherited component reconstructs");
    }
    Check(live.Health == 42 && live.PrivateValue == 17 && live.Other is not null, "Play injects Inspector and private values before initialization");
    Check(live.SawOther && live.StartedAfterOther, "initialization and Start barriers expose complete graph");
    runtime.Step();
    var sharedSnapshot = runtime.Snapshot();
    // Runtime code may temporarily exceed editor constraints; restore only that test fixture value before reconstruction.
    foreach (var item in sharedSnapshot.Entities.SelectMany(e => e.Components).Where(c => c.TypeId == probeType.TypeId)) item.State["Health"] = 43;
    using (var rebuilt = new RuntimeWorld(sharedSnapshot, registry))
    {
        var restoredComposite = (Composite)rebuilt.FindComponent(composite.ComponentId)!;
        Check(restoredComposite.A is not null && restoredComposite.B is not null && !ReferenceEquals(restoredComposite.A, restoredComposite.B), "nested data round trips by value without alias preservation");
    }
    Check(live.Health == 43 && document.Data.Entities[0].Components.Last().State["Health"]!.GetValue<int>() == 42, "Runtime fields are isolated from Authoring");
    Check(Probe.Events.Contains("fixed") && Probe.Events.Contains("late"), "single step runs fixed update and late update");
    live.Enabled = false;
    Check(Probe.Events.Last() == "disable", "disable transition runs at safe boundary");
    var starts = Probe.Events.Count(e => e == "start");
    live.Enabled = true;
    Check(Probe.Events.Count(e => e == "start") == starts, "Start runs once across reactivation");
    var added = runtime.AddComponent<Other>(a.EntityId).Value;
    Check(added.IsAlive, "dynamic component added");
    runtime.AddComponent<Other>(a.EntityId);
    Reject(() => runtime.FindEntity(a.EntityId)!.GetComponent<Other>(), "multiple matching GetComponent is ambiguous");
    var disabled = runtime.CreateEntity("inactive").Value;
    disabled.ActiveSelf = false;
    var disabledProbe = disabled.AddComponent<Probe>().Value;
    Check(disabledProbe.InitializedObserved && !disabledProbe.EnabledObserved, "inactive entity still initializes component");
    disabled.ActiveSelf = true;
    Check(disabledProbe.EnabledObserved, "entity activation enables components");
    var fault = runtime.AddComponent<Fault>(a.EntityId).Value;
    Check(fault.IsAlive && fault.Enabled && fault.StartObserved, "initialization exception does not rollback or disable");
    runtime.Step(); runtime.Step();
    Check(fault.Updates == 2 && runtime.Diagnostics.Count(d => d.ObjectId == fault.ComponentId) == 3, "exceptions logged while subsequent ticks continue");
    var queued = runtime.AddComponent<Queued>(a.EntityId).Value;
    runtime.Step();
    Check(queued.PendingInsideCallback && queued.Created!.IsCompleted && queued.Created.Value.IsAlive, "callback spawn is deferred until safe boundary");
    var dead = live.Other!;
    runtime.DestroyEntity(b.EntityId);
    Check(!dead.IsAlive && live.Other is null && live.EntityTarget is null, "subtree deletion nulls serialized references without forcing CLR collection");
    Check(runtime.FindEntity(b.EntityId) is null, "deleted entity removed from registry");
}
var prefab = data.ExportPrefab(registry, a.EntityId);
Check(prefab.Entities.Count == 2 && prefab.IsPrefab, "Prefab exports complete subtree");
var external = data.AddComponent(registry, b.EntityId, probeType.TypeId).Last();
external.State["EntityTarget"] = a.EntityId;
Reject(() => data.ExportPrefab(registry, b.EntityId), "Prefab export rejects outside entity references");
var destination = new SceneData();
var copies = destination.Instantiate(prefab, registry);
destination.Validate(registry);
Check(copies.All(e => data.Entities.All(old => old.EntityId != e.EntityId)), "Prefab instance has fresh entity IDs");
var copiedProbe = copies.SelectMany(e => e.Components).Single(c => c.TypeId == probeType.TypeId);
Check(copiedProbe.State["Other"]!.GetValue<string>() == copies.SelectMany(e => e.Components).Single(c => c.TypeId == registry.Get(typeof(Other)).TypeId).ComponentId, "Prefab internal component reference remapped");
var unknownJson = data.ToJson();
unknownJson["entities"]![0]!["components"]!.AsArray().Add(new JsonObject { ["componentId"] = "unknown", ["typeId"] = "missing", ["schemaVersion"] = 4, ["enabled"] = true, ["state"] = new JsonObject { ["keep"] = 99 }, ["extension"] = "preserved" });
var unknown = SceneData.Parse(unknownJson.ToJsonString(), registry);
Check(unknown.ToJson()["entities"]![0]!["components"]!.AsArray().Last()!["extension"]!.GetValue<string>() == "preserved", "unknown component record preserved");
Reject(() => unknown.Validate(registry, cook: true), "Cook validation rejects unknown component");
Console.WriteLine($"Component validation passed: {count} assertions");

[ComponentTypeId("b8034dab-c1f0-4330-9850-690497c68324"), ComponentSchemaVersion(1)]
public sealed partial class Probe : SharpComponent
{
    [SharpEngine.Components.Range(0, 100)] public int Health = 100;
    [SerializeField] private int _private = 17;
    public Other? Other;
    public SharpEntity? EntityTarget;
    [NonSerialized] public bool SawOther, StartedAfterOther, InitializedObserved, EnabledObserved;
    public static readonly List<string> Events = [];
    public int PrivateValue => _private;
    protected override void OnInitialize() { InitializedObserved = true; SawOther = Other is not null; Events.Add("initialize"); }
    protected override void OnEnable() { EnabledObserved = true; Events.Add("enable"); }
    protected override void OnStart() { StartedAfterOther = Other?.Ready ?? true; Events.Add("start"); }
    protected override void OnFixedUpdate(float deltaSeconds) => Events.Add("fixed");
    protected override void OnUpdate(float deltaSeconds) { Health++; Events.Add("update"); }
    protected override void OnLateUpdate(float deltaSeconds) => Events.Add("late");
    protected override void OnDisable() => Events.Add("disable");
}
[ComponentTypeId("169aa40e-6822-4381-afae-d7bc51fb8a2b"), ComponentSchemaVersion(1)]
public sealed partial class Other : SharpComponent
{
    [NonSerialized] public bool Ready;
    protected override void OnInitialize() => Ready = true;
}
[ComponentTypeId("28415047-e4a2-40df-99e1-86cc6fa39a0e"), ComponentSchemaVersion(1)]
public sealed partial class Fault : SharpComponent
{
    [NonSerialized] public bool StartObserved;
    [NonSerialized] public int Updates;
    protected override void OnInitialize() => throw new Exception("expected initialization failure");
    protected override void OnStart() => StartObserved = true;
    protected override void OnUpdate(float deltaSeconds) { Updates++; throw new Exception("expected update failure"); }
}
[ComponentTypeId("adeb9182-91c1-443b-b5b4-b3e9e7a7b731"), ComponentSchemaVersion(1)]
public sealed partial class Queued : SharpComponent
{
    [NonSerialized] public RuntimeMutation<SharpEntity>? Created;
    [NonSerialized] public bool PendingInsideCallback;
    protected override void OnUpdate(float deltaSeconds)
    { if (Created is null) { Created = World.CreateEntity("queued"); PendingInsideCallback = !Created.IsCompleted; } }
}

public abstract partial class CommonComponent : SharpComponent
{
    [SerializeField] private int _baseValue = 5;
    public int BaseValue => _baseValue;
}
[Serializable]
public partial class SampleData
{
    [SerializeField] private int _seed = 7;
    public int Seed => _seed;
}
[Serializable]
public partial struct SamplePoint { public int Value; }
[Serializable]
public sealed partial class SampleNode { public string Label = "node"; public SampleNode? Next; }
[ComponentTypeId("872a75cf-e44b-46eb-a558-f90e8138276b"), ComponentSchemaVersion(1), RequireComponent(typeof(Other)), DisallowMultipleComponent]
public sealed partial class Composite : CommonComponent
{
    public int Count = 2;
    public SampleData? A = new SampleData();
    public SampleData? B;
    public SamplePoint Point;
    public List<int> Values = [1, 2];
    public SampleNode? Root;
    public int Min;
    public int Max = 10;
    public static void Validate(in GeneratedState state, ComponentDiagnosticSink diagnostics)
    { if (state.Min > state.Max) diagnostics.Error("INVALID_LIMITS", nameof(Min), "Min must not exceed Max"); }
}
