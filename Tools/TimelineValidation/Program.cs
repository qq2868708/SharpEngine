using System.Reflection;
using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Playables;
using SharpEngine.Modules;
using SharpEngine.Timeline;

var root = args.Length == 0 ? Directory.GetCurrentDirectory() : Path.GetFullPath(args[0]);
var assertions = 0;
void Check(bool value, string message)
{
    assertions++;
    if (!value) throw new InvalidOperationException(message);
}

var registry = AssetRegistry.Load(root);
Check(registry.Diagnostics.All(diagnostic => !diagnostic.IsError), "Animation assets must be present in the engine manifest");
Check(typeof(TimelineDocument).Assembly.GetName().Name == "SharpEngine.Timeline.Runtime" &&
      typeof(TimelineView).Assembly.GetName().Name == "SharpEngine.Timeline.Editor",
    "Timeline Runtime and Editor APIs must be compiled into separate Engine Modules");
var runtimeModule = typeof(TimelineRuntimeModule).Assembly.GetCustomAttribute<SharpEngineModuleAttribute>();
var editorModule = typeof(TimelineEditorModule).Assembly.GetCustomAttribute<SharpEngineModuleAttribute>();
Check(runtimeModule is { Id: "SharpEngine.Timeline.Runtime", Type: "Runtime" } &&
      editorModule is { Id: "SharpEngine.Timeline.Editor", Type: "Editor" },
    "Timeline assemblies must declare explicit Engine Module lifecycle metadata");
Check(typeof(AssetRegistry).Assembly.GetName().Name == "SharpEngine.Runtime" &&
      typeof(TimelineDocument).Assembly != typeof(AssetRegistry).Assembly,
    "Timeline feature code must not be folded back into the core Runtime assembly");
var animation = SkeletalAnimationContent.Load(registry, "engine://animations/robot-walk");
Check(animation.Bones.Count == 3 && animation.Tracks.Count == 3 && Math.Abs(animation.Duration - 1) < 0.0001, "Robot walk asset must load with its skeleton and tracks");
var roundTrip = SkeletalAnimationAsset.Parse(animation.Serialize(false));
Check(roundTrip.Serialize(false) == animation.Serialize(false), "Skeletal animation serialization must be deterministic");
var NotifiedAnimation = animation.WithNotifies([
    new AnimationNotify("footstep", "Footstep", 0.5, 0.1),
]);
var NotifiedRoundTrip = SkeletalAnimationAsset.Parse(NotifiedAnimation.Serialize(false));
Check(NotifiedRoundTrip.Notifies.Count == 1 &&
      NotifiedRoundTrip.Notifies[0] == new AnimationNotify("footstep", "Footstep", 0.5, 0.1),
    "Skeletal animation Notify data must round-trip through the asset schema");
var middle = animation.Sample(0.25).Get("left-foot");
Check(Math.Abs(animation.Sample(0.25).Get("root").Position.Y - 0.02f) < 0.0001f && middle.Scale == Vector3.One, "Bone tracks must interpolate into a pose");
Check(Math.Abs(animation.SampleLooped(1.25).Time - 0.25) < 0.0001, "Looped sampling must wrap at clip duration");

var timeline = TimelineContent.Load(registry, "engine://timelines/robot-walk");
Check(timeline.Tracks.Count == 1 && timeline.Tracks[0].Clips.Count == 1, "Timeline asset must load its generic track and clip");
Check(TimelineDocument.Parse(timeline.Serialize(false)).Serialize(false) == timeline.Serialize(false), "Timeline serialization must round-trip deterministically");
var clip = timeline.Tracks[0].Clips[0];
var playable = new SkeletalAnimationPlayable(clip.PlayableId, animation, timeline.Tracks[0].TargetId);
var composite = new TimelinePlayable(timeline, new Resolver(playable));
composite.Play();
composite.Advance(1.25);
var sink = new ListPlayableOutputSink();
composite.Evaluate(new PlayableEvaluationContext(1.25, 1.25, 1), sink);
Check(sink.Outputs.Count == 1 && sink.Outputs[0].PropertyPath == "skeleton.pose" && sink.Outputs[0].TargetId == "robot", "Timeline playable must emit the active skeletal pose to the track target");
var graph = new PlayableGraph();
graph.Add(new SkeletalAnimationPlayable("graph-robot", animation, "robot"));
graph.Play();
Check(graph.Evaluate(0.25).Count == 1 && graph.FrameIndex == 1, "Playable graph must advance and evaluate nodes deterministically");

