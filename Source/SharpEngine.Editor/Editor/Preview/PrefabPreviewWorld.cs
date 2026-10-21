using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.World;

namespace SharpEngine.Editor;

/// <summary>Copies authoring records into a transient scene; navigation never touches the Prefab document.</summary>
internal sealed class PrefabPreviewWorld
{
    private readonly AssetPreviewWorld Preview;
    private readonly SceneData Environment;
    private readonly AssetRegistry Assets;
    private readonly HashSet<string> ModelIds = new(StringComparer.Ordinal);

    internal PrefabPreviewWorld(WorldDocument Source, AssetRegistry Assets, string Id)
    {
        this.Assets = Assets;
        Preview = new AssetPreviewWorld(Source.Registry, Id);
        Environment = Preview.World.Data.Clone();
        foreach (var Entity in Environment.Entities)
        {
            Entity.EntityId = Guid.NewGuid().ToString("N");
        }
        Synchronize(Source);
    }

    internal WorldDocument World
    {
        get
        {
            return Preview.World;
        }
    }

    internal void Synchronize(WorldDocument Source)
    {
        var Data = Environment.Clone();
        Data.Entities.AddRange(Source.Data.Entities.Select(Entity => Entity.Clone()));
        Data.Validate(Source.Registry);
        World.Replace(Data);
        ModelIds.Clear();
        ModelIds.UnionWith(Source.Entities.Select(Entity => Entity.Id));
    }

    internal IEnumerable<Vector3> FramePositions()
    {
        foreach (var Entity in World.Entities.Where(Entity => ModelIds.Contains(Entity.Id)))
        {
            var Matrix = World.WorldMatrix(Entity);
            yield return Matrix.Translation;
            foreach (var Component in Entity.Components.Where(Component => Component.Enabled))
            {
                if (Component.TypeId is not ("engine.mesh-renderer" or "engine.static-mesh" or "engine.skeletal-mesh"))
                {
                    continue;
                }
                var MeshId = Component.State["Mesh"]?.GetValue<string>();
                if (string.IsNullOrEmpty(MeshId))
                {
                    continue;
                }
                var Positions = Component.TypeId switch
                {
                    "engine.static-mesh" => AssetContent.LoadStaticMesh(Assets, MeshId).Positions
                        .Select(Position => new Vector3(Position.X, Position.Y, Position.Z)),
                    "engine.skeletal-mesh" => AssetContent.LoadSkeletalMesh(Assets, MeshId).Positions
                        .Select(Position => new Vector3(Position.X, Position.Y, Position.Z)),
                    _ => AssetContent.LoadMesh(Assets, MeshId).Positions
                        .Select(Position => new Vector3(Position.X, Position.Y, Position.Z)),
                };
                foreach (var Position in Positions)
                {
                    yield return Vector3.Transform(Position, Matrix);
                }
            }
        }
    }

    internal JsonObject Describe() { return Preview.Describe(); }
}
