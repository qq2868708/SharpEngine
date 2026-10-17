namespace SharpEngine.Components;

[ComponentTypeId("engine.mesh-renderer"), ComponentSchemaVersion(1), AddComponentMenu("Rendering/Mesh Renderer")]
public sealed partial class MeshRendererComponent : SharpComponent
{
    public AssetRef<MeshAsset> Mesh;
    public AssetRef<MaterialAsset> Material;
    public Color Color = Color.White;
}