var view = new TimelineView(timeline);
view.Seek(1.25);
Check(view.SelectClip(clip.ClipId), "Timeline view must select a stable clip ID");
var snapshot = view.BuildSnapshot(1000, 300);
Check(snapshot.Rows.Count == 1 && snapshot.Rows[0].Clips.Count == 1, "Generic timeline view must expose tracks and clips");
Check(snapshot.PlayheadX > snapshot.HeaderWidth && snapshot.Ruler.Count > 0, "Timeline view must produce ruler ticks and a playhead");
Check(view.BuildMarkup(1000, 300).Contains("data-timeline-action=\"select-clip:robot-walk-clip\"", StringComparison.Ordinal), "Timeline markup must expose semantic clip selection");

var authoring = new TimelineAuthoringSession(timeline);
var notifyTrack = new TimelineTrack("notify", "Animation Notify", "robot", "notify",
    [new TimelineClip("footstep", "Footstep", 0.5, 0.1)]);
var editedTimeline = new TimelineDocument(timeline.TimelineId, timeline.Name, timeline.Duration,
    timeline.FrameRate, timeline.Tracks.Append(notifyTrack).ToArray());
var editResult = authoring.Replace(editedTimeline, "add-notify", authoring.DocumentRevision);
Check(editResult.Success && authoring.IsDirty && authoring.DocumentRevision == 2,
    "Timeline authoring edits must commit a revision and mark the source dirty");
Check(authoring.Undo(authoring.DocumentRevision).Success &&
      authoring.Document.Tracks.All(track => track.TrackId != "notify"),
    "Timeline authoring Undo must restore the committed source document");
Check(authoring.Redo(authoring.DocumentRevision).Success &&
      authoring.Document.Tracks.Any(track => track.TrackId == "notify"),
    "Timeline authoring Redo must restore the edited source document");
var savedTimelinePath = Path.Combine(Path.GetTempPath(), $"sharpengine-timeline-{Guid.NewGuid():N}.json");
try
{
    authoring.Save(savedTimelinePath);
    Check(!authoring.IsDirty && TimelineDocument.Parse(File.ReadAllText(savedTimelinePath)).Tracks.Count == 2,
        "Timeline authoring Save must validate and atomically persist the edited source");
}
finally
{
    if (File.Exists(savedTimelinePath)) File.Delete(savedTimelinePath);
}

var genericTimeline = new TimelineDocument("generic-events", "Generic Events", 2, 30,
[
    new TimelineTrack("events", "Events", "game", "event",
    [new TimelineClip("marker-clip", "marker", 0.25, 1.0)])
]);
var marker = new MarkerPlayable("marker", 1.0);
var genericComposite = new TimelinePlayable(genericTimeline, new Resolver(marker));
genericComposite.Play();
genericComposite.Advance(0.5);
var genericSink = new ListPlayableOutputSink();
genericComposite.Evaluate(new PlayableEvaluationContext(0.5, 0.5, 1), genericSink);
Check(genericSink.Outputs.Count == 1 && genericSink.Outputs[0].TargetId == "game" && genericSink.Outputs[0].PropertyPath == "event.marker",
    "Generic timeline must schedule a non-animation Playable through the same contract");
try
{
    _ = SkeletalAnimationAsset.Parse(animation.Serialize(false).Replace("\"tracks\":", "\"unknown\":0,\"tracks\":", StringComparison.Ordinal));
    throw new InvalidOperationException("Unknown skeletal animation fields must be rejected");
}
catch (InvalidDataException) { assertions++; }

Console.WriteLine($"[TimelineValidation] PASS: {assertions} assertions; bones={animation.Bones.Count}; tracks={timeline.Tracks.Count}; ticks={snapshot.Ruler.Count}");

sealed class Resolver(IPlayable playable) : IPlayableResolver
{
    public IPlayable Resolve(string playableId) => playable.PlayableId == playableId
        ? playable : throw new KeyNotFoundException(playableId);
}

sealed class MarkerPlayable(string playableId, double duration) : PlayableBase(playableId, duration)
{
    public override void Evaluate(in PlayableEvaluationContext context, IPlayableOutputSink output) =>
        output.Emit(new PlayableOutput(PlayableId, "unused", "event.marker", JsonValue.Create(Math.Round(context.Time, 3))));
}
