using System.Globalization;

namespace SharpEngine.Editor;

internal sealed partial class UiAssetEditorInstance
{
    private readonly record struct DesignContentBounds(float X, float Y, float Width, float Height);

    private bool TryHandleLayoutAction(string Action)
    {
        if (Action == "editor.ui-preview-fit")
        {
            var Found = Context.TryGetElementRect("uiDesignViewport", out var Bounds);
            if (Found)
            {
                var Zoom = Math.Min((Bounds.Width - 76) / Document.PreviewWidth,
                    (Bounds.Height - 76) / Document.PreviewHeight);
                if (Document.SetPreviewZoom(Zoom))
                {
                    Document.SetPreviewOffset((Bounds.Width - Document.PreviewWidth * Zoom) / 2,
                        (Bounds.Height - Document.PreviewHeight * Zoom) / 2);
                    MarkupRefreshRequested = true;
                }
            }
            return true;
        }
        if (Action.StartsWith("editor.ui-preview-wheel:", StringComparison.Ordinal))
        {
            var Parts = Action["editor.ui-preview-wheel:".Length..].Split(':');
            var Valid = float.TryParse(Parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var Delta);
            var AnchorX = 0f;
            var AnchorY = 0f;
            if (Valid && Parts.Length == 3)
            {
                Valid = float.TryParse(Parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out AnchorX) &&
                    float.TryParse(Parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out AnchorY);
            }
            if (Valid && float.IsFinite(Delta) && float.IsFinite(AnchorX) && float.IsFinite(AnchorY))
            {
                var Zoom = Document.PreviewZoom * MathF.Pow(1.1f, Delta);
                var Ratio = Zoom / Document.PreviewZoom;
                var X = AnchorX - (AnchorX - Document.PreviewOffsetX) * Ratio;
                var Y = AnchorY - (AnchorY - Document.PreviewOffsetY) * Ratio;
                if (float.IsFinite(X) && float.IsFinite(Y) && Document.SetPreviewZoom(Zoom))
                {
                    Document.SetPreviewOffset(X, Y);
                    MarkupRefreshRequested = true;
                }
            }
            return true;
        }
        if (Action.StartsWith("editor.ui-preview-pan:", StringComparison.Ordinal))
        {
            var Parts = Action["editor.ui-preview-pan:".Length..].Split(':', 2);
            var X = 0f;
            var Y = 0f;
            var Valid = Parts.Length == 2 &&
                float.TryParse(Parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out X) &&
                float.TryParse(Parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out Y);
            if (Valid)
            {
                Document.SetPreviewOffset(X, Y);
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
                Context.SetStatus(Changed ? "Design canvas size changed; Runtime window size unchanged" : Message);
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
                Context.SetStatus(Changed ? "Design canvas size changed; Runtime window size unchanged" : Message);
            }
            return true;
        }
        return false;
    }

    private void UpdateDesignContentBounds()
    {
        var Bounds = ReadDesignContentBounds();
        if (Bounds == PublishedDesignContentBounds)
        {
            return;
        }
        PublishedDesignContentBounds = Bounds;
        PendingLiveProperties.Add(new("uiDesignContentBounds", "display",
            Bounds is null ? "none" : "block"));
        if (Bounds is null)
        {
            return;
        }
        PendingLiveProperties.Add(new("uiDesignContentBounds", "left",
            FormatPixels(Bounds.Value.X)));
        PendingLiveProperties.Add(new("uiDesignContentBounds", "top",
            FormatPixels(Bounds.Value.Y)));
        PendingLiveProperties.Add(new("uiDesignContentBounds", "width",
            FormatPixels(Bounds.Value.Width)));
        PendingLiveProperties.Add(new("uiDesignContentBounds", "height",
            FormatPixels(Bounds.Value.Height)));
        PendingLiveProperties.Add(new("uiDesignContentBoundsLabel", "text-content",
            Math.Round(Bounds.Value.Width, 1).ToString("0.#", CultureInfo.InvariantCulture) +
            " x " + Math.Round(Bounds.Value.Height, 1).ToString("0.#", CultureInfo.InvariantCulture)));
    }

    private DesignContentBounds? ReadDesignContentBounds()
    {
        var HasBounds = false;
        var MinX = float.MaxValue;
        var MinY = float.MaxValue;
        var MaxX = float.MinValue;
        var MaxY = float.MinValue;
        foreach (var Element in Document.DesignBodyElement?.Elements() ?? [])
        {
            var Metrics = UiLayoutMetrics.Read(
                Context.ReadElementLayout(UiEditorDocument.PreviewNodeIdOf(Element)));
            if (Metrics is null || !float.IsFinite(Metrics.X) || !float.IsFinite(Metrics.Y) ||
                !float.IsFinite(Metrics.Width) || !float.IsFinite(Metrics.Height) ||
                Metrics.Width <= 0 || Metrics.Height <= 0)
            {
                continue;
            }
            HasBounds = true;
            MinX = Math.Min(MinX, Metrics.X);
            MinY = Math.Min(MinY, Metrics.Y);
            MaxX = Math.Max(MaxX, Metrics.X + Metrics.Width);
            MaxY = Math.Max(MaxY, Metrics.Y + Metrics.Height);
        }
        return HasBounds ? new DesignContentBounds(MinX, MinY, MaxX - MinX, MaxY - MinY) : null;
    }

    private static string FormatPixels(float Value)
    {
        return Math.Round(Value, 2).ToString("0.##", CultureInfo.InvariantCulture) + "px";
    }
}
