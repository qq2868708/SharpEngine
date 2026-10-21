using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Editor;
using SharpEngine.Playables;
using SharpEngine.UI;
using SharpEngine.World;

var Root = Path.GetFullPath(args.Length == 0 ? Directory.GetCurrentDirectory() : args[0]);
var Count = 0;
void Check(bool Condition, string Message)
{
    if (!Condition)
    {
        throw new Exception(Message);
    }
    Count++;
}
void Reject(Action Action, string Message, string? Code = null)
{
    try
    {
        Action();
    }
    catch (Exception Error)
    {
        Check(Code is null || Error is ComponentValidationException Validation && Validation.Code == Code, Message + ": diagnostic");
        return;
    }
    throw new Exception("Expected rejection: " + Message);
}
bool Near(double First, double Second) { return Math.Abs(First - Second) < 1e-6; }
var Assets = AssetRegistry.Load(Root);
var Registry = ComponentRegistry.CreateBuiltin();
Registry.RegisterAssembly(typeof(AnimationPhaseProbe).Assembly, "animation-validation", "1");
Registry.ValidateRegistrations();
var Descriptor = Registry.Find("engine.animator")!;
WorldViewportFrame PreviewFrame(SceneData Scene)
{
    var Preview = new WorldDocument(Scene, Registry);
    SceneRenderPreparation.Prepare(Preview, Assets);
    return SceneRenderer.BuildFrame(Preview, new UiRect(0, 0, 800, 500), Assets);
}
var Data = new SceneData();
var Entity = Data.CreateEntity(Registry, "Animator");
var Added = Data.AddComponent(Registry, Entity.EntityId, "engine.animator");
var Animator = Added.Single(Component => Component.TypeId == "engine.animator");
var Mesh = Data.AddComponent(Registry, Entity.EntityId, "engine.skeletal-mesh").Single(Component => Component.TypeId == "engine.skeletal-mesh");
Check(Added.Count == 1 && Descriptor.Single, "Animator has an explicit optional target without adding a mesh");
Check(Descriptor.SchemaVersion == 3 && Descriptor.Schema(Registry)["$schema"]!.GetValue<string>().Contains("2020-12"), "versioned schema discovery");
Check(Animator.State.Count == 11 && Animator.State["Target"] is null && Animator.State["Clip"] is null && Animator.State["Time"]!.GetValue<double>() == 0 && Animator.State["Speed"]!.GetValue<float>() == 1 && Animator.State["Playing"]!.GetValue<bool>() && Animator.State["Loop"]!.GetValue<bool>() && Animator.State["Controller"] is null && Animator.State["StateId"]!.GetValue<string>() == "" && Animator.State["Parameters"]!.AsArray().Count == 0 && Animator.State["MovementSource"] is null && Animator.State["MovementSpeedParameter"]!.GetValue<string>() == "", "explicit defaults");
var Defaults = Data.Serialize();
Check(SceneData.Parse(Defaults, Registry).Serialize() == Defaults, "default deterministic serialization");
using (var Runtime = new RuntimeWorld(Data, Registry))
{
    Check(Runtime.Snapshot().Serialize() == Defaults, "default runtime reconstruction");
    Check(new AnimationSnapshot(Runtime.Frame).ToJson()["animators"]![0]!["status"]!.GetValue<string>() == "Unbound", "missing target is explicitly unbound");
}
Reject(() => Data.AddComponent(Registry, Entity.EntityId, "engine.animator"), "single Animator restriction");
var Removable = Data.Clone();
Removable.RemoveComponent(Registry, Entity.EntityId, Mesh.ComponentId);
Check(Removable.Entities[0].Components.All(Component => Component.TypeId != "engine.skeletal-mesh"), "Animator permits independent target removal");
foreach (var Version in new[]
{
    0,
    4,
})
{
    var Invalid = Data.Clone();
    Invalid.Entities[0].Components.Single(Component => Component.TypeId == "engine.animator").SchemaVersion = Version;
    Reject(() => Invalid.Validate(Registry), "unsupported component version " + Version);
}
foreach (var Field in new[]
{
    "Time",
    "Speed",
})
{
    var State = Descriptor.CreateDefaultState(Registry);
    State[Field] = -1;
    Reject(() => Registry.ValidateState(Descriptor, State), "negative " + Field, Field == "Time" ? "ANIMATOR_TIME" : "ANIMATOR_SPEED");
    State[Field] = "NaN";
    Reject(() => Registry.ValidateState(Descriptor, State), "non-numeric " + Field);
}
var UnknownField = Descriptor.CreateDefaultState(Registry);
UnknownField["HiddenClock"] = 1;
Reject(() => Registry.ValidateState(Descriptor, UnknownField), "unknown authoritative field");
Animator.State["Target"] = Mesh.ComponentId;
Animator.State["Clip"] = "engine://animations/robot-walk";
Animator.State["Time"] = 0.125;
Animator.State["Speed"] = 2f;
Animator.State["Loop"] = false;
Animator.State["Playing"] = false;
Mesh.State["Mesh"] = "engine://meshes/robot-skeletal";
var NonDefaults = Data.Serialize();
Check(SceneData.Parse(NonDefaults, Registry).Serialize() == NonDefaults, "non-default deterministic serialization");
using (var Runtime = new RuntimeWorld(Data, Registry, Assets))
{
    Check(Runtime.Snapshot().Serialize() == NonDefaults, "non-default runtime reconstruction preserves authoritative fields");
    Check(new AnimationSnapshot(Runtime.Frame).ToJson()["animators"]![0]!["status"]!.GetValue<string>() == "Paused", "paused state reconstructed through Playable");
}
Registry.AssetExists = (Id, Type) => Assets.Find(Id) is { Exists: true } Asset && Asset.Type == Type.Name.Replace("Asset", "");
Data.Validate(Registry, cook: true);
var WrongAsset = Data.Clone();
WrongAsset.Entities[0].Components.Single(Component => Component.TypeId == "engine.animator").State["Clip"] = "engine://meshes/robot-skeletal";
Reject(() => WrongAsset.Validate(Registry, cook: true), "Cook prevalidation rejects wrong typed asset reference");
var MissingAsset = Data.Clone();
MissingAsset.Entities[0].Components.Single(Component => Component.TypeId == "engine.animator").State["Clip"] = "engine://animations/missing";
Reject(() => MissingAsset.Validate(Registry, cook: true), "Cook prevalidation rejects missing animation");
Registry.AssetExists = null;

