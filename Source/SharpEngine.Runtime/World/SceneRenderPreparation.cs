using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;

namespace SharpEngine.World;

/// <summary>Explicit authoring preview stage. Runtime scenes already carry committed component outputs.</summary>
public static class SceneRenderPreparation
{
    public static void Prepare(WorldDocument World, AssetRegistry? Assets)
    {
        if (World.RuntimeFrame is not null)
        {
            return;
        }
        var States = new Dictionary<string, ComponentFrameState>(StringComparer.Ordinal);
        if (Assets is null)
        {
            World.RenderStates = States;
            return;
        }
        var Animators = World.Entities.Where(Entity => Entity.ActiveInHierarchy).SelectMany(Entity => Entity.Components)
            .Where(Component => Component.TypeId == "engine.animator" && Component.Enabled).ToArray();
        foreach (var Entity in World.Entities.Where(Entity => Entity.ActiveInHierarchy))
        {
            foreach (var Mesh in Entity.Components.Where(Component => Component.TypeId == "engine.skeletal-mesh" && Component.Enabled))
            {
                var MeshId = Mesh.State["Mesh"]?.GetValue<string>();
                if (MeshId is null)
                {
                    continue;
                }
                try
                {
                    var Sources = Animators.Where(Animator => Animator.State["Target"]?.GetValue<string>() == Mesh.ComponentId).ToArray();
                    var Code = Sources.Length > 1 ? "SKELETON_POSE_CONFLICT" : "SKELETON_READY";
                    SkeletalPose? Pose = null;
                    if (Sources.Length == 1)
                    {
                        var Animator = Sources[0];
                        Pose = SkeletalMeshComponent.SampleLegacy(Assets, MeshId, Animator.State["Clip"]?.GetValue<string>(),
                            Animator.State["Time"]!.GetValue<double>(), Animator.State["Loop"]!.GetValue<bool>());
                    }
                    else if (Sources.Length == 0)
                    {
                        Pose = SkeletalMeshComponent.SampleLegacy(Assets, MeshId, Mesh.State["Animation"]?.GetValue<string>(),
                            Mesh.State["AnimationTime"]!.GetValue<float>(), Mesh.State["LoopAnimation"]!.GetValue<bool>());
                    }
                    var Skeleton = new SkeletonInstance(AnimatorContent.Mesh(Assets, MeshId));
                    States[Mesh.ComponentId] = Skeleton.Prepare(Entity.Id, Mesh.ComponentId, Pose, Code,
                        Sources.Length > 1 ? "Multiple pose sources target this skeleton; explicit blending is required." : null);
                }
                catch (Exception Error) when (Error is IOException or InvalidDataException or KeyNotFoundException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
                {
                    States[Mesh.ComponentId] = new SkeletonFrameState(Entity.Id, Mesh.ComponentId, MeshId, "SKELETON_PREPARATION_FAILED", Error.Message);
                }
            }
        }
        World.RenderStates = States;
    }
}
