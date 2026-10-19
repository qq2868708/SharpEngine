using System.Text.Json.Nodes;

namespace SharpEngine.Networking;

/// <summary>Atomic read-only network mirror, using PropertyId values. This is not a client RuntimeWorld.</summary>
public sealed class NetworkReplicaState
{
    private readonly NetworkSchemaCatalog Schema;
    private JsonObject State = new()
    {
        ["schemaVersion"] = NetworkProtocol.SchemaVersion,
        ["revision"] = 0UL,
        ["entities"] = new JsonArray(),
    };

    public NetworkReplicaState(NetworkSchemaCatalog Schema)
    {
        this.Schema = Schema ?? throw new ArgumentNullException(nameof(Schema));
    }

    public ulong SnapshotId
    {
        get;
        private set;
    }

    public ulong ServerTick
    {
        get;
        private set;
    }

    public JsonObject ToJson() { return (JsonObject)State.DeepClone(); }

    public void Apply(JsonObject Message)
    {
        ArgumentNullException.ThrowIfNull(Message);
        NetworkWire.RequireVersion(Message);
        var NewSnapshotId = NetworkWire.UInt64(Message, "snapshotId");
        var BaselineId = NetworkWire.UInt64(Message, "baselineId");
        var NewTick = NetworkWire.UInt64(Message, "serverTick");
        var Body = Message["snapshot"]?.AsObject() ?? throw new InvalidDataException("Snapshot body is required");
        NetworkWire.RequireVersion(Body);
        var Mode = NetworkWire.String(Body, "mode");
        var Revision = NetworkWire.UInt64(Body, "revision");
        if (NewSnapshotId <= SnapshotId || NewTick < ServerTick || Revision < NetworkWire.UInt64(State, "revision") ||
            (Mode != "full" && Mode != "delta"))
        {
            throw new InvalidDataException("Snapshot sequence is invalid");
        }

        JsonObject Staging;
        if (Mode == "full")
        {
            if (BaselineId != 0 || NetworkWire.UInt64(Body, "baselineRevision") != 0)
            {
                throw new InvalidDataException("Full snapshot cannot have a baseline");
            }

            Staging = new JsonObject
            {
                ["schemaVersion"] = NetworkProtocol.SchemaVersion,
                ["revision"] = Revision,
                ["entities"] = Array(Body, "entities").DeepClone(),
            };
        }
        else
        {
            if (SnapshotId == 0 || BaselineId != SnapshotId || NetworkWire.UInt64(Body, "baselineRevision") != NetworkWire.UInt64(State, "revision"))
            {
                throw new InvalidDataException("Snapshot baseline does not match the committed replica");
            }

            Staging = (JsonObject)State.DeepClone();
            var Entities = Map(Array(Staging, "entities"), "entityId");
            foreach (var Node in Array(Body, "destroyed"))
            {
                var Id = Node?.GetValue<string>() ?? throw new InvalidDataException("Destroyed entity id is required");
                if (!Entities.Remove(Id))
                {
                    throw new InvalidDataException("Delta destroys an unknown entity");
                }
            }

            foreach (var Node in Array(Body, "spawned"))
            {
                var Entity = Node!.AsObject();
                Entities.Add(NetworkWire.String(Entity, "entityId"), (JsonObject)Entity.DeepClone());
            }

            foreach (var Node in Array(Body, "updated"))
            {
                var Change = Node!.AsObject();
                var EntityId = NetworkWire.String(Change, "entityId");
                var Found = Entities.TryGetValue(EntityId, out var Entity);
                if (!Found)
                {
                    throw new InvalidDataException("Delta updates an unknown entity");
                }

                ApplyEntity(Entity!, Change);
            }

            Staging["entities"] = new JsonArray(Entities.OrderBy(Entry => Entry.Key, StringComparer.Ordinal)
                .Select(Entry => (JsonNode)Entry.Value.DeepClone()).ToArray());
            Staging["revision"] = Revision;
        }

        Validate(Staging);
        State = Staging;
        SnapshotId = NewSnapshotId;
        ServerTick = NewTick;
    }

