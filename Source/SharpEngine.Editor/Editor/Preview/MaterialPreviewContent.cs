using System.Numerics;
using SharpEngine.Assets;
using SharpEngine.Rendering;
using SharpEngine.UI;

namespace SharpEngine.Editor;

/// <summary>Small material-specific content layer composed with the ordinary 3D viewport.</summary>
internal sealed class MaterialPreviewContent(string ContentId, StaticMeshAsset Mesh,
    Func<MaterialAssetDescription> ReadMaterial) : IViewport3DContent
{
    public string ContentId
    {
        get;
    } = ContentId;

    public ViewportFrame Render(ViewportRenderContext Context)
    {
        var Material = ReadMaterial();
        var Color = new Vector4(Material.BaseColor.R, Material.BaseColor.G,
            Material.BaseColor.B, Material.BaseColor.A);
        return MaterialPreviewRenderer.BuildMeshFrame(Mesh.Positions, Mesh.Indices,
            Context.Bounds, Color, Context.Camera);
    }
}
