using System.Text.Json.Nodes;
using SharpEngine.Components;

namespace SharpEngine.Networking;

public sealed class NetworkReplicationBaseline
{
    internal NetworkReplicationBaseline(ulong Revision, JsonObject State)
    {
        this.Revision = Revision;
        this.State = (JsonObject)State.DeepClone();
    }

    public ulong Revision
    {
        get;
    }

    internal JsonObject State
    {
        get;
    }
}

public sealed class NetworkSnapshot
{
    internal NetworkSnapshot(ulong Revision, ulong BaselineRevision, bool IsFull,
        JsonObject Body, JsonObject CurrentState)
    {
        this.Revision = Revision;
        this.BaselineRevision = BaselineRevision;
        this.IsFull = IsFull;
        this.Body = (JsonObject)Body.DeepClone();
        this.CurrentState = (JsonObject)CurrentState.DeepClone();
    }

    public ulong Revision
    {
        get;
    }

    public ulong BaselineRevision
    {
        get;
    }

    public bool IsFull
    {
        get;
    }

    private JsonObject Body
    {
        get;
    }

    private JsonObject CurrentState
    {
        get;
    }

    public NetworkReplicationBaseline CreateBaseline()
    {
        return new NetworkReplicationBaseline(Revision, CurrentState);
    }

    public JsonObject ToJson()
    {
        return (JsonObject)Body.DeepClone();
    }
}

public sealed class NetworkReplicationFrameBuilder
{
    private readonly NetworkSchemaCatalog Catalog;

    public NetworkReplicationFrameBuilder(NetworkSchemaCatalog Catalog)
    {
        this.Catalog = Catalog ?? throw new ArgumentNullException(nameof(Catalog));
    }

    public NetworkSnapshot Build(RuntimeFrame Frame, NetworkReplicationBaseline? Baseline = null,
        IReadOnlySet<string>? RelevantEntityIds = null)
    {
        ArgumentNullException.ThrowIfNull(Frame);
        var CurrentState = BuildState(Frame, RelevantEntityIds);
        if (Baseline is null)
        {
            var FullBody = new JsonObject
            {
                ["schemaVersion"] = NetworkProtocol.SchemaVersion,
                ["mode"] = "full",
                ["revision"] = Frame.Revision,
                ["baselineRevision"] = 0,
                ["entities"] = CurrentState["entities"]!.DeepClone(),
            };
            return new NetworkSnapshot(Frame.Revision, 0, true, FullBody, CurrentState);
        }

        var Delta = BuildDelta(Baseline.State, CurrentState);
        var DeltaBody = new JsonObject
        {
            ["schemaVersion"] = NetworkProtocol.SchemaVersion,
            ["mode"] = "delta",
            ["revision"] = Frame.Revision,
            ["baselineRevision"] = Baseline.Revision,
            ["spawned"] = Delta.Spawned,
            ["destroyed"] = Delta.Destroyed,
            ["updated"] = Delta.Updated,
        };
        return new NetworkSnapshot(Frame.Revision, Baseline.Revision, false, DeltaBody, CurrentState);
    }

    private JsonObject BuildState(RuntimeFrame Frame, IReadOnlySet<string>? RelevantEntityIds)
    {
        var Scene = Frame.Snapshot();
        var Entities = new JsonArray();
        foreach (var Entity in Scene.Entities.Where(Entity => RelevantEntityIds is null || RelevantEntityIds.Contains(Entity.EntityId))
                     .OrderBy(Entity => Entity.EntityId, StringComparer.Ordinal))
        {
            var Components = new JsonArray();
            foreach (var Component in Entity.Components.OrderBy(Component => Component.ComponentId, StringComparer.Ordinal))
            {
                var Descriptor = Catalog.FindComponent(Component.TypeId);
                if (Descriptor is null)
                {
                    continue;
                }

                Components.Add(BuildComponent(Component, Descriptor));
            }

            Entities.Add(new JsonObject
            {
                ["entityId"] = Entity.EntityId,
                ["name"] = Entity.Name,
                ["parentId"] = Entity.ParentId,
                ["activeSelf"] = Entity.ActiveSelf,
                ["components"] = Components,
            });
        }

        return new JsonObject
        {
            ["schemaVersion"] = NetworkProtocol.SchemaVersion,
            ["revision"] = Frame.Revision,
            ["entities"] = Entities,
        };
    }

    private static JsonObject BuildComponent(ComponentRecord Component, NetworkComponentDescriptor Descriptor)
    {
        var State = new JsonObject();
        foreach (var Property in Descriptor.Properties)
        {
            State[Property.PropertyId] = Component.State[Property.FieldName]?.DeepClone();
        }

        return new JsonObject
        {
            ["componentId"] = Component.ComponentId,
            ["typeId"] = Component.TypeId,
            ["schemaVersion"] = Component.SchemaVersion,
            ["enabled"] = Component.Enabled,
            ["state"] = State,
        };
    }

