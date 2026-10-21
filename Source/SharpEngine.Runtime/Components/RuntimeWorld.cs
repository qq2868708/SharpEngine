using System.Text.Json.Nodes;
using SharpEngine.Logging;
using SharpEngine.Subsystems;

namespace SharpEngine.Components;

/// <summary>Owns live instances. Authoring records never contain these objects.</summary>
public sealed partial class RuntimeWorld : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, SharpEntity> _entities = new(StringComparer.Ordinal);
    private readonly Queue<(Action Execute, Action Cancel)> _pending = new();
    private readonly List<ComponentDiagnostic> _diagnostics = [];
    private readonly SceneData _identity;
    private SceneData _snapshot;
    private RuntimeFrame PublishedFrame = null!;
    private readonly bool OwnsInstance;
    public RuntimeInstance Instance
    {
        get;
    }
    public SubsystemCollection Subsystems
    {
        get;
    } = null!;
    public SharpEngine.Assets.AssetRegistry? Assets
    {
        get;
    }
    public RuntimeFrame Frame
    {
        get
        {
            CheckThread();
            return PublishedFrame;
        }
    }
    private bool _inPhase, _draining, _activationPending, _disposed;
    private double _fixedTime;
    private const double FixedIntervalSeconds = 1.0 / 60;
    public const float FixedDeltaSeconds = 1f / 60f;
    public ComponentRegistry Registry { get; }
    public ulong Revision { get; private set; } = 1;
    public IReadOnlyList<ComponentDiagnostic> Diagnostics => _diagnostics.ToArray();
    public IReadOnlyList<SharpEntity> Entities => _entities.Values.ToArray();
    public PlayerControllerComponent? LocalPlayerController
    {
        get
        {
            CheckThread();
            return _entities.Values.Where(Entity => Entity.ActiveInHierarchy)
                .SelectMany(Entity => Entity.GetComponents<PlayerControllerComponent>())
                .Where(Controller => Controller.IsAlive && Controller.EffectiveEnabled &&
                    Controller.ControlledEntity is not null && Controller.ControlledEntity.IsAlive &&
                    Controller.ControlledEntity.ActiveInHierarchy &&
                    Controller.ControlledEntity.GetComponent<ControllableComponent>() is
                        { ControlEnabled: true, EffectiveEnabled: true })
                .OrderByDescending(Controller => Controller.Priority)
                .ThenBy(Controller => Controller.ComponentId, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }

    public void ClearPlayerInput()
    {
        CheckThread();
        foreach (var Controller in _entities.Values.SelectMany(Entity => Entity.GetComponents<PlayerControllerComponent>()))
        {
            Controller.ClearPlayerInput();
        }
    }
    public SharpEntity? FindEntity(string id) => _entities.GetValueOrDefault(id);
    public SharpComponent? FindComponent(string id) => _entities.Values.SelectMany(e => e.MutableComponents).FirstOrDefault(c => c.ComponentId == id);
    public RuntimeWorld(SceneData Source, ComponentRegistry Registry, SharpEngine.Assets.AssetRegistry? Assets = null, RuntimeInstance? Instance = null)
    {
        this.Registry = Registry;
        this.Assets = Assets ?? Instance?.Assets;
        var Scene = Source.Clone();
        Registry.MigrateScene(Scene);
        Scene.Validate(Registry);
        _identity = Scene.Clone();
        _snapshot = Scene.Clone();
        OwnsInstance = Instance is null;
        this.Instance = Instance ?? new RuntimeInstance(this.Assets);
        this.Instance.Attach(this);
        try
        {
            _inPhase = true;
            Subsystems = new(this.Instance.Engine.Registry, SubsystemScope.World, this, this.Instance.Subsystems);
            CreateBatch(Scene.Entities);
            Boundary();
            Publish();
            this.Instance.Commit();
        }
        catch
        {
            _inPhase = false;
            Dispose();
            throw;
        }
    }
    public SceneData Snapshot() { CheckThread(); return _snapshot.Clone(); }
    public void CheckThread()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RuntimeWorld));
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Runtime mutations require the World thread");
    }
    private RuntimeMutation<T> Schedule<T>(Func<T> operation)
    {
        CheckThread();
        var result = new RuntimeMutation<T>();
        void Execute()
        {
            try { result.Complete(operation()); }
            catch (Exception error) { result.Fail(error); Report("MUTATION_REJECTED", "", "", error); }
        }
        if (_inPhase || _draining) _pending.Enqueue((Execute, () => result.Fail(new ObjectDisposedException(nameof(RuntimeWorld)))));
        else { Execute(); Boundary(); Publish(); }
        return result;
    }
    public RuntimeMutation<SharpEntity> CreateEntity(string name = "Entity", string? parentId = null) => Schedule(() =>
    {
        var staging = Capture();
        var record = staging.CreateEntity(Registry, name, parentId);
        staging.Validate(Registry, validateStateIds: record.Components.Select(c => c.ComponentId).ToHashSet());
        CreateBatch([record]); return _entities[record.EntityId];
    });
    public RuntimeMutation<SharpEntity> Instantiate(SceneData prefab, string? parentId = null)
    {
        var captured = prefab.Clone();
        return Schedule(() =>
        {
            var staging = Capture();
            var records = staging.Instantiate(captured, Registry, parentId);
            staging.Validate(Registry, validateStateIds: records.SelectMany(e => e.Components).Select(c => c.ComponentId).ToHashSet());
            CreateBatch(records); return _entities[records.Single(e => e.ParentId == parentId).EntityId];
        });
    }
    public RuntimeMutation<T> AddComponent<T>(string entityId, JsonObject? initialState = null) where T : SharpComponent
    {
        var captured = (JsonObject?)initialState?.DeepClone();
        return Schedule(() => (T)Add(entityId, Registry.Get(typeof(T)).TypeId, captured));
    }
    public RuntimeMutation<T> AddComponent<T>(string entityId, IComponentInitialState initialState) where T : SharpComponent
    {
        if (initialState.ComponentType != typeof(T)) throw new ArgumentException("Initial State component type mismatch");
        return AddComponent<T>(entityId, initialState.ToState(Registry));
    }
    public RuntimeMutation<SharpComponent> AddComponent(string entityId, string typeId, JsonObject? initialState = null)
    {
        var captured = (JsonObject?)initialState?.DeepClone();
        return Schedule(() => Add(entityId, typeId, captured));
    }
    private SharpComponent Add(string entityId, string typeId, JsonObject? initial)
    {
        var staging = Capture();
        var records = staging.AddComponent(Registry, entityId, typeId, initial);
        staging.Validate(Registry, validateStateIds: records.Select(c => c.ComponentId).ToHashSet());
        var owner = _entities[entityId];
        var added = records.Select(record => Construct(owner, record)).ToArray();
        foreach (var (component, record) in added.Zip(records)) Registry.Write(component, record.State, this);
        Initialize(added);
        return added.Single(c => c.TypeId == typeId);
    }
    public RuntimeMutation<bool> RemoveComponent(string entityId, string componentId) => Schedule(() =>
    {
        var owner = _entities[entityId];
        var component = owner.MutableComponents.Single(c => c.ComponentId == componentId);
        var staging = Capture(); staging.RemoveComponent(Registry, entityId, componentId);
        Remove([component], []); return true;
    });
    public RuntimeMutation<bool> DestroyEntity(string entityId) => Schedule(() =>
    {
        var staging = Capture();
        var ids = staging.Subtree(entityId).Select(e => e.EntityId).ToArray();
        var entities = ids.Select(id => _entities[id]).ToArray();
        Remove(entities.SelectMany(e => e.MutableComponents).ToArray(), entities);
        return true;
    });
    public RuntimeMutation<bool> Patch(string componentId, JsonObject changes, ulong expectedRevision)
    {
        var captured = (JsonObject)changes.DeepClone();
        return Schedule(() =>
        {
            if (expectedRevision != Revision) throw new InvalidOperationException("REVISION_CONFLICT");
            var component = FindComponent(componentId) ?? throw new InvalidOperationException("Component not found");
            var state = Registry.Read(component);
            foreach (var (name, value) in captured) state[name] = value?.DeepClone();
            Registry.ValidateState(component.Descriptor, state, (id, type) =>
                type == typeof(SharpEntity) ? FindEntity(id) is not null : FindComponent(id) is { } target && type.IsInstanceOfType(target));
            Registry.Write(component, state, this); return true;
        });
    }
    public RuntimeMutation<bool> Reset(string componentId, ulong expectedRevision)
    {
        var component = FindComponent(componentId) ?? throw new InvalidOperationException("Component not found");
        return Patch(componentId, component.Descriptor.CreateDefaultState(Registry), expectedRevision);
    }
    public RuntimeMutation<bool> Reparent(string entityId, string? parentId) => Schedule(() =>
    {
        var staging = Capture();
        staging.Entities.Single(e => e.EntityId == entityId).ParentId = parentId;
        staging.Validate(Registry);
        _entities[entityId].Parent = parentId is null ? null : _entities[parentId];
        RequestActivation(); return true;
    });
    internal void RequestActivation()
    {
        CheckThread(); _activationPending = true;
        if (!_inPhase && !_draining) { Boundary(); Publish(); }
    }
    private void CreateBatch(IEnumerable<EntityRecord> input)
    {
        var records = input.ToArray();
        var added = new List<SharpComponent>();
        _inPhase = true;
        try
        {
            foreach (var record in records)
            {
                var entity = new SharpEntity(this, record.EntityId, record.Name);
                _entities.Add(entity.EntityId, entity);
                entity.ActiveSelf = record.ActiveSelf;
                foreach (var component in record.Components)
                    if (Registry.Find(component.TypeId) is not null) added.Add(Construct(entity, component));
            }
            foreach (var record in records)
            {
                _entities[record.EntityId].Parent = record.ParentId is null ? null : _entities[record.ParentId];
                foreach (var component in record.Components)
                    if (FindComponent(component.ComponentId) is { } instance) Registry.Write(instance, component.State, this);
            }
        }
        finally { _inPhase = false; }
        Initialize(added);
    }
    private SharpComponent Construct(SharpEntity entity, ComponentRecord record)
    {
        var descriptor = Registry.Find(record.TypeId)!;
        var component = descriptor.Factory();
        component.Owner = entity; component.ComponentId = record.ComponentId; component.Descriptor = descriptor;
        component.SetInitialEnabled(record.Enabled); component.IsAlive = true;
        entity.MutableComponents.Add(component); return component;
    }
    private void Initialize(IEnumerable<SharpComponent> components)
    {
        var batch = components.ToArray();
        Phase("Initialize", batch, before: component => component.Initialized = true);
        _activationPending = true;
    }
    private void Phase(string name, IEnumerable<SharpComponent> components, float delta = 0, Action<SharpComponent>? before = null)
    {
        _inPhase = true;
        try
        {
            // Stable identity order deliberately does not derive from Inspector presentation order.
            foreach (var component in components.OrderBy(c => c.ComponentId, StringComparer.Ordinal).ToArray())
            {
                before?.Invoke(component);
                try { component.Invoke(name, delta); }
                catch (Exception error) { Report("COMPONENT_CALLBACK_EXCEPTION", component.ComponentId, name, error); }
            }
        }
        finally { _inPhase = false; }
    }
    private void Boundary()
    {
        if (_draining) return;
        _draining = true;
        try
        {
            // Bound user-generated chains; remaining work waits for the next boundary.
            for (int iteration = 0; iteration < 1024; iteration++)
            {
                if (_activationPending)
                {
                    _activationPending = false;
                    var components = _entities.Values.SelectMany(e => e.MutableComponents).Where(c => c.Initialized).ToArray();
                    bool Effective(SharpComponent c) => c.Enabled && c.Entity.ActiveInHierarchy;
                    var disabled = components.Where(c => c.EffectiveEnabled && !Effective(c)).ToArray();
                    var enabled = components.Where(c => !c.EffectiveEnabled && Effective(c)).ToArray();
                    Phase("Disable", disabled, before: c => c.EffectiveEnabled = false);
                    Phase("Enable", enabled, before: c => c.EffectiveEnabled = true);
                    var started = components.Where(c => c.EffectiveEnabled && Effective(c) && !c.Started).ToArray();
                    Phase("Start", started, before: c => c.Started = true);
                }
                if (_pending.TryDequeue(out var mutation)) mutation.Execute();
                else if (!_activationPending) return;
            }
            Report("RUNTIME_BOUNDARY_LIMIT", "", "", new InvalidOperationException("Lifecycle mutation chain exceeded boundary budget; remaining requests deferred"));
        }
        finally { _draining = false; }
    }
    public void Tick(float DeltaSeconds)
    {
        CheckThread();
        if (_inPhase || _draining)
        {
            throw new InvalidOperationException("World ticks cannot run inside lifecycle callbacks.");
        }
        if (!float.IsFinite(DeltaSeconds) || DeltaSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DeltaSeconds));
        }
        Boundary();
        // New additions during this tick start updating on the following tick.
        var Eligible = _entities.Values.SelectMany(e => e.MutableComponents).Where(Component => Component.Started).ToArray();
        IEnumerable<SharpComponent> Active() => Eligible.Where(Component => Component.IsAlive && Component.EffectiveEnabled);
        _inPhase = true;
        try
        {
            Subsystems.Update(DeltaSeconds);
        }
        finally
        {
            _inPhase = false;
        }
        Boundary();
        _fixedTime += DeltaSeconds;
        while (_fixedTime + 1e-9 >= FixedIntervalSeconds)
        {
            _fixedTime -= FixedIntervalSeconds;
            Phase("FixedUpdate", Active(), FixedDeltaSeconds);
            Boundary();
            AdvanceNavigation(FixedDeltaSeconds, Active().OfType<NavAgentComponent>());
        }
        Phase("Update", Active(), DeltaSeconds);
        Boundary();
        Phase("Evaluate", Active(), DeltaSeconds);
        Boundary();
        _inPhase = true;
        try
        {
            FinalizeAnimationAndMovement(DeltaSeconds, Active().ToArray());
        }
        finally
        {
            _inPhase = false;
        }
        Boundary();
        Phase("LateUpdate", Active(), DeltaSeconds);
        Boundary();
        Publish();
    }
    public void Step() => Tick(FixedDeltaSeconds);
    private void Remove(SharpComponent[] components, SharpEntity[] entities)
    {
        Phase("Disable", components.Where(c => c.EffectiveEnabled), before: c => c.EffectiveEnabled = false);
        Phase("Uninitialize", components.Where(c => c.Initialized));
        var removed = components.Select(c => c.ComponentId).Concat(entities.Select(e => e.EntityId)).ToHashSet();
        foreach (var component in components)
        { component.Owner!.MutableComponents.Remove(component); component.IsAlive = false; component.Owner = null; }
        foreach (var entity in entities) { entity.IsAlive = false; _entities.Remove(entity.EntityId); }
        // Traverse only generated serialized fields. Project caches are deliberately untouched.
        foreach (var component in _entities.Values.SelectMany(e => e.MutableComponents))
        {
            var state = Registry.Read(component);
            foreach (var field in component.Descriptor.Fields)
                state[field.Name] = ComponentValue.Remap(state[field.Name], field.ValueType, Registry, id => removed.Contains(id) ? null : id);
            Registry.Write(component, state, this);
        }
    }
    private SceneData Capture()
    {
        var PlayerCamera = LocalPlayerController?.ViewCamera;
        var ActiveCameraId = PlayerCamera is not null && PlayerCamera.IsAlive &&
            PlayerCamera.Enabled && PlayerCamera.Entity.ActiveInHierarchy
            ? PlayerCamera.Entity.EntityId : _identity.ActiveCameraId;
        var result = new SceneData { WorldId = _identity.WorldId, Name = _identity.Name,
            ActiveCameraId = FindEntity(ActiveCameraId ?? "") is null ? null : ActiveCameraId };
        foreach (var entity in _entities.Values)
        {
            var record = new EntityRecord { EntityId = entity.EntityId, Name = entity.Name, ParentId = entity.Parent?.EntityId, ActiveSelf = entity.ActiveSelf };
            record.Components.AddRange(entity.MutableComponents.Select(c => new ComponentRecord
            { ComponentId = c.ComponentId, TypeId = c.TypeId, SchemaVersion = c.Descriptor.SchemaVersion, Enabled = c.Enabled, State = Registry.Read(c) }));
            result.Entities.Add(record);
        }
        return result;
    }
    private void Publish()
    {
        for (var Attempt = 0; ; Attempt++)
        {
            if (Attempt >= 1024)
            {
                throw new InvalidOperationException("Runtime frame preparation did not reach a stable boundary.");
            }
            var Components = _entities.Values.SelectMany(Entity => Entity.MutableComponents).Where(Component => Component.Started && Component.EffectiveEnabled).ToArray();
            // Zero-delta evaluation reconciles seeks, retargeting and LateUpdate writes.
            Phase("Evaluate", Components);
            if (_pending.Count > 0 || _activationPending)
            {
                Boundary();
                continue;
            }
            Phase("PrepareRender", Components);
            if (_pending.Count > 0 || _activationPending)
            {
                Boundary();
                continue;
            }
            break;
        }
        var States = new Dictionary<string, ComponentFrameState>(StringComparer.Ordinal);
        foreach (var Component in _entities.Values.SelectMany(Entity => Entity.MutableComponents))
        {
            var State = Component.CaptureFrameState();
            if (State is not null)
            {
                States.Add(Component.ComponentId, State);
            }
        }
        _snapshot = Capture();
        Subsystems.Commit();
        Revision++;
        PublishedFrame = new(_snapshot, Revision, States, Subsystems.ToJson());
    }
    private void Report(string code, string id, string stage, Exception error)
    {
        _diagnostics.Add(new(code, id, stage, error.ToString(), Revision: Revision));
        SharpLog.Error("Component", $"{stage} failed", error,
            new Dictionary<string, string>
            {
                ["componentId"] = id,
                ["stage"] = stage,
                ["code"] = code,
                ["revision"] = Revision.ToString(),
            });
    }
    internal void CheckDisposal()
    {
        CheckThread();
        if (_inPhase || _draining)
        {
            throw new InvalidOperationException("Destroy entities through the scheduled mutation API during lifecycle callbacks.");
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        CheckDisposal();
        Instance.Engine.Registry.Run(() =>
        {
        });
        _draining = true;
        try { Remove(_entities.Values.SelectMany(e => e.MutableComponents).ToArray(), _entities.Values.ToArray()); }
        finally
        {
            while (_pending.TryDequeue(out var Pending))
            {
                Pending.Cancel();
            }
            Subsystems?.Dispose();
            _disposed = true;
            _draining = false;
            Instance.Detach(this);
            if (OwnsInstance)
            {
                Instance.Dispose();
            }
        }
    }
}
