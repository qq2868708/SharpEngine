using System.Text.Json.Nodes;
using SharpEngine.Components;

namespace SharpEngine.Subsystems;

public enum SubsystemScope
{
    Engine,
    Editor,
    Instance,
    World,
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SubsystemAttribute(string Id, int SchemaVersion = 1) : Attribute
{
    public string Id
    {
        get;
    } = Id;
    public int SchemaVersion
    {
        get;
    } = SchemaVersion;
}

[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class SubsystemScopeAttribute(SubsystemScope Scope) : Attribute
{
    public SubsystemScope Scope
    {
        get;
    } = Scope;
}

/// <summary>One automatically owned instance per concrete type in a fixed engine scope.</summary>
public abstract class Subsystem
{
    internal SubsystemCollection? Collection;
    public bool IsInitialized
    {
        get;
        internal set;
    }
    protected SubsystemCollection Subsystems
    {
        get
        {
            return Collection ?? throw new ObjectDisposedException(GetType().Name);
        }
    }
    protected virtual void OnInitialize()
    {
    }
    protected virtual void OnUpdate(float DeltaSeconds)
    {
    }
    protected virtual void OnUninitialize()
    {
    }
    /// <summary>Versioned, runtime-only semantic state. Never return native handles or delegates.</summary>
    protected virtual JsonObject CaptureState() { return new(); }
    internal void Initialize() { OnInitialize(); }
    internal void Update(float DeltaSeconds) { OnUpdate(DeltaSeconds); }
    internal void Uninitialize() { OnUninitialize(); }
    internal JsonObject Capture() { return CaptureState(); }
}

[SubsystemScope(SubsystemScope.Engine)]
public abstract class EngineSubsystem : Subsystem
{
    protected EngineLifetime Engine
    {
        get
        {
            return (EngineLifetime)Subsystems.Owner;
        }
    }
}

[SubsystemScope(SubsystemScope.Instance)]
public abstract class InstanceSubsystem : Subsystem
{
    protected RuntimeInstance Instance
    {
        get
        {
            return (RuntimeInstance)Subsystems.Owner;
        }
    }
}

[SubsystemScope(SubsystemScope.World)]
public abstract class WorldSubsystem : Subsystem
{
    protected RuntimeWorld World
    {
        get
        {
            return (RuntimeWorld)Subsystems.Owner;
        }
    }
}

public sealed class SubsystemException(string Code, string TypeId, string Message, Exception? Inner = null)
    : InvalidOperationException($"{Code}: {TypeId}: {Message}", Inner)
{
    public string Code
    {
        get;
    } = Code;
    public string TypeId
    {
        get;
    } = TypeId;
}

public interface ISubsystemRuntime
{
    EngineLifetime SubsystemLifetime
    {
        get;
    }
    RuntimeInstance? Instance
    {
        get;
    }
}
