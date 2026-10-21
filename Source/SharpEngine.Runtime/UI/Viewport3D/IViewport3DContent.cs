using SharpEngine.Rendering;

namespace SharpEngine.UI;

public readonly record struct ViewportRenderContext(UiRect Bounds, Camera3D Camera);

public interface IViewport3DContent
{
    string ContentId
    {
        get;
    }
    ViewportFrame Render(ViewportRenderContext Context);
}

public interface IViewport3DHost
{
    bool TryGetElementRect(string ElementId, out UiRect Bounds);
    void SubmitViewportFrame(string Kind, ViewportFrame Frame);
}
