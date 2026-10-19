namespace SharpEngine.Subsystems;

/// <summary>Host-owned Engine scope. Independent hosts never share subsystem instances.</summary>
public sealed class EngineLifetime : IDisposable
{
    private readonly List<RuntimeInstance> Instances = new();
    private readonly List<SubsystemCollection> Editors = new();
    private bool Disposed;
    public SubsystemRegistry Registry
    {
        get;
    } = new();
    public SubsystemCollection Subsystems
    {
        get;
    }

    public EngineLifetime()
    {
        Subsystems = new(Registry, SubsystemScope.Engine, this, null);
    }

    public SubsystemCollection CreateEditorScope(object Editor)
    {
        CheckAccess();
        var Collection = new SubsystemCollection(Registry, SubsystemScope.Editor, Editor, Subsystems);
        Editors.Add(Collection);
        return Collection;
    }

    internal void Attach(RuntimeInstance Instance)
    {
        CheckAccess();
        Instances.Add(Instance);
    }

    internal void Detach(RuntimeInstance Instance) { Instances.Remove(Instance); }

    public void Update(float DeltaSeconds)
    {
        CheckAccess();
        Subsystems.Update(DeltaSeconds);
        foreach (var Editor in Editors)
        {
            Editor.Update(DeltaSeconds);
            Editor.Commit();
        }
        Subsystems.Commit();
    }

    internal void CheckAccess()
    {
        Registry.CheckThread();
        ObjectDisposedException.ThrowIf(Disposed, this);
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }
        CheckAccess();
        Registry.Run(() =>
        {
        });
        foreach (var Instance in Instances)
        {
            Instance.CheckDisposal();
        }
        foreach (var Instance in Instances.ToArray().Reverse())
        {
            Instance.Dispose();
        }
        foreach (var Editor in Editors.AsEnumerable().Reverse())
        {
            Editor.Dispose();
        }
        Editors.Clear();
        Subsystems.Dispose();
        Registry.Close();
        Disposed = true;
    }
}
