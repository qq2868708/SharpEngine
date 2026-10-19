using SharpEngine.Assets;
using SharpEngine.Components;

namespace SharpEngine.Subsystems;

/// <summary>One game run / Editor Play session. Survives worlds created and destroyed within that run.</summary>
public sealed class RuntimeInstance : IDisposable
{
    private readonly List<RuntimeWorld> ActiveWorlds = new();
    private readonly bool OwnsEngine;
    private bool Disposed;
    private bool Disposing;
    private bool Updating;
    public string Id
    {
        get;
    } = Guid.NewGuid().ToString("N");
    public EngineLifetime Engine
    {
        get;
    }
    public AssetRegistry? Assets
    {
        get;
    }
    public SubsystemCollection Subsystems
    {
        get;
    }
    public IReadOnlyList<RuntimeWorld> Worlds
    {
        get
        {
            Engine.CheckAccess();
            ObjectDisposedException.ThrowIf(Disposed, this);
            return ActiveWorlds.AsReadOnly();
        }
    }

    public RuntimeInstance(AssetRegistry? Assets = null, EngineLifetime? Engine = null)
    {
        OwnsEngine = Engine is null;
        this.Engine = Engine ?? new();
        this.Assets = Assets;
        try
        {
            Subsystems = new(this.Engine.Registry, SubsystemScope.Instance, this, this.Engine.Subsystems);
            this.Engine.Attach(this);
        }
        catch
        {
            if (OwnsEngine)
            {
                this.Engine.Dispose();
            }
            throw;
        }
    }

    public RuntimeWorld CreateWorld(SceneData Scene, ComponentRegistry Registry)
    {
        CheckAccess();
        return new(Scene, Registry, Assets, this);
    }

    internal void Attach(RuntimeWorld World)
    {
        CheckAccess();
        Engine.Registry.Run(() =>
        {
        });
        ActiveWorlds.Add(World);
    }

    internal void Detach(RuntimeWorld World)
    {
        ActiveWorlds.Remove(World);
        if (!Disposing && !Disposed)
        {
            Commit();
        }
    }

    internal void Commit() { Subsystems.Commit(); }

    public void Tick(float DeltaSeconds)
    {
        CheckAccess();
        if (Updating)
        {
            throw new InvalidOperationException("Runtime Instance ticks cannot be nested.");
        }
        Updating = true;
        try
        {
            Subsystems.Update(DeltaSeconds);
            foreach (var World in ActiveWorlds.ToArray())
            {
                if (ActiveWorlds.Contains(World))
                {
                    World.Tick(DeltaSeconds);
                }
            }
            Subsystems.Commit();
        }
        finally
        {
            Updating = false;
        }
    }

    public void Step() { Tick(RuntimeWorld.FixedDeltaSeconds); }

    private void CheckAccess()
    {
        Engine.CheckAccess();
        ObjectDisposedException.ThrowIf(Disposed || Disposing, this);
    }

    public void Dispose()
    {
        if (Disposed || Disposing)
        {
            return;
        }
        CheckDisposal();
        Disposing = true;
        foreach (var World in ActiveWorlds.ToArray().Reverse())
        {
            World.Dispose();
        }
        Subsystems.Dispose();
        Engine.Detach(this);
        Disposed = true;
        Disposing = false;
        if (OwnsEngine)
        {
            Engine.Dispose();
        }
    }

    internal void CheckDisposal()
    {
        CheckAccess();
        if (Updating)
        {
            throw new InvalidOperationException("Runtime Instance disposal requires a completed tick.");
        }
        Engine.Registry.Run(() =>
        {
        });
        foreach (var World in ActiveWorlds)
        {
            World.CheckDisposal();
        }
    }
}
