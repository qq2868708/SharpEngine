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
}
