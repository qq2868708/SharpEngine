using SharpEngine.Assets;

namespace SharpEngine.Components;

[ComponentTypeId("engine.static-mesh"), ComponentSchemaVersion(1),
    AddComponentMenu("Rendering/Static Mesh"), RequireComponent(typeof(TransformComponent)),
    DisallowMultipleComponent]
public sealed partial class StaticMeshComponent : SharpComponent
{
    public AssetRef<StaticMeshAsset> Mesh;
    public AssetRef<MaterialAsset> Material;
    public Color Color = Color.White;
}
