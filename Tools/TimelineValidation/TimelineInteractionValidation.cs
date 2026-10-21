using System.Numerics;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Editor;
using SharpEngine.Timeline;
using SharpEngine.UI;
using SharpEngine.World;

internal static class TimelineInteractionValidation
{
    internal static void Run(AssetRegistry Assets, TimelineDocument Document, Action<bool, string> Check)
    {
        var View = new TimelineView(Document);
        var Times = new List<TimelineTimeChangedEvent>();
        var States = new List<TimelinePlaybackChangedEvent>();
        View.TimeChanged += Times.Add;
        View.PlaybackChanged += States.Add;
        var Source = Document.Serialize(false);
        View.Play();
        View.Update(0.137);
        Check(View.IsPlaying && Math.Abs(View.State.CurrentTime - 0.137) < 1e-9,
            "Timeline owns its playback clock and advances by the supplied elapsed time");
        Check(Times.Count == 1 && Times[0].PreviousTime == 0 &&
            Times[0].Source == TimelineTimeChangeSource.Playback,
            "External preview subscribers receive coherent previous/current time events");
        View.TryHandleAction("editor.timeline-scrub-start");
        View.TryHandleAction("editor.timeline-seek:0.2137");
        View.TryHandleAction("editor.timeline-seek:0.3271");
        View.TryHandleAction("editor.timeline-scrub-end");
        View.Update(0.25);
        Check(!View.IsPlaying && Math.Abs(View.State.CurrentTime - 0.3271) < 1e-9,
            "Continuous scrub has no tick or frame snapping and remains paused after release");
        Check(States.Count == 2 && States[0].IsPlaying && !States[1].IsPlaying,
            "Scrubbing emits pause without saving or restoring the previous playing state");
        Check(Times.TakeLast(2).All(Event => Event.Source == TimelineTimeChangeSource.Seek),
            "Subscribers can distinguish scrub/seek from normal clock advancement");
        View.Play();
        View.Update(Document.Duration + 1);
        Check(!View.IsPlaying && View.State.CurrentTime == Document.Duration,
            "Playback clamps to the end and stops without wrapping implicitly");
        View.Play();
        Check(View.IsPlaying && View.State.CurrentTime == 0, "Play at the end restarts from the beginning");
        View.Stop();
        Check(!View.IsPlaying && View.State.CurrentTime == 0 &&
            Times[^1].Source == TimelineTimeChangeSource.Stop, "Stop emits a reset event");
        Check(Document.Serialize(false) == Source, "Playback and scrubbing never modify the timeline source");
        var Snapshot = View.BuildSnapshot(800, 260);
        Check(Snapshot.Width == 800 && Snapshot.Height == 260 && Snapshot.TimelineId == Document.TimelineId &&
            Snapshot.Ruler.Any(Tick => Tick.Major) && Snapshot.Ruler.Any(Tick => !Tick.Major),
            "View snapshots preserve dimensions, identity and ruler subdivisions");
        Check(!View.BuildMarkup(800, 260).Contains("<button class=\"timeline-tick", StringComparison.Ordinal),
            "Ruler ticks are visual divisions, not discrete seek buttons");
        var Components = ComponentRegistry.CreateBuiltin();
        var Preview = new AssetPreviewWorld(Components, "viewport-validation");
        Preview.SetMesh("engine://meshes/cube", "engine.mesh-renderer");
        var Layout = new WorldViewportLayout("validationViewport", "validation");
        Layout.Frame(Preview.World, [new Vector3(-0.5f), new Vector3(0.5f)]);
        var Frame = WorldViewportRenderer.BuildFrame(Preview.World, new UiRect(0, 0, 640, 480), assets: Assets);
        Check(Frame.TriangleCount > 0 && Frame.Triangles.Any(Vertex => Vertex.R > 0 || Vertex.G > 0 || Vertex.B > 0),
            "The viewport renders real geometry using lights from the editor-owned temporary World");
        var CameraBefore = Preview.World.ActiveCamera!.Components.Single(Component =>
            Component.TypeId == "engine.transform").State.ToJsonString();
        Layout.TryHandleAction(Preview.World, "editor.world-viewport:validationViewport:orbit:20:10");
        var CameraAfter = Preview.World.ActiveCamera!.Components.Single(Component =>
            Component.TypeId == "engine.transform").State.ToJsonString();
        Check(CameraBefore != CameraAfter, "Viewport navigation modifies only the supplied preview camera");
        var Unlit = Preview.World.Data.Clone();
        foreach (var Light in Unlit.Entities.SelectMany(Entity => Entity.Components).Where(Component =>
            Component.TypeId is "engine.sky-light" or "engine.directional-light"))
        {
            Light.State["Intensity"] = 0;
        }
        var Dark = WorldViewportRenderer.BuildFrame(new WorldDocument(Unlit, Components),
            new UiRect(0, 0, 640, 480), assets: Assets);
        Check(Dark.TriangleCount > 0 && Dark.Triangles.All(Vertex => Vertex.R == 0 && Vertex.G == 0 && Vertex.B == 0),
            "Preview uses the standard scene lighting path with no hidden fallback illumination");
    }
}