var Document = new WorldDocument(Data.Clone(), Registry, "animation-fixture");
var Authoring = new WorldAuthoringSession(Document);
var Edit = Authoring.PatchComponent(Entity.EntityId, Animator.ComponentId, new JsonObject
{
    ["Time"] = 0.25,
    ["Speed"] = 1.5f,
    ["Playing"] = true,
    ["Loop"] = true,
}, Authoring.DocumentRevision);
Check(Edit.Success && Edit.Transaction is not null && Authoring.IsDirty && Edit.AfterRevision == Edit.BeforeRevision + 1, "multi-field Animator patch is one tracked transaction");
Check(Authoring.Undo(Authoring.DocumentRevision).Success && Document.Serialize() == NonDefaults, "Undo restores complete Animator state");
Check(Authoring.Redo(Authoring.DocumentRevision).Success && Document.Data.Entities[0].Components.Single(Component => Component.TypeId == "engine.animator").State["Time"]!.GetValue<double>() == 0.25, "Redo restores Animator transaction");
var BeforeInvalid = Document.Serialize();
var BeforeRevision = Authoring.DocumentRevision;
var Rejected = Authoring.PatchComponent(Entity.EntityId, Animator.ComponentId, new JsonObject
{
    ["Time"] = 0.75,
    ["Speed"] = -1,
}, BeforeRevision);
Check(!Rejected.Success && Authoring.DocumentRevision == BeforeRevision && Document.Serialize() == BeforeInvalid, "invalid patch is atomic");
var Stale = Authoring.PatchComponent(Entity.EntityId, Animator.ComponentId, new JsonObject
{
    ["Time"] = 0,
}, BeforeRevision - 1);
Check(!Stale.Success && Document.Serialize() == BeforeInvalid, "stale revision rejected");
Check(WorldDocument.Parse(Document.Serialize(), registry: Registry).Serialize() == Document.Serialize(), "saved authoring round trip");
var SavePath = Path.Combine(Path.GetTempPath(), $"SharpEngine-Animation-{Guid.NewGuid():N}.sworld");
try
{
    Authoring.Save(SavePath);
    Check(!Authoring.IsDirty && Authoring.SavepointRevision == Authoring.DocumentRevision &&
        WorldDocument.Load(SavePath, Registry).Serialize() == Document.Serialize(), "explicit atomic Save preserves Animator state and updates savepoint");
}
finally
{
    File.Delete(SavePath);
}

