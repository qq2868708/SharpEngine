using System.Reflection;
using System.Text.Json.Nodes;
using SharpEngine.Modules;

namespace SharpEngine.Subsystems;

internal sealed record SubsystemDescriptor(string TypeId, int SchemaVersion, Type Type, SubsystemScope Scope, string ModuleId);

/// <summary>Definitions belong to this engine lifetime, never to a process-wide static singleton.</summary>
public sealed class SubsystemRegistry
{
    private readonly int ThreadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, SubsystemDescriptor> Definitions = new(StringComparer.Ordinal);
    private readonly List<SubsystemCollection> Collections = new();
    private bool Busy;
    private bool Disposed;
    private ulong Revision;
    private JsonObject Catalog = new()
    {
        ["schemaVersion"] = 1,
        ["stateRevision"] = 0ul,
        ["types"] = new JsonArray(),
    };

    public JsonObject ToJson()
    {
        CheckThread();
        return (JsonObject)Catalog.DeepClone();
    }

    public IDisposable RegisterAssembly(Assembly Assembly, string ModuleId, string ModuleType = "Runtime")
    {
        return RegisterTypes(ModuleId, ModuleType, Assembly.GetTypes().Where(Type =>
            typeof(Subsystem).IsAssignableFrom(Type) && !Type.IsAbstract).ToArray());
    }

    /// <summary>Read-only reload preflight. Existing definitions from these module ids will be replaced.</summary>
    public void ValidateModules(IEnumerable<(Assembly Assembly, string Id, string Type)> Modules)
    {
        CheckThread();
        var Candidates = Modules.ToArray();
        var Replaced = Candidates.Select(Module => Module.Id).ToHashSet(StringComparer.Ordinal);
        var Descriptors = Candidates.SelectMany(Module => Module.Assembly.GetTypes()
            .Where(Type => typeof(Subsystem).IsAssignableFrom(Type) && !Type.IsAbstract)
            .Select(Type => Describe(Type, Module.Id, Module.Type))).ToArray();
        var Combined = Definitions.Values.Where(Definition => !Replaced.Contains(Definition.ModuleId)).Concat(Descriptors).ToArray();
        if (Combined.Select(Definition => Definition.TypeId).Distinct(StringComparer.Ordinal).Count() != Combined.Length ||
            Combined.Select(Definition => Definition.Type).Distinct().Count() != Combined.Length)
        {
            throw new SubsystemException("SUBSYSTEM_DUPLICATE", "", "Reload contains duplicate subsystem ids or concrete types.");
        }
    }

    public IDisposable RegisterTypes(string ModuleId, string ModuleType, params Type[] Types)
    {
        CheckThread();
        if (string.IsNullOrWhiteSpace(ModuleId) || ModuleType is not ("Runtime" or "Editor"))
        {
            throw new SubsystemException("SUBSYSTEM_MODULE", ModuleId, "A stable module id and Runtime/Editor module type are required.");
        }
        var Added = Types.Select(Type => Describe(Type, ModuleId, ModuleType)).OrderBy(Definition => Definition.TypeId, StringComparer.Ordinal).ToArray();
        if (Added.Length == 0)
        {
            return ModuleRegistrationScope.Track(new Registration(this, Added));
        }
        if (Added.Select(Definition => Definition.TypeId).Distinct(StringComparer.Ordinal).Count() != Added.Length ||
            Added.Any(Definition => Definitions.ContainsKey(Definition.TypeId) || Definitions.Values.Any(Existing => Existing.Type == Definition.Type)))
        {
            throw new SubsystemException("SUBSYSTEM_DUPLICATE", ModuleId, "Subsystem ids and concrete types must be unique in an engine lifetime.");
        }
        Run(() =>
        {
            var Created = new List<(SubsystemCollection Collection, SubsystemCollection.Entry Entry)>();
            try
            {
                foreach (var Definition in Added)
                {
                    Definitions.Add(Definition.TypeId, Definition);
                }
                foreach (var Collection in Collections.OrderBy(Collection => Collection.Scope))
                {
                    foreach (var Definition in Added.Where(Definition => Definition.Scope == Collection.Scope))
                    {
                        Created.Add((Collection, Collection.Construct(Definition)));
                    }
                }
                foreach (var Item in Created)
                {
                    Item.Collection.Initialize(Item.Entry);
                }
                foreach (var Collection in Collections)
                {
                    Collection.Publish();
                }
                PublishCatalog();
            }
            catch
            {
                foreach (var Item in Created.AsEnumerable().Reverse())
                {
                    Item.Collection.Remove(Item.Entry);
                }
                foreach (var Definition in Added)
                {
                    Definitions.Remove(Definition.TypeId);
                }
                throw;
            }
        });
        return ModuleRegistrationScope.Track(new Registration(this, Added));
    }

