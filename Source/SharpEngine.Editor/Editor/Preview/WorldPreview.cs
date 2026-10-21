using SharpEngine.Assets;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Editor;

/// <summary>World preview is a composition of an ordinary 3D viewport and a World content adapter.</summary>
public sealed class WorldPreview
{
    public WorldPreview(WorldDocument World, AssetRegistry? Assets, string ElementId, string Kind)
    {
        Viewport = new Viewport3D(ElementId, Kind);
        Content = new WorldViewportContent(World, Assets);
        Viewport.AddContent(Content);
    }

    public Viewport3D Viewport
    {
        get;
    }
    public WorldViewportContent Content
    {
        get;
    }
}
