using System.Text.Json.Nodes;
using SharpEngine.Logging;

namespace SharpEngine.Subsystems;

public sealed class SubsystemCollection
{
    internal sealed class Entry(SubsystemDescriptor Definition, Subsystem Instance)
    {
        internal readonly SubsystemDescriptor Definition = Definition;
        internal readonly Subsystem Instance = Instance;
        internal bool InitializeAttempted;
        internal JsonObject State = new();
        internal string? Code;
        internal string? Message;
    }
    internal readonly List<Entry> Entries = new();
    private readonly SubsystemRegistry Registry;
    private JsonObject Published = new();
    private ulong Revision;
    private bool Disposed;
    public string Id
    {
        get;
    } = Guid.NewGuid().ToString("N");
    public SubsystemScope Scope
    {
        get;
    }
    public object Owner
    {
        get;
    }
    public SubsystemCollection? Parent
    {
        get;
    }

    internal SubsystemCollection(SubsystemRegistry Registry, SubsystemScope Scope, object Owner, SubsystemCollection? Parent)
    {
        this.Registry = Registry;
        this.Scope = Scope;
        this.Owner = Owner;
        this.Parent = Parent;
        Registry.Attach(this);
    }

    /// <summary>Looks up this scope, then its fixed parent chain. Never creates a subsystem lazily.</summary>
    public T? Get<T>() where T : Subsystem
    {
        CheckAccess();
        return Entries.Select(Entry => Entry.Instance).OfType<T>().SingleOrDefault() ?? Parent?.Get<T>();
    }

    public JsonObject ToJson()
    {
        CheckAccess();
        return (JsonObject)Published.DeepClone();
    }

    internal Entry Construct(SubsystemDescriptor Definition)
    {
        try
        {
            var Instance = (Subsystem)Activator.CreateInstance(Definition.Type)!;
            Instance.Collection = this;
            var Entry = new Entry(Definition, Instance);
            Entries.Add(Entry);
            return Entry;
        }
        catch (Exception Error)
        {
            throw new SubsystemException("SUBSYSTEM_CONSTRUCTION_FAILED", Definition.TypeId, "Subsystem construction failed.", Error);
        }
    }

    internal void Initialize(Entry Entry)
    {
        Entry.InitializeAttempted = true;
        try
        {
            Entry.Instance.Initialize();
            Entry.Instance.IsInitialized = true;
        }
        catch (Exception Error)
        {
            throw new SubsystemException("SUBSYSTEM_INITIALIZATION_FAILED", Entry.Definition.TypeId, "Subsystem initialization failed.", Error);
        }
    }

    internal void Remove(Entry Entry)
    {
        if (Entry.InitializeAttempted)
        {
            try
            {
                Entry.Instance.Uninitialize();
            }
            catch (Exception Error)
            {
                SharpLog.Error("Subsystem", $"SUBSYSTEM_UNINITIALIZATION_FAILED: {Entry.Definition.TypeId}", Error);
            }
        }
        Entry.Instance.IsInitialized = false;
        Entry.Instance.Collection = null;
        Entries.Remove(Entry);
    }

    internal void Clear()
    {
        foreach (var Entry in Entries.AsEnumerable().Reverse().ToArray())
        {
            Remove(Entry);
        }
    }

    internal void Update(float DeltaSeconds)
    {
        CheckAccess();
        if (!float.IsFinite(DeltaSeconds) || DeltaSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DeltaSeconds));
        }
        Registry.Run(() =>
        {
            foreach (var Entry in Entries.OrderBy(Entry => Entry.Definition.TypeId, StringComparer.Ordinal))
            {
                try
                {
                    Entry.Instance.Update(DeltaSeconds);
                    Entry.Code = null;
                    Entry.Message = null;
                }
                catch (Exception Error)
                {
                    Entry.Code = "SUBSYSTEM_UPDATE_FAILED";
                    Entry.Message = Error.Message;
                    SharpLog.Error("Subsystem", $"{Entry.Code}: {Entry.Definition.TypeId}", Error);
                }
            }
        });
    }

    internal void Publish()
    {
        foreach (var Entry in Entries)
        {
            try
            {
                var State = Entry.Instance.Capture();
                _ = State.ToJsonString();
                Entry.State = (JsonObject)State.DeepClone();
                if (Entry.Code == "SUBSYSTEM_CAPTURE_FAILED")
                {
                    Entry.Code = null;
                    Entry.Message = null;
                }
            }
            catch (Exception Error)
            {
                Entry.Code = "SUBSYSTEM_CAPTURE_FAILED";
                Entry.Message = Error.Message;
            }
        }
        var Snapshot = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["scopeId"] = Id,
            ["scope"] = Scope.ToString(),
            ["parentScopeId"] = Parent?.Id,
            ["subsystems"] = new JsonArray(Entries.OrderBy(Entry => Entry.Definition.TypeId, StringComparer.Ordinal).Select(Entry => (JsonNode)new JsonObject
            {
                ["typeId"] = Entry.Definition.TypeId,
                ["schemaVersion"] = Entry.Definition.SchemaVersion,
                ["moduleId"] = Entry.Definition.ModuleId,
                ["runtimeType"] = Entry.Definition.Type.FullName,
                ["status"] = Entry.Code is null ? "Initialized" : "Faulted",
                ["code"] = Entry.Code,
                ["message"] = Entry.Message,
                ["state"] = Entry.State.DeepClone(),
            }).ToArray()),
        };
        var Previous = (JsonObject)Published.DeepClone();
        Previous.Remove("stateRevision");
        if (Snapshot.ToJsonString() != Previous.ToJsonString())
        {
            Revision++;
        }
        Snapshot["stateRevision"] = Revision;
        Published = Snapshot;
    }

    internal void Commit()
    {
        CheckAccess();
        Registry.Capture(Publish);
    }

    private void CheckAccess()
    {
        Registry.CheckThread();
        ObjectDisposedException.ThrowIf(Disposed, this);
    }

    internal void Dispose()
    {
        if (Disposed)
        {
            return;
        }
        CheckAccess();
        Registry.Detach(this);
        Disposed = true;
    }
}