Animator.State["Time"] = 0d;
Animator.State["Speed"] = 1f;
Animator.State["Loop"] = true;
Animator.State["Playing"] = true;
var Camera = Data.CreateEntity(Registry, "Camera");
Data.AddComponent(Registry, Camera.EntityId, "engine.camera");
Camera.Components.Single(Component => Component.TypeId == "engine.transform").State["Position"] = new JsonArray(0f, 0.5f, 4f);
Data.ActiveCameraId = Camera.EntityId;
var Probe = Data.AddComponent(Registry, Entity.EntityId, "57cd3ab0-aa35-4ce9-ad6c-71d71d812aef").Single();
Probe.State["Animator"] = Animator.ComponentId;
var Clip = SkeletalAnimationContent.Load(Assets, "engine://animations/robot-walk");
using (var Runtime = new RuntimeWorld(Data, Registry, Assets))
{
    var Live = (AnimatorComponent)Runtime.FindComponent(Animator.ComponentId)!;
    var Phase = (AnimationPhaseProbe)Runtime.FindComponent(Probe.ComponentId)!;
    var World = new WorldDocument(Runtime.Frame, Registry);
    var FirstFrame = SceneRenderer.BuildFrame(World, new UiRect(0, 0, 800, 500), Assets);
    var Reference = new SkeletalAnimationPlayable("reference", Clip, Entity.EntityId);
    Reference.Play();
    Runtime.Tick(0.25f);
    Reference.Advance(0.25);
    Check(Near(Live.Time, Reference.Time) && Phase.UpdateTime == 0 && Near(Phase.LateTime, 0.25), "Animator advances via Playable after all Update and before LateUpdate callbacks");
    World = new WorldDocument(Runtime.Frame, Registry);
    var SecondFrame = SceneRenderer.BuildFrame(World, new UiRect(0, 0, 800, 500), Assets);
    Check(FirstFrame.TriangleCount > 0 && FirstFrame.Diagnostics.Count == 0 && SecondFrame.Diagnostics.Count == 0 && !FirstFrame.Triangles.SequenceEqual(SecondFrame.Triangles), "Playable time changes actual skinned renderer geometry");
    var Legacy = Runtime.Snapshot();
    var LegacyComponents = Legacy.Entities.Single(Item => Item.EntityId == Entity.EntityId).Components;
    var LegacyMesh = LegacyComponents.Single(Component => Component.TypeId == "engine.skeletal-mesh");
    LegacyMesh.State["Animation"] = "engine://animations/robot-walk";
    LegacyMesh.State["AnimationTime"] = 0.25f;
    LegacyComponents.Single(Component => Component.TypeId == "engine.animator").Enabled = false;
    var LegacyFrame = PreviewFrame(Legacy);
    Check(SecondFrame.Triangles.SequenceEqual(LegacyFrame.Triangles), "disabled Animator preserves legacy skeletal sampling");
    LegacyComponents.Single(Component => Component.TypeId == "engine.animator").Enabled = true;
    LegacyComponents.Single(Component => Component.TypeId == "engine.animator").State["Clip"] = null;
    var EmptyClipFrame = PreviewFrame(Legacy);
    LegacyComponents.Single(Component => Component.TypeId == "engine.animator").Enabled = false;
    LegacyMesh.State["Animation"] = null;
    var BindFrame = PreviewFrame(Legacy);
    Check(BindFrame.Triangles.SequenceEqual(EmptyClipFrame.Triangles), "enabled empty Animator selects bind pose");
    Check(Live.Entity.GetComponent<SkeletalMeshComponent>()!.Animation.Id is null && Live.Entity.GetComponent<SkeletalMeshComponent>()!.AnimationTime == 0, "Animator does not duplicate playhead into legacy mesh state");
    var Observation = new AnimationSnapshot(Runtime.Frame).ToJson();
    Check(Observation["schemaVersion"]!.GetValue<int>() == 2 && Observation["revision"]!.GetValue<ulong>() == Runtime.Revision && Near(Observation["animators"]![0]!["sampleTime"]!.GetValue<double>(), Live.Time), "versioned observations match committed revision and Playable time");
    var Published = Runtime.Snapshot().Serialize();
    Live.Time = 0.7;
    Check(Runtime.Snapshot().Serialize() == Published && Near(new AnimationSnapshot(Runtime.Frame).ToJson()["animators"]![0]!["sampleTime"]!.GetValue<double>(), 0.25), "observations read committed state across direct component writes");
    _ = Runtime.Patch(Live.ComponentId, new JsonObject
    {
        ["Playing"] = false,
        ["Time"] = 0.25,
    }, Runtime.Revision).Value;
    Runtime.Tick(0.25f);
    Check(Near(Live.Time, 0.25), "paused playhead does not advance");
    _ = Runtime.Patch(Live.ComponentId, new JsonObject
    {
        ["Playing"] = false,
        ["Time"] = 0,
    }, Runtime.Revision).Value;
    Runtime.Tick(0.125f);
    Check(Live.Time == 0 && !Live.Playing, "atomic stop patch resets and freezes playhead");
    _ = Runtime.Patch(Live.ComponentId, new JsonObject
    {
        ["Time"] = Clip.Duration * 3 + 0.125,
    }, Runtime.Revision).Value;
    Check(Near(new AnimationSnapshot(Runtime.Frame).ToJson()["animators"]![0]!["sampleTime"]!.GetValue<double>(), 0.125), "loop seek wraps through generic Playable");
    _ = Runtime.Patch(Live.ComponentId, new JsonObject
    {
        ["Playing"] = true,
        ["Speed"] = 2,
        ["Time"] = Clip.Duration - 0.125,
    }, Runtime.Revision).Value;
    Runtime.Tick(0.25f);
    Check(Near(Live.Time, 0.375), "loop and playback speed follow Playable advancement");
    _ = Runtime.Patch(Live.ComponentId, new JsonObject
    {
        ["Loop"] = false,
        ["Time"] = Clip.Duration - 0.125,
    }, Runtime.Revision).Value;
    Runtime.Tick(0.25f);
    Check(!Live.Playing && Near(Live.Time, Clip.Duration) && new AnimationSnapshot(Runtime.Frame).ToJson()["animators"]![0]!["status"]!.GetValue<string>() == "Completed", "once mode completes through Playable state");
    _ = Runtime.Patch(Live.ComponentId, new JsonObject
    {
        ["Playing"] = true,
        ["Time"] = 0,
        ["Speed"] = 0,
    }, Runtime.Revision).Value;
    Runtime.Tick(0.25f);
    Check(Live.Time == 0 && Live.Playing, "zero speed retains playing state");
    _ = Runtime.Patch(Live.ComponentId, new JsonObject
    {
        ["Speed"] = 1,
    }, Runtime.Revision).Value;
    Live.Enabled = false;
    Runtime.Tick(0.25f);
    Check(Live.Time == 0 && new AnimationSnapshot(Runtime.Frame).ToJson()["animators"]![0]!["status"]!.GetValue<string>() == "Disabled", "disabled Animator freezes");
    Live.Enabled = true;
    Live.Entity.ActiveSelf = false;
    Runtime.Tick(0.25f);
    Check(Live.Time == 0, "inactive entity freezes Animator");
    Live.Entity.ActiveSelf = true;
    var LiveMesh = Live.Entity.GetComponent<SkeletalMeshComponent>()!;
    LiveMesh.Enabled = false;
    Runtime.Tick(0.25f);
    Check(Live.Time == 0, "disabled mesh freezes dependent Animator");
    LiveMesh.Enabled = true;
    Runtime.Tick(0.125f);
    Check(Near(Live.Time, 0.125), "reenable resumes current playhead");
    using var Reconstructed = new RuntimeWorld(Runtime.Snapshot(), Registry, Assets);
    Check(Reconstructed.Snapshot().Serialize() == Runtime.Snapshot().Serialize(), "live authoritative state reconstructs without hidden playback state");
    Runtime.Tick(0.125f);
    Reconstructed.Tick(0.125f);
    Check(Near(((AnimatorComponent)Reconstructed.FindComponent(Live.ComponentId)!).Time, Live.Time), "reconstructed playback continues identically");
    _ = Runtime.Patch(Live.ComponentId, new JsonObject
    {
        ["Clip"] = "engine://animations/missing",
    }, Runtime.Revision).Value;
    var BeforeMissing = Live.Time;
    Runtime.Tick(0.25f);
    Check(Live.Time == BeforeMissing && new AnimationSnapshot(Runtime.Frame).ToJson()["animators"]![0]!["code"]!.GetValue<string>() == "ANIMATOR_BINDING_INVALID", "missing clip produces explicit observation without advancement");
    _ = Runtime.DestroyEntity(Entity.EntityId).Value;
    Check(new AnimationSnapshot(Runtime.Frame).ToJson()["animators"]!.AsArray().Count == 0, "entity deletion removes animation observations");
}
using (var Runtime = new RuntimeWorld(Data, Registry))
{
    Runtime.Tick(0.25f);
    Check(new AnimationSnapshot(Runtime.Frame).ToJson()["animators"]![0]!["code"]!.GetValue<string>() == "ANIMATOR_ASSETS_UNAVAILABLE", "missing asset service is explicit");
}
var Once = new SkeletalAnimationPlayable("once", Clip, "target", PlayableLoopMode.Once);
Once.Play();
Once.Advance(Clip.Duration + 1);
Check(Once.State == PlayableState.Completed, "generic once completion");
Once.Play();
Check(Once.Time == 0 && Once.State == PlayableState.Playing, "generic replay owns restart semantics");
Once.Stop();
Check(Once.Time == 0 && Once.State == PlayableState.Stopped, "generic stop owns stop semantics");
var Looping = new SkeletalAnimationPlayable("loop", Clip, "target");
Looping.Seek(-0.125);
Check(Near(Looping.Time, Clip.Duration - 0.125), "generic negative loop seek");
Looping.Seek(Clip.Duration * 4 + 0.125);
Check(Near(Looping.Time, 0.125), "generic multi-cycle seek");
var Outputs = new ListPlayableOutputSink();
Looping.Evaluate(new(Looping.Time, 0, 1), Outputs);
Check(Outputs.Outputs.Single().PropertyPath == "skeleton.pose" && Outputs.Outputs.Single().Value!.ToJsonString() == Looping.SamplePose(Looping.Time).ToJson().ToJsonString(), "typed skeletal sampling and Playable output agree");
var Cyclic = new[]
{
    new SkeletonBone("a", "A", "b", BoneTransform.Identity),
    new SkeletonBone("b", "B", "a", BoneTransform.Identity),
};
Reject(() => new SkeletalAnimationAsset("cycle", "Cycle", 1, 30, Cyclic, Array.Empty<BoneTrack>()), "cyclic animation skeleton rejected");
AnimationAssetValidation.Run(Root, Data, Registry, Animator.ComponentId, Check);
var Dynamic = new SceneData();
var DynamicEntity = Dynamic.CreateEntity(Registry, "Dynamic Animator");
var DynamicMesh = Dynamic.AddComponent(Registry, DynamicEntity.EntityId, "engine.skeletal-mesh").Single(Component => Component.TypeId == "engine.skeletal-mesh");
DynamicMesh.State["Mesh"] = "engine://meshes/robot-skeletal";
Dynamic.AddComponent(Registry, DynamicEntity.EntityId, "f1d5280a-b03e-4764-a851-57f160e31f72");
using (var Runtime = new RuntimeWorld(Dynamic, Registry, Assets))
{
    Runtime.Tick(0.25f);
    var DynamicAnimator = Runtime.FindEntity(DynamicEntity.EntityId)!.GetComponent<AnimatorComponent>()!;
    Check(DynamicAnimator.Time == 0, "Animator added during Update waits until the next tick");
    Runtime.Tick(0.25f);
    Check(Near(DynamicAnimator.Time, 0.25), "new Animator advances next tick");
}
RootMotionValidation.Run(Root, Registry, Check);
AnimationPipelineValidation.Run(Assets, Registry, Check);
AnimationStateMachineValidation.Run(Root, Assets, Registry, Check);
Console.WriteLine($"[AnimationValidation] PASS: {Count} assertions; Playable playback, component transactions and skinned frame geometry");

[ComponentTypeId("57cd3ab0-aa35-4ce9-ad6c-71d71d812aef"), ComponentSchemaVersion(1)]
public sealed partial class AnimationPhaseProbe : SharpComponent
{
    public AnimatorComponent? Animator;
    [NonSerialized]
    public double UpdateTime;
    [NonSerialized]
    public double LateTime;

    protected override void OnUpdate(float DeltaSeconds)
    {
        UpdateTime = Animator?.Time ?? -1;
    }

    protected override void OnLateUpdate(float DeltaSeconds)
    {
        LateTime = Animator?.Time ?? -1;
    }
}

[ComponentTypeId("f1d5280a-b03e-4764-a851-57f160e31f72"), ComponentSchemaVersion(1)]
public sealed partial class AnimationSpawner : SharpComponent
{
    protected override void OnUpdate(float DeltaSeconds)
    {
        if (Entity.GetComponent<AnimatorComponent>() is not null)
        {
            return;
        }
        Entity.AddComponent<AnimatorComponent>(new AnimatorComponent.InitialState
        {
            Target = Entity.GetComponent<SkeletalMeshComponent>(),
            Clip = new AssetRef<SkeletalAnimationAsset>("engine://animations/robot-walk"),
        });
    }
}
