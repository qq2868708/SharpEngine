using System.Globalization;

namespace SharpEngine.UI;

internal enum Viewport3DActionKind
{
    Focus,
    Orbit,
    Pan,
    Zoom,
}

internal readonly record struct Viewport3DAction(Viewport3DActionKind Kind, float X, float Y)
{
    internal static bool TryParse(string Payload, out Viewport3DAction Action)
    {
        Action = default;
        if (Payload == "focus")
        {
            Action = new(Viewport3DActionKind.Focus, 0, 0);
            return true;
        }
        var Parts = Payload.Split(':');
        if (Parts.Length != 3)
        {
            return false;
        }
        var Kind = Parts[0] switch
        {
            "orbit" => Viewport3DActionKind.Orbit,
            "pan" => Viewport3DActionKind.Pan,
            "zoom" => Viewport3DActionKind.Zoom,
            _ => (Viewport3DActionKind?)null,
        };
        var ParsedX = float.TryParse(Parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var X);
        var ParsedY = float.TryParse(Parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var Y);
        if (Kind is null || !ParsedX || !ParsedY || !float.IsFinite(X) || !float.IsFinite(Y))
        {
            return false;
        }
        Action = new(Kind.Value, X, Y);
        return true;
    }
}
