namespace SharpEngine.Editor;

internal sealed class ViewportTransformCustomBar : ICustomBarExtension
{
    public string BarId
    {
        get
        {
            return EditorCustomBarRegistry.ViewportSurfaceId;
        }
    }

    public EditorCustomBarMode Mode
    {
        get
        {
            return EditorCustomBarMode.Append;
        }
    }

    public string BuildMarkup(EditorCustomBarContext Context)
    {
        var Content = Context.CommandButton("core.viewport-move", Icon: "move") +
            Context.CommandButton("core.viewport-rotate", Icon: "rotate") +
            Context.CommandButton("core.viewport-scale", Icon: "scale");
        return Context.Group("Transform", Content, "custom-bar-group viewport-transform-bar");
    }
}
