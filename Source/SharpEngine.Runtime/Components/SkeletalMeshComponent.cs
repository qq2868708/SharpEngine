using SharpEngine.Animation;
using SharpEngine.Assets;

namespace SharpEngine.Components;

[ComponentTypeId("engine.skeletal-mesh"), ComponentSchemaVersion(1),
    AddComponentMenu("Rendering/Skeletal Mesh"), RequireComponent(typeof(TransformComponent)),
    DisallowMultipleComponent]
public sealed partial class SkeletalMeshComponent : SharpComponent
{
    public AssetRef<SkeletalMeshAsset> Mesh;
    public AssetRef<MaterialAsset> Material;
    public AssetRef<SkeletalAnimationAsset> Animation;
    public float AnimationTime;
    public bool LoopAnimation = true;
    public Color Color = Color.White;

    private readonly Dictionary<string, (SharpComponent Source, SkeletalPose? Pose)> Inputs = new(StringComparer.Ordinal);
    private SkeletonInstance? Skeleton;
    private SkeletalMeshAsset? BoundMesh;
    private SkeletonFrameState? Prepared;

    internal void SetPose(SharpComponent Source, SkeletalPose? Pose)
    {
        World.CheckThread();
        if (Source.World != World)
        {
            throw new InvalidOperationException("Pose source and target must belong to the same runtime world.");
        }
        Inputs[Source.ComponentId] = (Source, Pose);
    }

    internal void ClearPose(SharpComponent Source) { Inputs.Remove(Source.ComponentId); }

    protected override void OnPrepareRender()
    {
        var Assets = World.Assets;
        if (Assets is null || Mesh.Id is null)
        {
            Prepared = new(Entity.EntityId, ComponentId, Mesh.Id, "SKELETON_UNAVAILABLE", null);
            return;
        }
        try
        {
            var Geometry = AnimatorContent.Mesh(Assets, Mesh.Id);
            if (BoundMesh != Geometry)
            {
                BoundMesh = Geometry;
                Skeleton = new(Geometry);
            }
            var Active = Inputs.Values.Where(Input => Input.Source.IsAlive && Input.Source.Enabled && Input.Source.Entity.ActiveInHierarchy).ToArray();
            var Code = Active.Length > 1 ? "SKELETON_POSE_CONFLICT" : "SKELETON_READY";
            var Pose = Active.Length == 1 ? Active[0].Pose : Active.Length == 0 ? SampleLegacy(Assets, Mesh.Id, Animation.Id, AnimationTime, LoopAnimation) : null;
            Prepared = Skeleton!.Prepare(Entity.EntityId, ComponentId, Pose, Code,
                Active.Length > 1 ? "Multiple pose sources target this skeleton; explicit blending is required." : null);
        }
        catch (Exception Error) when (Error is IOException or InvalidDataException or KeyNotFoundException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Prepared = new(Entity.EntityId, ComponentId, Mesh.Id, "SKELETON_PREPARATION_FAILED", Error.Message);
        }
    }

    internal static SkeletalPose? SampleLegacy(AssetRegistry Assets, string MeshId, string? ClipId, double Time, bool Loop)
    {
        if (ClipId is null)
        {
            return null;
        }
        var Playable = AnimatorContent.CreatePlayable(Assets, "legacy-preview", MeshId, ClipId, MeshId, Time, false, Loop);
        return Playable.SamplePose(Playable.Time);
    }

    protected override void OnDisable()
    {
        Prepared = new(Entity.EntityId, ComponentId, Mesh.Id, "SKELETON_DISABLED", null);
    }

    protected override void OnUninitialize()
    {
        Inputs.Clear();
        Skeleton = null;
        BoundMesh = null;
        Prepared = null;
    }

    protected internal override ComponentFrameState? CaptureFrameState() { return Prepared; }
}
