using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Editor;

/// <summary>Reusable viewport layout and navigation. The caller supplies and owns the World.</summary>
public sealed class WorldViewportLayout
{
    private Vector3 Pivot;
    private float Distance = 3;
    private float Yaw = 37;
    private float Pitch = -20;
    private ulong StateRevision = 1;
    private Vector3 Minimum = new(-0.5f);
    private Vector3 Maximum = new(0.5f);
    private float Aspect = 1;

    public WorldViewportLayout(string ElementId, string Kind)
    {
        this.ElementId = ElementId;
        this.Kind = Kind;
    }

    public string ElementId
    {
        get;
    }
    public string Kind
    {
        get;
    }

    public string BuildMarkup(string Title)
    {
        var Id = WebUtility.HtmlEncode(ElementId);
        return "<div class=\"viewport-pane asset-world-viewport\"><viewport id=\"" + Id +
            "\" tabindex=\"0\" data-viewport-kind=\"" + WebUtility.HtmlEncode(Kind) +
            "\" data-input-surface=\"" + Id + "\" data-world-viewport=\"" + Id +
            "\" /><div class=\"viewport-footer\"><span>" + WebUtility.HtmlEncode(Title) +
            "</span><div class=\"grow\" /><button data-action=\"editor.world-viewport:" + Id +
            ":focus\">Focus</button><span>RMB Orbit · MMB Pan · Wheel Zoom</span></div></div>";
    }

    public void Frame(WorldDocument World, IEnumerable<Vector3> Positions)
    {
        var Points = Positions.ToArray();
        Minimum = Points.Length == 0 ? new Vector3(-0.5f) : Points.Aggregate(Vector3.Min);
        Maximum = Points.Length == 0 ? new Vector3(0.5f) : Points.Aggregate(Vector3.Max);
        Focus(World);
    }

    public void Render(EditorAssetOpenContext Context, WorldDocument World,
        AssetRegistry? Assets, Func<WorldViewportFrame, WorldViewportFrame>? Overlay = null)
    {
        if (!Context.TryGetElementRect(ElementId, out var Bounds) || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }
        Aspect = Bounds.Width / Bounds.Height;
        var Frame = WorldViewportRenderer.BuildFrame(World, Bounds, assets: Assets);
        Context.SubmitViewportFrame(Kind, Overlay is null ? Frame : Overlay(Frame));
    }

    public bool TryHandleAction(WorldDocument World, string Action)
    {
        var Prefix = "editor.world-viewport:" + ElementId + ":";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var Parts = Action[Prefix.Length..].Split(':');
        if (Parts[0] == "focus")
        {
            Focus(World);
            return true;
        }
        if (Parts.Length != 3)
        {
            return true;
        }
        var ParsedX = float.TryParse(Parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var X);
        var ParsedY = float.TryParse(Parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var Y);
        if (!ParsedX || !ParsedY || !float.IsFinite(X) || !float.IsFinite(Y))
        {
            return true;
        }
        if (Parts[0] == "orbit")
        {
            Yaw += X * 0.35f;
            Pitch = Math.Clamp(Pitch + Y * 0.35f, -89, 89);
        }
        else if (Parts[0] == "pan")
        {
            var Rotation = Quaternion.CreateFromYawPitchRoll(Yaw * MathF.PI / 180, Pitch * MathF.PI / 180, 0);
            var Right = Vector3.Transform(Vector3.UnitX, Rotation);
            var Up = Vector3.Transform(Vector3.UnitY, Rotation);
            Pivot += (-Right * X + Up * Y) * Distance * 0.0015f;
        }
        else if (Parts[0] == "zoom")
        {
            Distance = Math.Clamp(Distance * MathF.Exp(-Y * 0.16f), 0.01f, 10000);
        }
        ApplyCamera(World);
        return true;
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["stateRevision"] = StateRevision,
            ["elementId"] = ElementId,
            ["worldOwnedByCaller"] = true,
            ["pivot"] = Vector(Pivot),
            ["distance"] = Distance,
            ["yaw"] = Yaw,
            ["pitch"] = Pitch,
        };
    }

    private void Focus(WorldDocument World)
    {
        Pivot = (Minimum + Maximum) * 0.5f;
        var Extents = Vector3.Max(Maximum - Minimum, new Vector3(0.01f));
        var Radius = Extents.Length() * 0.5f;
        var HalfFov = 35 * MathF.PI / 360;
        Distance = MathF.Max(0.1f, Radius / MathF.Sin(HalfFov) * MathF.Max(1, 1 / Aspect) * 1.1f);
        ApplyCamera(World);
    }

    private void ApplyCamera(WorldDocument World)
    {
        var Camera = World.ActiveCamera ?? throw new InvalidOperationException("Viewport World has no active camera");
        var Rotation = Quaternion.CreateFromYawPitchRoll(Yaw * MathF.PI / 180, Pitch * MathF.PI / 180, 0);
        var Position = Pivot + Vector3.Transform(Vector3.UnitZ, Rotation) * Distance;
        var Transform = Camera.Components.Single(Component => Component.TypeId == "engine.transform");
        Transform.State["Position"] = Vector(Position);
        Transform.State["Rotation"] = new JsonArray(Pitch, Yaw, 0);
        StateRevision++;
    }

    private static JsonArray Vector(Vector3 Value) { return new JsonArray(Value.X, Value.Y, Value.Z); }
}
