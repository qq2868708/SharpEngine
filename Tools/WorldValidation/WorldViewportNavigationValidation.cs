using System.Numerics;
using SharpEngine.Editor;
using SharpEngine.Rendering;
using SharpEngine.UI;
using SharpEngine.World;

internal static class WorldViewportNavigationValidation
{
    internal static void Run(WorldDocument Source, Action<bool, string> Check)
    {
        var Before = Source.Serialize();
        var First = new WorldPreview(Source, null, "first", "first-host");
        var Second = new WorldPreview(Source, null, "second", "second-host");
        First.Viewport.Frame([new Vector3(-1), new Vector3(1)]);
        Second.Viewport.Frame([new Vector3(-1), new Vector3(1)]);
        var SecondState = Second.Viewport.Describe().ToJsonString();
        var BeforeY = First.Viewport.Camera.Position.Y;
        First.Viewport.TryHandleAction("ui.viewport3d:first:orbit:20:20");
        Check(First.Viewport.Camera.Position.Y > BeforeY, "Dragging down raises the independent observation camera");
        Check(Second.Viewport.Describe().ToJsonString() == SecondState,
            "Two previews of the same World retain independent cameras");
        Check(Source.Serialize() == Before, "Framing and orbiting never modify the supplied World or its camera entity");
        var Bounds = new UiRect(0, 0, 640, 480);
        var FirstFrame = First.Content.Render(new(Bounds, First.Viewport.Camera));
        var SecondFrame = Second.Content.Render(new(Bounds, Second.Viewport.Camera));
        Check(!FirstFrame.Triangles.SequenceEqual(SecondFrame.Triangles),
            "The same World produces different projections for independent view cameras");
        var NoCamera = Source.Data.Clone();
        NoCamera.ActiveCameraId = null;
        NoCamera.Entities.RemoveAll(Entity => Entity.Components.Any(Component => Component.TypeId == "engine.camera"));
        var World = new WorldDocument(NoCamera, Source.Registry);
        var Content = new WorldViewportContent(World);
        var Snapshot = World.Serialize();
        var Frame = Content.Render(new(Bounds, First.Viewport.Camera));
        Check(Frame.TriangleCount > 0 && World.ActiveCamera is null,
            "A World without any camera entity can render through a view camera");
        Check(World.Serialize() == Snapshot, "Rendering never injects a camera or other records into World data");
        First.Viewport.Navigation.Pan(20, 10);
        First.Viewport.Navigation.Zoom(1);
        First.Viewport.Navigation.Focus();
        Check(Source.Serialize() == Before, "Pan, zoom and focus also leave the original World unchanged");
        Check(ReferenceEquals(First.Content.World, Source) && ReferenceEquals(Second.Content.World, Source),
            "World adapters attach caller-owned content without manufacturing World copies");
        var BeforeHostFrame = new RecordingHost();
        First.Viewport.Render(BeforeHostFrame);
        Check(BeforeHostFrame.Frame?.TriangleCount > 0, "Composed World preview presents through the generic host");
    }

    private sealed class RecordingHost : IViewport3DHost
    {
        internal ViewportFrame? Frame
        {
            get;
            private set;
        }
        public bool TryGetElementRect(string ElementId, out UiRect Bounds)
        {
            Bounds = new UiRect(0, 0, 640, 480);
            return true;
        }
        public void SubmitViewportFrame(string Kind, ViewportFrame Frame)
        {
            this.Frame = Frame;
        }
    }
}
