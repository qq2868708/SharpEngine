using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.UI;

namespace SharpEngine.World;

// Display classification only: runtime/rendering query capabilities independently.
public enum WorldEntityKind { Mesh, Camera, Light, Empty }
public sealed record WorldEntityDescription(string Id, string Name, string Type, string Parent,
    string Asset, string Material, WorldVector3 Position, WorldVector3 Rotation, WorldVector3 Scale,
    WorldVector3 WorldPosition, float[] WorldMatrix, UiColor Color, float Intensity,
    float FieldOfView, float NearPlane, float FarPlane,
    bool ActiveSelf, bool ActiveInHierarchy, IReadOnlyList<JsonObject> Components);
public sealed record WorldSceneDescription(int SchemaVersion, string Name, string ActiveCamera,
    IReadOnlyList<WorldEntityDescription> Entities);
public readonly record struct WorldVector3(float X, float Y, float Z)
{
    public static WorldVector3 Zero => new(0, 0, 0);
    public static WorldVector3 One => new(1, 1, 1);
    public static WorldVector3 Parse(string? value, WorldVector3 fallback)
    {
        if (value is null) return fallback;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z) &&
            float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)) return new(x, y, z);
        throw new InvalidDataException("Expected three finite vector values");
    }
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X:R},{Y:R},{Z:R}");
}

