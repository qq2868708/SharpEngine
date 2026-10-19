using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace SharpEngine.Components;

/// <summary>Immutable derived component data published with its authoritative scene.</summary>
public abstract class ComponentFrameState
{
    public abstract JsonObject ToJson();
}

public sealed class RuntimeFrame
{
    private readonly SceneData Scene;
    public ulong Revision
    {
        get;
    }
    public IReadOnlyDictionary<string, ComponentFrameState> Components
    {
        get;
    }

    internal RuntimeFrame(SceneData Scene, ulong Revision, IDictionary<string, ComponentFrameState> Components)
    {
        this.Scene = Scene.Clone();
        this.Revision = Revision;
        this.Components = new ReadOnlyDictionary<string, ComponentFrameState>(new Dictionary<string, ComponentFrameState>(Components, StringComparer.Ordinal));
    }

    public SceneData Snapshot() { return Scene.Clone(); }
    public T? Get<T>(string ComponentId) where T : ComponentFrameState { return Components.GetValueOrDefault(ComponentId) as T; }

    public JsonObject ToJson()
    {
        var States = new JsonObject();
        foreach (var Entry in Components.OrderBy(Entry => Entry.Key, StringComparer.Ordinal))
        {
            States[Entry.Key] = Entry.Value.ToJson();
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["revision"] = Revision,
            ["components"] = States,
        };
    }
}