    private static SubsystemDescriptor Describe(Type Type, string ModuleId, string ModuleType)
    {
        var Metadata = Type.GetCustomAttribute<SubsystemAttribute>();
        var Scope = Type.GetCustomAttribute<SubsystemScopeAttribute>();
        if (!typeof(Subsystem).IsAssignableFrom(Type) || Type.IsAbstract || Type.ContainsGenericParameters || !Type.IsVisible ||
            Type.GetConstructor(Type.EmptyTypes) is null || Metadata is null || string.IsNullOrWhiteSpace(Metadata.Id) || Metadata.SchemaVersion < 1 ||
            Scope is null || !Enum.IsDefined(Scope.Scope))
        {
            throw new SubsystemException("SUBSYSTEM_DESCRIPTOR", Type.FullName ?? Type.Name, "A public concrete subsystem needs a parameterless constructor, stable id, positive version and fixed scope base.");
        }
        if (Scope.Scope == SubsystemScope.Editor && ModuleType != "Editor")
        {
            throw new SubsystemException("SUBSYSTEM_EDITOR_MODULE", Metadata.Id, "Editor subsystems must belong to an Editor module.");
        }
        return new(Metadata.Id, Metadata.SchemaVersion, Type, Scope.Scope, ModuleId);
    }

    internal void Attach(SubsystemCollection Collection)
    {
        Run(() =>
        {
            try
            {
                foreach (var Definition in Definitions.Values.Where(Definition => Definition.Scope == Collection.Scope).OrderBy(Definition => Definition.TypeId, StringComparer.Ordinal))
                {
                    Collection.Construct(Definition);
                }
                foreach (var Entry in Collection.Entries.ToArray())
                {
                    Collection.Initialize(Entry);
                }
                Collection.Publish();
                Collections.Add(Collection);
            }
            catch
            {
                Collection.Clear();
                throw;
            }
        });
    }

    internal void Detach(SubsystemCollection Collection)
    {
        Run(() =>
        {
            Collection.Clear();
            Collections.Remove(Collection);
        });
    }

    private void Unregister(SubsystemDescriptor[] Removed)
    {
        if (Disposed || Removed.Length == 0)
        {
            return;
        }
        Run(() =>
        {
            foreach (var Collection in Collections.OrderByDescending(Collection => Collection.Scope))
            {
                foreach (var Definition in Removed.Reverse())
                {
                    var Entry = Collection.Entries.SingleOrDefault(Entry => Entry.Definition == Definition);
                    if (Entry is not null)
                    {
                        Collection.Remove(Entry);
                    }
                }
            }
            foreach (var Definition in Removed)
            {
                Definitions.Remove(Definition.TypeId);
            }
            foreach (var Collection in Collections)
            {
                Collection.Publish();
            }
            PublishCatalog();
        });
    }

    private void PublishCatalog()
    {
        Catalog = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["stateRevision"] = ++Revision,
            ["types"] = new JsonArray(Definitions.Values.OrderBy(Definition => Definition.TypeId, StringComparer.Ordinal).Select(Definition => (JsonNode)new JsonObject
            {
                ["typeId"] = Definition.TypeId,
                ["schemaVersion"] = Definition.SchemaVersion,
                ["scope"] = Definition.Scope.ToString(),
                ["moduleId"] = Definition.ModuleId,
                ["runtimeType"] = Definition.Type.FullName,
            }).ToArray()),
        };
    }

    internal void CheckThread()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (ThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("Subsystem access requires its engine thread.");
        }
    }

    internal void Run(Action Action)
    {
        CheckThread();
        if (Busy)
        {
            throw new SubsystemException("SUBSYSTEM_REENTRANCY", "", "Lifecycle callbacks cannot mutate subsystem registrations or scopes.");
        }
        Busy = true;
        try
        {
            Action();
        }
        finally
        {
            Busy = false;
        }
    }

    internal void Close()
    {
        CheckThread();
        if (Collections.Count != 0)
        {
            throw new InvalidOperationException("Dispose subsystem owners before their registry.");
        }
        Definitions.Clear();
        Disposed = true;
    }

    internal void Capture(Action Action)
    {
        CheckThread();
        if (Busy)
        {
            Action();
        }
        else
        {
            Run(Action);
        }
    }

    private sealed class Registration(SubsystemRegistry Registry, SubsystemDescriptor[] Definitions) : IDisposable
    {
        private SubsystemDescriptor[]? Registered = Definitions;
        public void Dispose()
        {
            if (Registered is null)
            {
                return;
            }
            Registry.Unregister(Registered);
            Registered = null;
        }
    }
}