/// <summary>Renderer/editor view over component records, without duplicate authoritative fields.</summary>
public sealed class WorldEntity
{
    internal static readonly string[] MeshComponentTypeIds =
        ["engine.mesh-renderer", "engine.static-mesh", "engine.skeletal-mesh"];
    internal EntityRecord Record;
    internal WorldDocument? Document;
    private ComponentRecord? _renderComponent;
    private static readonly ComponentRegistry Defaults = ComponentRegistry.CreateBuiltin();
    public WorldEntity()
    {
        Record = new();
        Record.Components.Add(ComponentRecord.Create(Defaults.Find("engine.transform")!, Defaults));
    }
    internal WorldEntity(EntityRecord record, WorldDocument document) { Record = record; Document = document; }
    public string Id { get => Record.EntityId; init => Record.EntityId = value; }
    public string Name { get => Record.Name; init => Record.Name = value; }
    public string ParentId { get => Record.ParentId ?? ""; internal set => Record.ParentId = value.Length == 0 ? null : value; }
    public bool ActiveSelf => Record.ActiveSelf;
    public bool ActiveInHierarchy => ActiveSelf && (Record.ParentId is null || Document?.Entities.First(e => e.Id == Record.ParentId).ActiveInHierarchy == true);
    /// <summary>Authoring records are detached copies; changes require a document transaction.</summary>
    public IReadOnlyList<ComponentRecord> Components
    {
        get
        {
            return Document?.IsAuthoring == true
                ? Array.AsReadOnly(Record.Components.Select(Component => Component.Clone()).ToArray())
                : Record.Components.AsReadOnly();
        }
    }
    public bool HasComponent(string typeId) => Record.Components.Any(c => c.TypeId == typeId);
    public bool HasEnabledComponent(string typeId) => ActiveInHierarchy && Record.Components.Any(c => c.TypeId == typeId && c.Enabled);
    internal IEnumerable<WorldEntity> RenderViews(string typeId) => Record.Components
        .Where(c => c.TypeId == typeId && c.Enabled && ActiveInHierarchy)
        .OrderBy(c => c.ComponentId, StringComparer.Ordinal)
        .Select(c => new WorldEntity(Record, Document!) { _renderComponent = c });
    internal static bool IsMeshComponentType(string typeId) =>
        Array.IndexOf(MeshComponentTypeIds, typeId) >= 0;
    private ComponentRecord? MeshComponent =>
        _renderComponent is { } render && IsMeshComponentType(render.TypeId)
            ? render
            : MeshComponentTypeIds.Select(Component).FirstOrDefault(c => c is not null);
    private ComponentRecord? Component(string typeId) => _renderComponent?.TypeId == typeId ? _renderComponent :
        Record.Components.Where(c => c.TypeId == typeId).OrderBy(c => c.ComponentId, StringComparer.Ordinal).FirstOrDefault();
    public WorldEntityKind Kind
    {
        get => MeshComponentTypeIds.Any(HasComponent) ? WorldEntityKind.Mesh :
            HasComponent("engine.camera") ? WorldEntityKind.Camera :
            HasComponent("engine.directional-light") || HasComponent("engine.sky-light") ?
                WorldEntityKind.Light : WorldEntityKind.Empty;
        init { if (value != WorldEntityKind.Empty) Ensure(value == WorldEntityKind.Mesh ? "engine.mesh-renderer" : value == WorldEntityKind.Camera ? "engine.camera" : "engine.directional-light"); }
    }
    private ComponentRecord Ensure(string TypeId)
    {
        Document?.CheckDirectMutation();
        var Component = Record.Components.FirstOrDefault(Component => Component.TypeId == TypeId);
        if (Component is not null)
        {
            return Component;
        }
        Component = ComponentRecord.Create(Defaults.Find(TypeId)!, Defaults);
        Record.Components.Add(Component);
        return Component;
    }
    private JsonObject State(string typeId) => Component(typeId)!.State;
    internal Vector3 LinearColor(string TypeId)
    {
        var Values = State(TypeId)["Color"]!.AsArray();
        return new(Values[0]!.Deserialize<float>(),
            Values[1]!.Deserialize<float>(), Values[2]!.Deserialize<float>());
    }
    private WorldVector3 Vector(string name)
    {
        var a = State("engine.transform")[name]!.AsArray();
        return new(a[0]!.Deserialize<float>(), a[1]!.Deserialize<float>(), a[2]!.Deserialize<float>());
    }
    private void Vector(string Name, WorldVector3 Value)
    {
        Document?.CheckDirectMutation();
        if (!float.IsFinite(Value.X) || !float.IsFinite(Value.Y) || !float.IsFinite(Value.Z) ||
            Name == "Scale" && (Value.X <= 0 || Value.Y <= 0 || Value.Z <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(Value));
        }
        State("engine.transform")[Name] = new JsonArray(Value.X, Value.Y, Value.Z);
    }
    public WorldVector3 Position { get => Vector("Position"); set => Vector("Position", value); }
    public WorldVector3 Rotation { get => Vector("Rotation"); set => Vector("Rotation", value); }
    public WorldVector3 Scale { get => Vector("Scale"); set => Vector("Scale", value); }
    private ComponentRecord EnsureMeshComponent() => MeshComponent ?? Ensure("engine.mesh-renderer");
    public string Asset { get => MeshComponent?.State["Mesh"]?.GetValue<string>() ?? ""; init => EnsureMeshComponent().State["Mesh"] = string.IsNullOrEmpty(value) ? null : value; }
    public string Material { get => MeshComponent?.State["Material"]?.GetValue<string>() ?? ""; init => EnsureMeshComponent().State["Material"] = string.IsNullOrEmpty(value) ? null : value; }
    private string? LightType
    {
        get
        {
            return _renderComponent is
                { TypeId: "engine.directional-light" or "engine.sky-light" } ?
                    _renderComponent.TypeId : HasComponent("engine.directional-light") ?
                        "engine.directional-light" : HasComponent("engine.sky-light") ?
                            "engine.sky-light" : null;
        }
    }
    private string ColorType
    {
        get
        {
            return _renderComponent is
                { TypeId: "engine.mesh-renderer" or "engine.static-mesh" or
                    "engine.skeletal-mesh" or "engine.directional-light" or
                    "engine.sky-light" } ? _renderComponent.TypeId :
                LightType ?? MeshComponent?.TypeId ?? "engine.mesh-renderer";
        }
    }
    public UiColor Color
    {
        get
        {
            if (!HasComponent(ColorType)) return new(255, 255, 255, 255);
            var a = State(ColorType)["Color"]!.AsArray();
            return new((byte)Math.Clamp(a[0]!.Deserialize<float>() * 255, 0, 255), (byte)Math.Clamp(a[1]!.Deserialize<float>() * 255, 0, 255),
                (byte)Math.Clamp(a[2]!.Deserialize<float>() * 255, 0, 255), (byte)Math.Clamp(a[3]!.Deserialize<float>() * 255, 0, 255));
        }
        set => Ensure(ColorType).State["Color"] = new JsonArray(value.R / 255f, value.G / 255f, value.B / 255f, value.A / 255f);
    }
    public float Intensity
    {
        get
        {
            var TypeId = LightType;
            return TypeId is null ? 1 : State(TypeId)["Intensity"]!.Deserialize<float>();
        }
        set
        {
            if (!float.IsFinite(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            Ensure("engine.directional-light").State["Intensity"] = value;
        }
    }
    public float FieldOfView { get => HasComponent("engine.camera") ? State("engine.camera")["FieldOfView"]!.Deserialize<float>() : 60; init => Ensure("engine.camera").State["FieldOfView"] = value; }
    public float NearPlane { get => HasComponent("engine.camera") ? State("engine.camera")["NearPlane"]!.Deserialize<float>() : .1f; init => Ensure("engine.camera").State["NearPlane"] = value; }
    public float FarPlane { get => HasComponent("engine.camera") ? State("engine.camera")["FarPlane"]!.Deserialize<float>() : 1000; init => Ensure("engine.camera").State["FarPlane"] = value; }
}

public sealed class WorldDocument
{
    public RuntimeFrame? RuntimeFrame
    {
        get;
        private set;
    }
    internal IReadOnlyDictionary<string, ComponentFrameState> RenderStates = new Dictionary<string, ComponentFrameState>();
    public WorldDocument(RuntimeFrame Frame, ComponentRegistry Registry, string SourcePath = "<runtime>")
        : this(Frame.Snapshot(), Registry, SourcePath)
    {
        ApplyRuntimeFrame(Frame);
    }
    public void ApplyRuntimeFrame(RuntimeFrame Frame)
    {
        CheckDirectMutation();
        Replace(Frame.Snapshot());
        RuntimeFrame = Frame;
        RenderStates = Frame.Components;
    }
    public const int CurrentSchemaVersion = 3;
    private readonly List<WorldEntity> _entities = [];
    public ComponentRegistry Registry { get; internal set; }
    private SceneData Scene = null!;
    internal bool IsAuthoring
    {
        get;
        private set;
    }
    /// <summary>Returns detached data for documents owned by an Authoring session.</summary>
    public SceneData Data
    {
        get
        {
            return IsAuthoring ? Scene.Clone() : Scene;
        }
    }
    public string SourcePath { get; private set; }
    public string Name
    {
        get
        {
            return Scene.Name;
        }
    }
    public string ActiveCameraId
    {
        get
        {
            return Scene.ActiveCameraId ?? "";
        }
    }
    public WorldEntity? ActiveCamera => Entities.FirstOrDefault(e => e.HasEnabledComponent("engine.camera") && (ActiveCameraId.Length == 0 || e.Id == ActiveCameraId));
    public IReadOnlyList<WorldEntity> Entities => _entities.AsReadOnly();
    public int MeshCount => Entities.Count(e => WorldEntity.MeshComponentTypeIds.Any(e.HasComponent));
    public WorldDocument(SceneData data, ComponentRegistry registry, string sourcePath = "<memory>")
    {
        Registry = registry;
        SourcePath = sourcePath;
        Replace(data);
    }
    internal void BindAuthoring()
    {
        if (IsAuthoring)
        {
            throw new InvalidOperationException("AUTHORING_ALREADY_OWNED: A document has one Authoring session.");
        }
        IsAuthoring = true;
        // Detach references handed out before this document acquired an owner.
        Replace(Scene);
    }
    internal void CheckDirectMutation()
    {
        if (IsAuthoring)
        {
            throw new InvalidOperationException("AUTHORING_TRANSACTION_REQUIRED: Edit this document through its Authoring session.");
        }
    }
    internal void SetSourcePath(string path) => SourcePath = Path.GetFullPath(path);
    internal void Replace(SceneData Data)
    {
        RuntimeFrame = null;
        RenderStates = new Dictionary<string, ComponentFrameState>();
        Scene = IsAuthoring ? Data.Clone() : Data;
        var Existing = _entities.ToDictionary(Entity => Entity.Id, StringComparer.Ordinal);
        _entities.Clear();
        foreach (var Record in Scene.Entities)
        {
            var Found = Existing.TryGetValue(Record.EntityId, out var Entity);
            if (!Found)
            {
                Entity = new(Record, this);
            }
            Entity!.Record = Record;
            _entities.Add(Entity);
        }
    }
    public Matrix4x4 WorldMatrix(WorldEntity entity)
    {
        var result = Matrix4x4.Identity;
        var visiting = new HashSet<string>();
        for (WorldEntity? current = entity; current is not null; current = current.ParentId.Length == 0 ? null : Entities.First(e => e.Id == current.ParentId))
        {
            if (!visiting.Add(current.Id)) throw new InvalidDataException("Parent cycle");
            result *= Matrix4x4.CreateScale(current.Scale.X, current.Scale.Y, current.Scale.Z) *
                Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromYawPitchRoll(current.Rotation.Y * MathF.PI / 180, current.Rotation.X * MathF.PI / 180, current.Rotation.Z * MathF.PI / 180)) *
                Matrix4x4.CreateTranslation(current.Position.X, current.Position.Y, current.Position.Z);
        }
        return result;
    }
    public static WorldDocument Load(string path, ComponentRegistry? registry = null) => Parse(File.ReadAllText(path), path, registry);
    public static WorldDocument Parse(string json, string sourceName = "<memory>", ComponentRegistry? registry = null)
    {
        registry ??= ComponentRegistry.CreateBuiltin();
        try { return new(SceneData.Parse(json, registry), registry, sourceName); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or NullReferenceException)
        { throw new InvalidDataException("Invalid World v3 JSON: " + error.Message, error); }
    }
    public string Serialize() => Scene.Serialize();
    public void Save(string path)
    {
        Scene.Validate(Registry);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, Serialize()); _ = Load(temporary, Registry); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public WorldSceneDescription Describe() => new(CurrentSchemaVersion, Name, ActiveCameraId, Entities.Select(e =>
    {
        var m = WorldMatrix(e);
        return new WorldEntityDescription(e.Id, e.Name, e.Kind.ToString(), e.ParentId, e.Asset, e.Material, e.Position, e.Rotation, e.Scale,
            new(m.M41, m.M42, m.M43), [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44],
            e.Color,e.Intensity,e.FieldOfView,e.NearPlane,e.FarPlane,e.ActiveSelf,e.ActiveInHierarchy,
            e.Record.Components.Where(c => Registry.Find(c.TypeId) is not null).Select(c => c.ToJson()).ToArray());
    }).ToArray());
    public string ToJson(JsonSerializerOptions? options = null) => JsonSerializer.Serialize(Describe(), options ?? new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
