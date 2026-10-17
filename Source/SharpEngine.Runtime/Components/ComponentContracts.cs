using System.Numerics;
using System.Text.Json.Nodes;

namespace SharpEngine.Components;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ComponentTypeIdAttribute(string id) : Attribute { public string Id { get; } = id; }
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ComponentSchemaVersionAttribute(int version) : Attribute { public int Version { get; } = version; }
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class DisallowMultipleComponentAttribute : Attribute;
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequireComponentAttribute(Type type) : Attribute { public Type Type { get; } = type; }
[AttributeUsage(AttributeTargets.Class)]
public sealed class AddComponentMenuAttribute(string path) : Attribute { public string Path { get; } = path; }
[AttributeUsage(AttributeTargets.Field)] public sealed class SerializeFieldAttribute : Attribute;
[AttributeUsage(AttributeTargets.Field)] public sealed class HideInInspectorAttribute : Attribute;
[AttributeUsage(AttributeTargets.Field)] public sealed class HeaderAttribute(string text) : Attribute { public string Text { get; } = text; }
[AttributeUsage(AttributeTargets.Field)] public sealed class TooltipAttribute(string text) : Attribute { public string Text { get; } = text; }
[AttributeUsage(AttributeTargets.Field)] public sealed class InspectorNameAttribute(string text) : Attribute { public string Text { get; } = text; }
[AttributeUsage(AttributeTargets.Field)] public sealed class RangeAttribute(double min, double max) : Attribute { public double Min { get; } = min; public double Max { get; } = max; }
[AttributeUsage(AttributeTargets.Field)] public sealed class MultilineAttribute(int lines = 3) : Attribute { public int Lines { get; } = lines; }
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ComponentCatalogAttribute(Type provider) : Attribute { public Type Provider { get; } = provider; }
public interface IComponentCatalog { void Register(ComponentRegistry registry, string moduleId, string generationId); }

public readonly record struct Color(float R, float G, float B, float A = 1)
{
    public static Color White => new(1, 1, 1, 1);
    public static Color Red => new(1, 0, 0, 1);
}
public interface IAssetReference { string? Id { get; } }
public readonly record struct AssetRef<T>(string? Id) : IAssetReference;
public interface IComponentInitialState
{
    Type ComponentType { get; }
    JsonObject ToState(ComponentRegistry registry);
}
public sealed class MeshAsset;
public sealed class MaterialAsset;
public sealed class PrefabAsset;

public sealed record ComponentDiagnostic(string Code, string ObjectId, string FieldPath,
    string Message, string Severity = "error", string? Expected = null,
    string? Actual = null, ulong Revision = 0, bool Retryable = false);
public sealed class ComponentDiagnosticSink
{
    private readonly List<ComponentDiagnostic> _items = [];
    public IReadOnlyList<ComponentDiagnostic> Items => _items;
    public void Error(string code, string fieldPath, string message) =>
        _items.Add(new(code, "", fieldPath, message));
}
public sealed class ComponentValidationException(string code, string path, string message)
    : Exception($"{code}: {path}: {message}")
{
    public string Code { get; } = code;
    public string FieldPath { get; } = path;
}

public abstract class SharpComponent
{
    private bool _enabled = true;
    internal SharpEntity? Owner;
    internal ComponentDescriptor Descriptor = null!;
    internal bool Initialized, Started, EffectiveEnabled;
    public string ComponentId { get; internal set; } = "";
    public string TypeId => Descriptor.TypeId;
    public bool IsAlive { get; internal set; }
    public SharpEntity Entity => IsAlive ? Owner! : throw new InvalidOperationException("Component is not alive");
    public RuntimeWorld World => Entity.World;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!IsAlive) throw new InvalidOperationException("Component is not alive");
            World.CheckThread();
            if (this is TransformComponent && !value) throw new InvalidOperationException("Transform cannot be disabled");
            _enabled = value;
            World.RequestActivation();
        }
    }
    internal void SetInitialEnabled(bool value) => _enabled = value;
    protected virtual void OnInitialize() { }
    protected virtual void OnEnable() { }
    protected virtual void OnStart() { }
    protected virtual void OnFixedUpdate(float deltaSeconds) { }
    protected virtual void OnUpdate(float deltaSeconds) { }
    protected virtual void OnLateUpdate(float deltaSeconds) { }
    protected virtual void OnDisable() { }
    protected virtual void OnUninitialize() { }
    internal void Invoke(string stage, float delta = 0)
    {
        switch (stage)
        {
            case "Initialize": OnInitialize(); break;
            case "Enable": OnEnable(); break;
            case "Start": OnStart(); break;
            case "FixedUpdate": OnFixedUpdate(delta); break;
            case "Update": OnUpdate(delta); break;
            case "LateUpdate": OnLateUpdate(delta); break;
            case "Disable": OnDisable(); break;
            case "Uninitialize": OnUninitialize(); break;
        }
    }
}

