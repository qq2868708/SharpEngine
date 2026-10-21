using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Rendering;

namespace SharpEngine.UI;

/// <summary>A composable 3D UI surface with its own observation camera and content layers.</summary>
public sealed class Viewport3D : IUiLayoutPart, IUiActionHandler
{
    private static readonly UiLayoutTemplate Layout = UiLayoutTemplate.LoadEmbedded(typeof(Viewport3D).Assembly, "SharpEngine.UI.Viewport3D.rml");
    private readonly List<IViewport3DContent> Contents = [];
    private readonly string ActionPrefix;
    private ulong ContentRevision = 1;

    public Viewport3D(string ElementId, string Kind)
    {
        this.ElementId = ElementId;
        this.Kind = Kind;
        ActionPrefix = "ui.viewport3d:" + ElementId + ":";
    }

    public string ElementId
    {
        get;
    }
    public string Kind
    {
        get;
    }
    public OrbitCameraController Navigation
    {
        get;
    } = new();
    public Camera3D Camera
    {
        get
        {
            return Navigation.Camera;
        }
    }

    public void AddContent(IViewport3DContent Content)
    {
        ArgumentNullException.ThrowIfNull(Content);
        if (string.IsNullOrWhiteSpace(Content.ContentId) || Contents.Any(Item => Item.ContentId == Content.ContentId))
        {
            throw new ArgumentException("Viewport content requires a unique non-empty ID", nameof(Content));
        }
        Contents.Add(Content);
        ContentRevision++;
    }

    public bool RemoveContent(string ContentId)
    {
        var Removed = Contents.RemoveAll(Content => Content.ContentId == ContentId) > 0;
        if (Removed)
        {
            ContentRevision++;
        }
        return Removed;
    }

    public string BuildLayout(UiLayoutContext Context) { return BuildMarkup(Context.Title); }

    public string BuildMarkup(string Title)
    {
        return Layout.Render(new(0, 0, Title), new JsonObject
        {
            ["ElementId"] = ElementId,
            ["Kind"] = Kind,
            ["Title"] = Title,
        });
    }

    public void Frame(IEnumerable<Vector3> Positions) { Navigation.Frame(Positions); }

    public void Render(IViewport3DHost Host)
    {
        var Found = Host.TryGetElementRect(ElementId, out var Bounds);
        if (!Found || !float.IsFinite(Bounds.Width) || !float.IsFinite(Bounds.Height) || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }
        Navigation.Aspect = Bounds.Width / Bounds.Height;
        var Context = new ViewportRenderContext(Bounds, Camera);
        var Frames = Contents.Select(Content => Content.Render(Context)).ToArray();
        var Frame = new ViewportFrame(Bounds, Frames.SelectMany(Item => Item.Triangles).ToArray(),
            Frames.SelectMany(Item => Item.Lines).ToArray())
        {
            Diagnostics = Frames.SelectMany(Item => Item.Diagnostics).ToArray(),
        };
        Host.SubmitViewportFrame(Kind, Frame);
    }

    public bool TryHandleAction(string Action)
    {
        if (!Action.StartsWith(ActionPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        var Parsed = Viewport3DAction.TryParse(Action[ActionPrefix.Length..], out var Command);
        if (Parsed)
        {
            Navigation.Execute(Command);
        }
        return true;
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["stateRevision"] = ContentRevision + Navigation.StateRevision,
            ["elementId"] = ElementId,
            ["contentIds"] = new JsonArray(Contents.Select(Content => (JsonNode?)JsonValue.Create(Content.ContentId)).ToArray()),
            ["pivot"] = Vector(Navigation.Pivot),
            ["distance"] = Navigation.Distance,
            ["yaw"] = Navigation.Yaw,
            ["pitch"] = Navigation.Pitch,
            ["position"] = Vector(Camera.Position),
            ["rotation"] = new JsonArray(Camera.Rotation.X, Camera.Rotation.Y, Camera.Rotation.Z, Camera.Rotation.W),
        };
    }

    private static JsonArray Vector(Vector3 Value) { return new JsonArray(Value.X, Value.Y, Value.Z); }
}