    private static (JsonArray Spawned, JsonArray Destroyed, JsonArray Updated) BuildDelta(
        JsonObject PreviousState, JsonObject CurrentState)
    {
        var PreviousEntities = EntityMap(PreviousState);
        var CurrentEntities = EntityMap(CurrentState);
        var Spawned = new JsonArray();
        var Destroyed = new JsonArray();
        var Updated = new JsonArray();

        foreach (var Entry in CurrentEntities.OrderBy(Entry => Entry.Key, StringComparer.Ordinal))
        {
            var Exists = PreviousEntities.TryGetValue(Entry.Key, out var Previous);
            if (!Exists)
            {
                Spawned.Add(Entry.Value.DeepClone());
                continue;
            }

            var Change = BuildEntityDelta(Previous!, Entry.Value);
            if (Change is not null)
            {
                Updated.Add(Change);
            }
        }

        foreach (var EntityId in PreviousEntities.Keys.Except(CurrentEntities.Keys, StringComparer.Ordinal).OrderBy(Id => Id, StringComparer.Ordinal))
        {
            Destroyed.Add(EntityId);
        }

        return (Spawned, Destroyed, Updated);
    }

    private static Dictionary<string, JsonObject> EntityMap(JsonObject State)
    {
        return State["entities"]!.AsArray().Select(Node => Node!.AsObject())
            .ToDictionary(Entity => Entity["entityId"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    private static JsonObject? BuildEntityDelta(JsonObject Previous, JsonObject Current)
    {
        var Changes = new JsonObject();
        foreach (var Field in new[] { "name", "parentId", "activeSelf" })
        {
            if (!JsonNode.DeepEquals(Previous[Field], Current[Field]))
            {
                Changes[Field] = Current[Field]?.DeepClone();
            }
        }

        var PreviousComponents = ComponentMap(Previous);
        var CurrentComponents = ComponentMap(Current);
        var Added = new JsonArray();
        var Removed = new JsonArray();
        var Changed = new JsonArray();
        foreach (var Entry in CurrentComponents.OrderBy(Entry => Entry.Key, StringComparer.Ordinal))
        {
            var Exists = PreviousComponents.TryGetValue(Entry.Key, out var PreviousComponent);
            if (!Exists)
            {
                Added.Add(Entry.Value.DeepClone());
                continue;
            }

            var ComponentChange = BuildComponentDelta(PreviousComponent!, Entry.Value);
            if (ComponentChange is not null)
            {
                Changed.Add(ComponentChange);
            }
        }

        foreach (var ComponentId in PreviousComponents.Keys.Except(CurrentComponents.Keys, StringComparer.Ordinal).OrderBy(Id => Id, StringComparer.Ordinal))
        {
            Removed.Add(ComponentId);
        }

        if (Changes.Count == 0 && Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0)
        {
            return null;
        }

        Changes["entityId"] = Current["entityId"]!.DeepClone();
        if (Added.Count != 0)
        {
            Changes["addedComponents"] = Added;
        }

        if (Removed.Count != 0)
        {
            Changes["removedComponentIds"] = Removed;
        }

        if (Changed.Count != 0)
        {
            Changes["changedComponents"] = Changed;
        }

        return Changes;
    }

    private static Dictionary<string, JsonObject> ComponentMap(JsonObject Entity)
    {
        return Entity["components"]!.AsArray().Select(Node => Node!.AsObject())
            .ToDictionary(Component => Component["componentId"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    private static JsonObject? BuildComponentDelta(JsonObject Previous, JsonObject Current)
    {
        var Changes = new JsonObject();
        if (!JsonNode.DeepEquals(Previous["enabled"], Current["enabled"]))
        {
            Changes["enabled"] = Current["enabled"]?.DeepClone();
        }

        if (!JsonNode.DeepEquals(Previous["typeId"], Current["typeId"]) ||
            !JsonNode.DeepEquals(Previous["schemaVersion"], Current["schemaVersion"]))
        {
            Changes["replacement"] = Current.DeepClone();
        }
        else
        {
            var PreviousState = Previous["state"]!.AsObject();
            var CurrentState = Current["state"]!.AsObject();
            var PropertyChanges = new JsonObject();
            foreach (var PropertyId in PreviousState.Select(Entry => Entry.Key)
                         .Concat(CurrentState.Select(Entry => Entry.Key)).Distinct(StringComparer.Ordinal).OrderBy(Id => Id, StringComparer.Ordinal))
            {
                if (!JsonNode.DeepEquals(PreviousState[PropertyId], CurrentState[PropertyId]))
                {
                    PropertyChanges[PropertyId] = CurrentState[PropertyId]?.DeepClone();
                }
            }

            if (PropertyChanges.Count != 0)
            {
                Changes["properties"] = PropertyChanges;
            }
        }

        if (Changes.Count == 0)
        {
            return null;
        }

        Changes["componentId"] = Current["componentId"]!.DeepClone();
        return Changes;
    }
}