public sealed class RuntimeMutation<T>
{
    public bool IsCompleted { get; internal set; }
    public Exception? Error { get; internal set; }
    private T? _value;
    public T Value => !IsCompleted ? throw new InvalidOperationException("Mutation is pending until the safe boundary")
        : Error is not null ? throw new InvalidOperationException("Mutation failed", Error) : _value!;
    internal void Complete(T value) { _value = value; IsCompleted = true; }
    internal void Fail(Exception error) { Error = error; IsCompleted = true; }
}

public sealed class SharpEntity
{
    internal readonly List<SharpComponent> MutableComponents = [];
    private bool _active = true;
    private readonly RuntimeWorld _world;
    internal SharpEntity(RuntimeWorld world, string id, string name) { _world = world; EntityId = id; Name = name; }
    public RuntimeWorld World { get { EnsureAlive(); return _world; } }
    public string EntityId { get; }
    public string Name { get; internal set; }
    public bool IsAlive { get; internal set; } = true;
    public SharpEntity? Parent { get; internal set; }
    public bool ActiveSelf
    {
        get => _active;
        set { EnsureAlive(); World.CheckThread(); _active = value; World.RequestActivation(); }
    }
    public bool ActiveInHierarchy => IsAlive && _active && (Parent?.ActiveInHierarchy ?? true);
    public IReadOnlyList<SharpComponent> Components => MutableComponents.AsReadOnly();
    public T? GetComponent<T>() where T : SharpComponent => GetComponents<T>().SingleOrDefault();
    public IReadOnlyList<T> GetComponents<T>() where T : SharpComponent
    { EnsureAlive(); return MutableComponents.OfType<T>().ToArray(); }
    public RuntimeMutation<T> AddComponent<T>(JsonObject? initialState = null) where T : SharpComponent =>
        World.AddComponent<T>(EntityId, initialState);
    public RuntimeMutation<T> AddComponent<T>(IComponentInitialState initialState) where T : SharpComponent =>
        World.AddComponent<T>(EntityId, initialState);
    public RuntimeMutation<bool> RemoveComponent(SharpComponent component) => World.RemoveComponent(EntityId, component.ComponentId);
    internal void EnsureAlive() { if (!IsAlive) throw new InvalidOperationException("Entity has been removed"); }
}

[ComponentTypeId("engine.transform"), ComponentSchemaVersion(1), DisallowMultipleComponent]
public sealed partial class TransformComponent : SharpComponent
{
    public Vector3 Position = Vector3.Zero;
    public Vector3 Rotation = Vector3.Zero;
    public Vector3 Scale = Vector3.One;
}
[ComponentTypeId("engine.mesh-renderer"), ComponentSchemaVersion(1), AddComponentMenu("Rendering/Mesh Renderer")]
public sealed partial class MeshRendererComponent : SharpComponent
{
    public AssetRef<MeshAsset> Mesh;
    public AssetRef<MaterialAsset> Material;
    public Color Color = Color.White;
}
[ComponentTypeId("engine.camera"), ComponentSchemaVersion(1), AddComponentMenu("Rendering/Camera")]
public sealed partial class CameraComponent : SharpComponent
{
    [Range(1, 179)] public float FieldOfView = 60;
    public float NearPlane = 0.1f;
    public float FarPlane = 1000;
}
[ComponentTypeId("engine.directional-light"), ComponentSchemaVersion(1), AddComponentMenu("Rendering/Directional Light")]
public sealed partial class DirectionalLightComponent : SharpComponent
{
    public Color Color = Color.White;
    public float Intensity = 1;
}
