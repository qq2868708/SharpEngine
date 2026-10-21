using System.Numerics;
using System.Runtime.InteropServices;
using SharpEngine.Rendering;
using SharpEngine.UI;

var Assertions = 0;
void Check(bool Condition, string Message)
{
    if (!Condition)
    {
        throw new InvalidOperationException(Message);
    }
    Assertions++;
}
UiLayoutTemplateValidation.Run(Check);
UiLayoutInstanceValidation.Run(Check);
var Content = new TriangleContent("triangle");
var Viewport = new Viewport3D("surface", "generic");
var Host = new RecordingHost();
Viewport.Frame([new Vector3(-1), new Vector3(1)]);
Viewport.Render(Host);
Check(Host.Frame?.TriangleCount == 0, "An empty 3D surface can present without a World");
Viewport.AddContent(Content);
Viewport.Render(Host);
Check(Host.Frame?.TriangleCount == 1 && Host.Kind == "generic", "A world-free content provider renders through the ordinary host");
Check(Marshal.SizeOf<ViewportVertex>() == 7 * sizeof(float), "Generic vertices preserve the native seven-float ABI");
var Before = Host.Frame!.Triangles.ToArray();
var BeforeY = Viewport.Camera.Position.Y;
Viewport.TryHandleAction("ui.viewport3d:surface:orbit:20:20");
Viewport.Render(Host);
Check(Viewport.Camera.Position.Y > BeforeY && !Host.Frame!.Triangles.SequenceEqual(Before),
    "Pointer orbit updates the observation camera and projected geometry");
var Other = new Viewport3D("surface", "other-host");
Other.AddContent(Content);
Other.Frame([new Vector3(-1), new Vector3(1)]);
var OtherState = Other.Describe().ToJsonString();
Viewport.Navigation.Pan(12, 8);
Check(Other.Describe().ToJsonString() == OtherState, "Two controls sharing content retain independent navigation");
var Revision = Viewport.Describe().ToJsonString();
Check(!Viewport.TryHandleAction("ui.viewport3d:foreign:orbit:1:2"), "Foreign control actions are not consumed");
string[] Invalid = ["unknown:1:2", "orbit:NaN:1", "pan:1:Infinity", "focus:ignored", "zoom:1"];
foreach (var Action in Invalid)
{
    Viewport.TryHandleAction("ui.viewport3d:surface:" + Action);
}
Check(Viewport.Describe().ToJsonString() == Revision, "Invalid actions do not mutate camera or revision");
var Rejected = false;
try
{
    Viewport.Navigation.Orbit(float.NaN, 0);
}
catch (ArgumentOutOfRangeException)
{
    Rejected = true;
}
Check(Rejected && Viewport.Describe().ToJsonString() == Revision, "Direct navigation APIs also reject non-finite input");
var Distance = Viewport.Navigation.Distance;
Viewport.Navigation.Zoom(1);
Check(Viewport.Navigation.Distance < Distance, "Navigation can be composed through typed APIs without action strings");
Viewport.Navigation.Focus();
Check(Viewport.Navigation.Pivot == Vector3.Zero, "Focus restores the framed target after panning");
Viewport.AddContent(new TriangleContent("overlay"));
Viewport.Render(Host);
Check(Host.Frame!.TriangleCount == 2, "Multiple independent content layers compose into one presentation");
Rejected = false;
try
{
    Viewport.AddContent(Content);
}
catch (ArgumentException)
{
    Rejected = true;
}
Check(Rejected, "Duplicate content identities are rejected");
Check(Viewport.RemoveContent("triangle") && !Viewport.RemoveContent("triangle"), "Content can detach without assuming ownership");
Viewport.Render(Host);
Check(Host.Frame!.TriangleCount == 1, "Detaching content removes its geometry from the next frame");
var Submissions = Host.Submissions;
Host.Bounds = new UiRect(0, 0, 0, 0);
Viewport.Render(Host);
Check(Host.Submissions == Submissions, "Collapsed surfaces do not submit frames");
Console.WriteLine($"PASS: {Assertions} world-free Viewport3D assertions");

sealed class TriangleContent(string ContentId) : IViewport3DContent
{
    public string ContentId
    {
        get;
    } = ContentId;

    public ViewportFrame Render(ViewportRenderContext Context)
    {
        Vector3[] Positions = [new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)];
        var Matrix = Context.Camera.ViewProjection(Context.Bounds.Width / Context.Bounds.Height);
        var Vertices = Positions.Select(Position =>
        {
            var Clip = Vector4.Transform(new Vector4(Position, 1), Matrix);
            return new ViewportVertex((Clip.X / Clip.W + 1) * 0.5f * Context.Bounds.Width,
                (1 - Clip.Y / Clip.W) * 0.5f * Context.Bounds.Height, Clip.Z / Clip.W, 1, 0, 0, 1);
        }).ToArray();
        return new ViewportFrame(Context.Bounds, Vertices, []);
    }
}

sealed class RecordingHost : IViewport3DHost
{
    internal UiRect Bounds
    {
        get;
        set;
    } = new(20, 40, 640, 480);
    internal ViewportFrame? Frame
    {
        get;
        private set;
    }
    internal string? Kind
    {
        get;
        private set;
    }
    internal int Submissions
    {
        get;
        private set;
    }

    public bool TryGetElementRect(string ElementId, out UiRect Bounds)
    {
        Bounds = this.Bounds;
        return true;
    }

    public void SubmitViewportFrame(string Kind, ViewportFrame Frame)
    {
        this.Kind = Kind;
        this.Frame = Frame;
        Submissions++;
    }
}
