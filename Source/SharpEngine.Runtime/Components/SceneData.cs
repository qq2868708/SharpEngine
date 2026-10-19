using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEngine.Components;

public sealed class ComponentRecord
{
    public string ComponentId { get; set; } = Guid.NewGuid().ToString("N");
    public string TypeId { get; set; } = "";
    public int SchemaVersion { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public JsonObject State { get; set; } = new();
    internal JsonObject? UnknownRecord;
    public JsonObject ToJson() => UnknownRecord is not null ? (JsonObject)UnknownRecord.DeepClone() : new()
    { ["componentId"] = ComponentId, ["typeId"] = TypeId, ["schemaVersion"] = SchemaVersion, ["enabled"] = Enabled, ["state"] = State.DeepClone() };
    public ComponentRecord Clone() => new() { ComponentId = ComponentId, TypeId = TypeId, SchemaVersion = SchemaVersion,
        Enabled = Enabled, State = (JsonObject)State.DeepClone(), UnknownRecord = (JsonObject?)UnknownRecord?.DeepClone() };
    public static ComponentRecord Create(ComponentDescriptor type, ComponentRegistry registry, JsonObject? initialState = null)
    {
        var state = type.CreateDefaultState(registry);
        if (initialState is not null) foreach (var (key, value) in initialState) state[key] = value?.DeepClone();
        registry.ValidateState(type, state);
        return new() { TypeId = type.TypeId, SchemaVersion = type.SchemaVersion, State = state };
    }
}
public sealed class EntityRecord
{
    public string EntityId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Entity";
    public string? ParentId { get; set; }
    public bool ActiveSelf { get; set; } = true;
    public List<ComponentRecord> Components { get; } = [];
    public JsonObject ToJson() => new() { ["entityId"] = EntityId, ["name"] = Name,
        ["parentId"] = ParentId, ["activeSelf"] = ActiveSelf,
        ["components"] = new JsonArray(Components.Select(c => (JsonNode)c.ToJson()).ToArray()) };
    public EntityRecord Clone()
    {
        var result = new EntityRecord { EntityId = EntityId, Name = Name, ParentId = ParentId, ActiveSelf = ActiveSelf };
        result.Components.AddRange(Components.Select(c => c.Clone())); return result;
    }
}

public sealed class SceneData
{
    public const int CurrentSchemaVersion = 3;
    public string WorldId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "World";
    public string? ActiveCameraId { get; set; }
    public bool IsPrefab { get; set; }
    public List<EntityRecord> Entities { get; } = [];
    public JsonObject ToJson() => new()
    {
        ["schemaVersion"] = CurrentSchemaVersion, ["kind"] = IsPrefab ? "prefab" : "world",
        ["worldId"] = WorldId, ["name"] = Name, ["activeCameraId"] = ActiveCameraId,
        ["entities"] = new JsonArray(Entities.Select(e => (JsonNode)e.ToJson()).ToArray()),
    };
    public string Serialize(bool indented = true) => ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
    public SceneData Clone()
    {
        var result = new SceneData { WorldId = WorldId, Name = Name, ActiveCameraId = ActiveCameraId, IsPrefab = IsPrefab };
        result.Entities.AddRange(Entities.Select(e => e.Clone())); return result;
    }
    public EntityRecord CreateEntity(ComponentRegistry registry, string name = "Entity", string? parentId = null)
    {
        if (parentId is not null && Entities.All(e => e.EntityId != parentId)) throw new InvalidDataException("Parent does not exist");
        var entity = new EntityRecord { Name = name, ParentId = parentId };
        entity.Components.Add(ComponentRecord.Create(registry.Find("engine.transform")!, registry));
        Entities.Add(entity); return entity;
    }
    public IReadOnlyList<ComponentRecord> AddComponent(ComponentRegistry registry, string entityId, string typeId, JsonObject? initial = null)
    {
        var entity = Entities.Single(e => e.EntityId == entityId);
        var batch = registry.Addition(typeId, entity.Components.Select(c => c.TypeId))
            .Select(type => ComponentRecord.Create(type, registry, type.TypeId == typeId ? initial : null)).ToArray();
        entity.Components.AddRange(batch); return batch;
    }
    public void RemoveComponent(ComponentRegistry registry, string entityId, string componentId)
    {
        var entity = Entities.Single(e => e.EntityId == entityId);
        var target = entity.Components.Single(c => c.ComponentId == componentId);
        if (target.TypeId == "engine.transform") throw new InvalidOperationException("Transform cannot be removed");
        var remaining = entity.Components.Where(c => c != target).ToArray();
        foreach (var item in remaining)
            foreach (var dependency in registry.Find(item.TypeId)?.Requires ?? [])
                if (remaining.All(c => c.TypeId != registry.Get(dependency).TypeId))
                    throw new InvalidOperationException($"{item.TypeId} requires {registry.Get(dependency).TypeId}");
        entity.Components.Remove(target); ClearReferences(registry, new HashSet<string> { componentId });
    }
    public void DestroyEntity(ComponentRegistry registry, string entityId)
    {
        var removed = Subtree(entityId).ToArray();
        var ids = removed.Select(e => e.EntityId).Concat(removed.SelectMany(e => e.Components).Select(c => c.ComponentId)).ToHashSet();
        Entities.RemoveAll(e => ids.Contains(e.EntityId));
        ClearReferences(registry, ids);
        if (ActiveCameraId is not null && ids.Contains(ActiveCameraId)) ActiveCameraId = null;
    }
    public IEnumerable<EntityRecord> Subtree(string entityId)
    {
        var root = Entities.Single(e => e.EntityId == entityId);
        yield return root;
        foreach (var child in Entities.Where(e => e.ParentId == entityId))
            foreach (var descendant in Subtree(child.EntityId)) yield return descendant;
    }
    public void ClearReferences(ComponentRegistry registry, IReadOnlySet<string> removed)
    {
        foreach (var component in Entities.SelectMany(e => e.Components))
            if (registry.Find(component.TypeId) is { } type)
                foreach (var field in type.Fields)
                    component.State[field.Name] = ComponentValue.Remap(component.State[field.Name], field.ValueType, registry, id => removed.Contains(id) ? null : id);
    }
    public SceneData ExportPrefab(ComponentRegistry registry, string rootId)
    {
        var prefab = new SceneData { Name = Entities.Single(e => e.EntityId == rootId).Name, IsPrefab = true };
        prefab.Entities.AddRange(Subtree(rootId).Select(e => e.Clone()));
        prefab.Entities.Single(e => e.EntityId == rootId).ParentId = null;
        prefab.Validate(registry); // External scene references are errors, never silently cleared.
        return prefab;
    }
    public IReadOnlyList<EntityRecord> Instantiate(SceneData prefab, ComponentRegistry registry, string? parentId = null)
    {
        if (!prefab.IsPrefab) throw new InvalidDataException("Expected a Prefab asset");
        prefab.Validate(registry);
        var copy = prefab.Entities.Select(e => e.Clone()).ToArray();
        var ids = copy.Select(e => e.EntityId).Concat(copy.SelectMany(e => e.Components).Select(c => c.ComponentId))
            .ToDictionary(id => id, _ => Guid.NewGuid().ToString("N"));
        foreach (var entity in copy)
        {
            entity.EntityId = ids[entity.EntityId];
            entity.ParentId = entity.ParentId is null ? parentId : ids[entity.ParentId];
            foreach (var component in entity.Components)
            {
                component.ComponentId = ids[component.ComponentId];
                if (registry.Find(component.TypeId) is not { } type) throw new InvalidDataException("Cannot instantiate unknown Prefab components");
                foreach (var field in type.Fields) component.State[field.Name] = ComponentValue.Remap(component.State[field.Name], field.ValueType, registry,
                    id => ids.TryGetValue(id, out var replacement) ? replacement : throw new InvalidDataException("External Prefab reference"));
            }
        }
        Entities.AddRange(copy); return copy;
    }
    public void Validate(ComponentRegistry registry, bool cook = false, IReadOnlySet<string>? validateStateIds = null)
    {
        if (string.IsNullOrWhiteSpace(WorldId)) throw new InvalidDataException("World ID is required");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in Entities)
        {
            if (string.IsNullOrWhiteSpace(entity.EntityId) || !ids.Add(entity.EntityId)) throw new InvalidDataException("Duplicate or empty entity ID");
            foreach (var component in entity.Components)
                if (string.IsNullOrWhiteSpace(component.ComponentId) || !ids.Add(component.ComponentId)) throw new InvalidDataException("Duplicate or empty component ID");
        }
        bool Reference(string id, Type expected) => expected == typeof(SharpEntity) ? Entities.Any(e => e.EntityId == id) :
            Entities.SelectMany(e => e.Components).Any(c => c.ComponentId == id && registry.Find(c.TypeId) is { } t && expected.IsAssignableFrom(t.RuntimeType));
        foreach (var entity in Entities)
        {
            var ancestors = new HashSet<string> { entity.EntityId };
            for (var parentId = entity.ParentId; parentId is not null;)
            {
                if (!ancestors.Add(parentId)) throw new InvalidDataException("Parent cycle");
                parentId = (Entities.SingleOrDefault(e => e.EntityId == parentId) ?? throw new InvalidDataException("Missing parent")).ParentId;
            }
            if (entity.Components.Count(c => c.TypeId == "engine.transform") != 1) throw new InvalidDataException("An entity requires exactly one Transform");
            foreach (var component in entity.Components)
            {
                var descriptor = registry.Find(component.TypeId);
                if (descriptor is null) { if (cook) throw new InvalidDataException($"Unknown component {component.TypeId}"); continue; }
                if (component.SchemaVersion != descriptor.SchemaVersion) throw new InvalidDataException($"Unsupported component schema {component.TypeId}@{component.SchemaVersion}");
                if (component.TypeId == "engine.transform" && !component.Enabled) throw new InvalidDataException("Transform must be enabled");
                if (descriptor.Single && entity.Components.Count(c => c.TypeId == component.TypeId) > 1) throw new InvalidDataException($"Duplicate single component {component.TypeId}");
                foreach (var dependency in descriptor.Requires)
                    if (entity.Components.All(c => c.TypeId != registry.Get(dependency).TypeId)) throw new InvalidDataException($"Missing required component {dependency.Name}");
                if (validateStateIds is null || validateStateIds.Contains(component.ComponentId))
                    registry.ValidateState(descriptor, component.State, Reference);
                component.UnknownRecord = null;
            }
        }
        if (ActiveCameraId is not null && !Entities.Any(e => e.EntityId == ActiveCameraId && e.Components.Any(c => c.TypeId == "engine.camera"))) throw new InvalidDataException("Active camera must identify an entity with a Camera");
        if (IsPrefab && Entities.Count(e => e.ParentId is null) != 1) throw new InvalidDataException("Prefab requires one root entity");
    }
    public static SceneData Parse(string json, ComponentRegistry registry)
    {
        using var syntax = JsonDocument.Parse(json);
        CheckDuplicates(syntax.RootElement);
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Expected JSON object");
        Exact(root, "schemaVersion", "kind", "worldId", "name", "activeCameraId", "entities");
        if (root["schemaVersion"]!.GetValue<int>() != CurrentSchemaVersion) throw new InvalidDataException("WORLD_UNSUPPORTED_SCHEMA: only v3 is supported");
        var kind = root["kind"]!.GetValue<string>();
        if (kind is not ("world" or "prefab")) throw new InvalidDataException("Unknown document kind");
        var scene = new SceneData { WorldId = root["worldId"]!.GetValue<string>(), Name = root["name"]!.GetValue<string>(),
            ActiveCameraId = root["activeCameraId"]?.GetValue<string>(), IsPrefab = kind == "prefab" };
        foreach (var node in root["entities"]!.AsArray())
        {
            var item = node!.AsObject(); Exact(item, "entityId", "name", "parentId", "activeSelf", "components");
            var entity = new EntityRecord { EntityId = item["entityId"]!.GetValue<string>(), Name = item["name"]!.GetValue<string>(),
                ParentId = item["parentId"]?.GetValue<string>(), ActiveSelf = item["activeSelf"]!.GetValue<bool>() };
            foreach (var componentNode in item["components"]!.AsArray())
            {
                var c = componentNode!.AsObject();
                var type = c["typeId"]!.GetValue<string>();
                if (registry.Find(type) is not null) Exact(c, "componentId", "typeId", "schemaVersion", "enabled", "state");
                entity.Components.Add(new() { ComponentId = c["componentId"]!.GetValue<string>(), TypeId = type,
                    SchemaVersion = c["schemaVersion"]!.GetValue<int>(), Enabled = c["enabled"]!.GetValue<bool>(),
                    State = (JsonObject)c["state"]!.DeepClone(), UnknownRecord = registry.Find(type) is null ? (JsonObject)c.DeepClone() : null });
            }
            scene.Entities.Add(entity);
        }
        registry.MigrateScene(scene);
        scene.Validate(registry);
        return scene;
    }
    private static void Exact(JsonObject value, params string[] fields)
    {
        if (value.Count != fields.Length || fields.Any(f => !value.ContainsKey(f))) throw new InvalidDataException("Unexpected or missing document fields");
    }
    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>();
            foreach (var item in element.EnumerateObject())
            { if (!names.Add(item.Name)) throw new InvalidDataException($"Duplicate JSON field {item.Name}"); CheckDuplicates(item.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }
}
