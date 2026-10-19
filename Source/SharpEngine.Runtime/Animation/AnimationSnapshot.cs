using System.Text.Json.Nodes;
using SharpEngine.Components;

namespace SharpEngine.Animation;

public sealed class AnimatorFrameState : ComponentFrameState
{
    private readonly JsonObject State;
    internal AnimatorFrameState(JsonObject State) { this.State = (JsonObject)State.DeepClone(); }
    public override JsonObject ToJson() { return (JsonObject)State.DeepClone(); }
}

/// <summary>Animation views of an already committed frame; observing never evaluates animation.</summary>
public sealed class AnimationSnapshot
{
    private readonly RuntimeFrame Frame;
    public AnimationSnapshot(RuntimeFrame Frame) { this.Frame = Frame; }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["revision"] = Frame.Revision,
            ["animators"] = new JsonArray(Frame.Components.OrderBy(Entry => Entry.Key, StringComparer.Ordinal).Select(Entry => Entry.Value).OfType<AnimatorFrameState>().Select(State => (JsonNode)State.ToJson()).ToArray()),
            ["skeletons"] = new JsonArray(Frame.Components.OrderBy(Entry => Entry.Key, StringComparer.Ordinal).Select(Entry => Entry.Value).OfType<SkeletonFrameState>().Select(State => (JsonNode)State.ToJson()).ToArray()),
        };
    }
}
