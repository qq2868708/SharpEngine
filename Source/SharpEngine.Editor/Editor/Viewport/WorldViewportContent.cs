using SharpEngine.Assets;
using SharpEngine.Rendering;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Editor;

/// <summary>Adapts a World to generic 3D content. It observes the supplied camera and never edits World cameras.</summary>
public sealed class WorldViewportContent : IViewport3DContent
{
    public WorldViewportContent(WorldDocument World, AssetRegistry? Assets = null)
    {
        this.World = World;
        this.Assets = Assets;
    }

    public WorldDocument World
    {
        get;
    }
    public AssetRegistry? Assets
    {
        get;
    }
    public string ContentId
    {
        get
        {
            return "world:" + World.Data.WorldId;
        }
    }
    public Func<ViewportRenderContext, ViewportFrame, ViewportFrame>? Overlay
    {
        get;
        set;
    }

    public ViewportFrame Render(ViewportRenderContext Context)
    {
        var Frame = WorldViewportRenderer.BuildPreviewFrame(World, Context.Bounds, Context.Camera, Assets);
        return Overlay is null ? Frame : Overlay(Context, Frame);
    }
}