    private static void ApplyEntity(JsonObject Entity, JsonObject Change)
    {
        RequireFields(Change, new HashSet<string>(["entityId", "name", "parentId", "activeSelf", "addedComponents", "removedComponentIds", "changedComponents"], StringComparer.Ordinal));
        foreach (var Field in new[] { "name", "parentId", "activeSelf" })
        {
            if (Change.ContainsKey(Field))
            {
                Entity[Field] = Change[Field]?.DeepClone();
            }
        }

        var Components = Map(Array(Entity, "components"), "componentId");
        foreach (var Node in Change["removedComponentIds"]?.AsArray() ?? new JsonArray())
        {
            var Removed = Components.Remove(Node!.GetValue<string>());
            if (!Removed)
            {
                throw new InvalidDataException("Delta removes an unknown component");
            }
        }

        foreach (var Node in Change["addedComponents"]?.AsArray() ?? new JsonArray())
        {
            var Component = Node!.AsObject();
            Components.Add(NetworkWire.String(Component, "componentId"), (JsonObject)Component.DeepClone());
        }

        foreach (var Node in Change["changedComponents"]?.AsArray() ?? new JsonArray())
        {
            var ComponentChange = Node!.AsObject();
            RequireFields(ComponentChange, new HashSet<string>(["componentId", "enabled", "replacement", "properties"], StringComparer.Ordinal));
            var ComponentId = NetworkWire.String(ComponentChange, "componentId");
            var Found = Components.TryGetValue(ComponentId, out var Component);
            if (!Found)
            {
                throw new InvalidDataException("Delta updates an unknown component");
            }

            var Replacement = ComponentChange["replacement"] as JsonObject;
            if (Replacement is not null)
            {
                if (NetworkWire.String(Replacement, "componentId") != ComponentId)
                {
                    throw new InvalidDataException("Component replacement cannot change identity");
                }

                Components[ComponentId] = (JsonObject)Replacement.DeepClone();
                continue;
            }

            if (ComponentChange.ContainsKey("enabled"))
            {
                Component!["enabled"] = ComponentChange["enabled"]?.DeepClone();
            }

            foreach (var Property in ComponentChange["properties"]?.AsObject() ?? new JsonObject())
            {
                var Properties = Component!["state"]!.AsObject();
                if (!Properties.ContainsKey(Property.Key))
                {
                    throw new InvalidDataException("Delta updates an unknown property");
                }

                Properties[Property.Key] = Property.Value?.DeepClone();
            }
        }

        Entity["components"] = new JsonArray(Components.OrderBy(Entry => Entry.Key, StringComparer.Ordinal)
            .Select(Entry => (JsonNode)Entry.Value.DeepClone()).ToArray());
    }

    private void Validate(JsonObject Candidate)
    {
        var Entities = Map(Array(Candidate, "entities"), "entityId");
        var ComponentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Entity in Entities.Values)
        {
            RequireFields(Entity, new HashSet<string>(["entityId", "name", "parentId", "activeSelf", "components"], StringComparer.Ordinal));
            _ = Entity["name"]!.GetValue<string>();
            _ = Entity["activeSelf"]!.GetValue<bool>();
            _ = Entity["parentId"]?.GetValue<string>();
            foreach (var Component in Map(Array(Entity, "components"), "componentId").Values)
            {
                RequireFields(Component, new HashSet<string>(["componentId", "typeId", "schemaVersion", "enabled", "state"], StringComparer.Ordinal));
                var Unique = ComponentIds.Add(NetworkWire.String(Component, "componentId"));
                var Descriptor = Schema.FindComponent(NetworkWire.String(Component, "typeId"));
                if (!Unique || Descriptor is null || Component["schemaVersion"]?.GetValue<int>() != Descriptor.SchemaVersion)
                {
                    throw new InvalidDataException("Snapshot component identity or schema is invalid");
                }

                _ = Component["enabled"]!.GetValue<bool>();
                var Properties = Component["state"]!.AsObject();
                var ExpectedProperties = Descriptor.Properties.Select(Property => Property.PropertyId).ToHashSet(StringComparer.Ordinal);
                if (!ExpectedProperties.SetEquals(Properties.Select(Property => Property.Key)))
                {
                    throw new InvalidDataException("Snapshot property set does not match the Schema");
                }
            }
        }
    }

    private static JsonArray Array(JsonObject Value, string Field)
    {
        return Value[Field]?.AsArray() ?? throw new InvalidDataException($"Missing snapshot array {Field}");
    }

    private static void RequireFields(JsonObject Value, IReadOnlySet<string> Allowed)
    {
        if (Value.Any(Field => !Allowed.Contains(Field.Key)))
        {
            throw new InvalidDataException("Snapshot contains an unknown field");
        }
    }

    private static Dictionary<string, JsonObject> Map(JsonArray Values, string IdField)
    {
        return Values.Select(Node => Node?.AsObject() ?? throw new InvalidDataException("Snapshot object is required"))
            .ToDictionary(Value => NetworkWire.String(Value, IdField), Value => Value, StringComparer.Ordinal);
    }
}
