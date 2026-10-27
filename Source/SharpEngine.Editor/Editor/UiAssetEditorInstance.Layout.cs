namespace SharpEngine.Editor;

internal sealed partial class UiAssetEditorInstance
{
    private bool TryHandleLayoutAction(string Action)
    {
        if (Action == "editor.ui-preview-fit")
        {
            var Found = Context.TryGetElementRect("uiDesignViewport", out var Bounds);
            if (Found)
            {
                var Zoom = Math.Min((Bounds.Width - 76) / Document.PreviewWidth, (Bounds.Height - 76) / Document.PreviewHeight);
                MarkupRefreshRequested |= Document.SetPreviewZoom(Math.Clamp(Zoom, 0.01f, 1));
            }
            return true;
        }
        if (Action.StartsWith("editor.ui-preview-zoom:", StringComparison.Ordinal))
        {
            var Valid = float.TryParse(Action["editor.ui-preview-zoom:".Length..],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var Zoom);
            if (Valid)
            {
                MarkupRefreshRequested |= Document.SetPreviewZoom(Zoom);
            }
            return true;
        }
        if (Action.StartsWith("editor.ui-preview-zoom-percent:", StringComparison.Ordinal))
        {
            var Valid = int.TryParse(Action["editor.ui-preview-zoom-percent:".Length..],
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var Percent);
            if (Valid)
            {
                MarkupRefreshRequested |= Document.SetPreviewZoom(Percent / 100f);
            }
            return true;
        }
        if (Action.StartsWith("editor.ui-layout:", StringComparison.Ordinal))
        {
            var Measurements = new Dictionary<string, UiLayoutMetrics>(StringComparer.Ordinal);
            foreach (var Child in Document.SelectedElement()?.Elements() ?? [])
            {
                var Metrics = UiLayoutMetrics.Read(Context.ReadElementLayout(UiEditorDocument.PreviewNodeIdOf(Child)));
                if (Metrics is not null)
                {
                    Measurements[UiEditorDocument.KeyOf(Child)] = Metrics;
                }
            }
            Report(Document.SetContainerLayout(Action["editor.ui-layout:".Length..], Measurements, out var Message), Message);
            return true;
        }
        if (Action.StartsWith("editor.ui-size:", StringComparison.Ordinal))
        {
            var Parts = Action["editor.ui-size:".Length..].Split(':', 2);
            var Metrics = UiLayoutMetrics.Read(Context.ReadElementLayout(SelectedPreviewId));
            if (Parts.Length == 2 && Parts[0] is "width" or "height" && Metrics is not null)
            {
                Report(Document.SetAxisMode(Parts[0] == "width", Parts[1], Metrics, out var Message), Message);
            }
            else
            {
                Context.SetStatus("Layout measurement is not available yet");
            }
            return true;
        }
        if (Action.StartsWith("editor.ui-align:", StringComparison.Ordinal))
        {
            var Parts = Action["editor.ui-align:".Length..].Split(':', 2);
            if (Parts.Length == 2 && Parts[0] is "width" or "height")
            {
                Report(Document.SetLayoutAlignment(Parts[0] == "width", Parts[1], out var Message), Message);
            }
            return true;
        }
        if (Action.StartsWith("editor.ui-preview-size:", StringComparison.Ordinal))
        {
            var Parts = Action["editor.ui-preview-size:".Length..].Split(':', 2);
            var Width = 0;
            var Height = 0;
            var Valid = Parts.Length == 2 && int.TryParse(Parts[0], out Width) && int.TryParse(Parts[1], out Height);
            if (Valid)
            {
                var Changed = Document.SetPreviewSize(Width, Height, out var Message);
                MarkupRefreshRequested |= Changed;
                Context.SetStatus(Changed ? "Preview environment changed; source layout preserved" : Message);
            }
            return true;
        }
        if (Action.StartsWith("editor.ui-preview:", StringComparison.Ordinal))
        {
            var Parts = Action["editor.ui-preview:".Length..].Split(':', 2);
            var Size = 0;
            var Valid = Parts.Length == 2 && Parts[0] is "width" or "height" && int.TryParse(Parts[1], out Size);
            if (Valid)
            {
                var Changed = Document.SetPreviewSize(Parts[0] == "width" ? Size : Document.PreviewWidth,
                    Parts[0] == "height" ? Size : Document.PreviewHeight, out var Message);
                MarkupRefreshRequested |= Changed;
                Context.SetStatus(Changed ? "Preview environment changed; source layout preserved" : Message);
            }
            return true;
        }
        return false;
    }
}
