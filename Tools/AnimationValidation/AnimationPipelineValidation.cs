using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.UI;
using SharpEngine.World;

internal static class AnimationPipelineValidation
{
    internal static void Run(AssetRegistry Assets, ComponentRegistry Registry, Action<bool, string> Check)
    {
        var Data = new SceneData();
        var Parent = Data.CreateEntity(Registry, "Controller");
        var Child = Data.CreateEntity(Registry, "Model", Parent.EntityId);
        var Mesh = Data.AddComponent(Registry, Child.EntityId, "engine.skeletal-mesh").Single(Component => Component.TypeId == "engine.skeletal-mesh");
        Mesh.State["Mesh"] = "engine://meshes/robot-skeletal";
        var Animator = Data.AddComponent(Registry, Parent.EntityId, "engine.animator").Single();
        Animator.State["Target"] = Mesh.ComponentId;
        Animator.State["Clip"] = "engine://animations/robot-walk";
        var Camera = Data.CreateEntity(Registry, "Camera");
        Data.AddComponent(Registry, Camera.EntityId, "engine.camera");
        Camera.Components.Single(Component => Component.TypeId == "engine.transform").State["Position"] = new JsonArray(0f, 0.5f, 4f);
        Data.ActiveCameraId = Camera.EntityId;
        var Bounds = new UiRect(0, 0, 800, 500);
        var BindData = Data.Clone();
        BindData.Entities.Single(Entity => Entity.EntityId == Parent.EntityId).Components.Single(Component => Component.TypeId == "engine.animator").Enabled = false;
        using var BindRuntime = new RuntimeWorld(BindData, Registry, Assets);
        var Bind = BindRuntime.Frame.Get<SkeletonFrameState>(Mesh.ComponentId)!;

        using var Runtime = new RuntimeWorld(Data, Registry, Assets);
        var Live = (AnimatorComponent)Runtime.FindComponent(Animator.ComponentId)!;
        var Initial = Runtime.Frame.Get<SkeletonFrameState>(Mesh.ComponentId)!;
        Check(Live.Target?.ComponentId == Mesh.ComponentId && Initial.Positions.Count > 0, "parent Animator binds child skeleton before first frame");
        Runtime.Tick(0.25f);
        var Frozen = Runtime.Frame;
        var Skeleton = Frozen.Get<SkeletonFrameState>(Mesh.ComponentId)!;
        var Json = Skeleton.ToJson();
        Check(Json["modelMatrices"]!.AsArray().Count > 0 && Json["skinMatrices"]!.AsArray().Count == Json["modelMatrices"]!.AsArray().Count &&
            Json["localPose"]!["time"]!.GetValue<double>() == 0.25 && !Initial.Positions.SequenceEqual(Skeleton.Positions), "frame end publishes sampled local pose, matrices and skinned vertices together");
        var World = new WorldDocument(Frozen, Registry);
        var Frame = SceneRenderer.BuildFrame(World, Bounds, Assets);
        var SampleCount = Frozen.Get<AnimatorFrameState>(Live.ComponentId)!.ToJson()["sampleCount"]!.GetValue<ulong>();
        Check(SampleCount == 2, "one initial and one advancing sample; zero-delta publication reuses the pose");
        for (var Index = 0; Index < 3; Index++)
        {
            Check(SceneRenderer.BuildFrame(World, Bounds, Assets).Triangles.SequenceEqual(Frame.Triangles), "repeated draw consumes identical prepared vertices");
            SceneRenderPreparation.Prepare(World, Assets);
            _ = new AnimationSnapshot(Runtime.Frame).ToJson();
        }
        var Triangle = Frame.Triangles.Take(3).ToArray();
        var Picked = SceneRenderer.TryPick(World, Bounds, Triangle.Average(Vertex => Vertex.X), Triangle.Average(Vertex => Vertex.Y), out var Hit, Assets);
        Check(Picked && Hit!.Id == Child.EntityId, "picking intersects the same prepared skeletal geometry");
        Check(Runtime.Revision == Frozen.Revision && Runtime.Frame.Get<AnimatorFrameState>(Live.ComponentId)!.ToJson()["sampleCount"]!.GetValue<ulong>() == SampleCount,
            "draw, pick, preview preparation and observation do not evaluate runtime animation or advance revision");
        var FrozenJson = Frozen.ToJson().ToJsonString();
        var FrozenScene = Frozen.Snapshot().Serialize();
        Json["code"] = "tampered";
        Runtime.Tick(0.125f);
        Check(Frozen.ToJson().ToJsonString() == FrozenJson && Frozen.Snapshot().Serialize() == FrozenScene &&
            SceneRenderer.BuildFrame(World, Bounds, Assets).Triangles.SequenceEqual(Frame.Triangles), "old committed frames and their render output remain frozen");
        Check(!Runtime.Snapshot().Serialize().Contains("modelMatrices", StringComparison.Ordinal) && !Runtime.Snapshot().Serialize().Contains("localPose", StringComparison.Ordinal),
            "derived skeleton state never enters authoritative scene serialization");

        var RawWorld = new WorldDocument(Frozen.Snapshot(), Registry);
        var RawFrame = SceneRenderer.BuildFrame(RawWorld, Bounds, Assets);
        Check(!RawFrame.Triangles.SequenceEqual(Frame.Triangles), "renderer alone uses bind geometry and never samples serialized Animator time");
        SceneRenderPreparation.Prepare(RawWorld, Assets);
        Check(SceneRenderer.BuildFrame(RawWorld, Bounds, Assets).Triangles.SequenceEqual(Frame.Triangles), "explicit authoring preparation produces the equivalent sampled preview");
        var InvalidPreview = Frozen.Snapshot();
        InvalidPreview.Entities.Single(Entity => Entity.EntityId == Parent.EntityId).Components.Single(Component => Component.TypeId == "engine.animator").State["Clip"] = "engine://animations/missing";
        var InvalidWorld = new WorldDocument(InvalidPreview, Registry);
        SceneRenderPreparation.Prepare(InvalidWorld, Assets);
        var InvalidFrame = SceneRenderer.BuildFrame(InvalidWorld, Bounds, Assets);
        Check(InvalidFrame.Triangles.SequenceEqual(RawFrame.Triangles) && InvalidFrame.Diagnostics.Any(Diagnostic => Diagnostic.Contains("SKELETON_PREPARATION_FAILED", StringComparison.Ordinal)),
            "invalid authoring clip reports a preparation error while displaying bind geometry");
        var InvalidLegacy = BindData.Clone();
        InvalidLegacy.Entities.Single(Entity => Entity.EntityId == Child.EntityId).Components.Single(Component => Component.TypeId == "engine.skeletal-mesh").State["Animation"] = "engine://animations/missing";
        using (var InvalidRuntime = new RuntimeWorld(InvalidLegacy, Registry, Assets))
        {
            Check(InvalidRuntime.Frame.Get<SkeletonFrameState>(Mesh.ComponentId)!.Code == "SKELETON_PREPARATION_FAILED", "invalid legacy clip is observable instead of reporting a ready skeleton");
        }

        _ = Runtime.Patch(Live.ComponentId, new JsonObject
        {
            ["Time"] = 0.6,
            ["Playing"] = false,
        }, Runtime.Revision).Value;
        Check(Runtime.Frame.Get<SkeletonFrameState>(Mesh.ComponentId)!.ToJson()["localPose"]!["time"]!.GetValue<double>() == 0.6, "seek patch prepares its pose before returning without a Tick");

        var Other = Runtime.CreateEntity("Other", Parent.EntityId).Value;
        var OtherMesh = Other.AddComponent<SkeletalMeshComponent>(new SkeletalMeshComponent.InitialState
        {
            Mesh = new AssetRef<SkeletalMeshAsset>("engine://meshes/robot-skeletal"),
        }).Value;
        _ = Runtime.Patch(Live.ComponentId, new JsonObject
        {
            ["Target"] = OtherMesh.ComponentId,
        }, Runtime.Revision).Value;
        Check(Runtime.Frame.Get<SkeletonFrameState>(Mesh.ComponentId)!.Positions.SequenceEqual(Bind.Positions) &&
            !Runtime.Frame.Get<SkeletonFrameState>(OtherMesh.ComponentId)!.Positions.SequenceEqual(Bind.Positions), "retargeting releases old skeleton and samples into the new target");
        Check(Runtime.Frame.Get<AnimatorFrameState>(Live.ComponentId)!.ToJson()["targetComponentId"]!.GetValue<string>() == OtherMesh.ComponentId, "committed observation follows the explicit target");
        Live.Enabled = false;
        Check(Runtime.Frame.Get<SkeletonFrameState>(OtherMesh.ComponentId)!.Positions.SequenceEqual(Bind.Positions), "disabling Animator releases its pose input immediately");
        Live.Enabled = true;
        Check(!Runtime.Frame.Get<SkeletonFrameState>(OtherMesh.ComponentId)!.Positions.SequenceEqual(Bind.Positions), "reenabling Animator restores its sampled input");

        var SecondController = Runtime.CreateEntity("Second controller").Value;
        var Second = SecondController.AddComponent<AnimatorComponent>(new AnimatorComponent.InitialState
        {
            Target = OtherMesh,
            Clip = new AssetRef<SkeletalAnimationAsset>("engine://animations/robot-walk"),
            Time = 0.25,
        }).Value;
        Check(Runtime.Frame.Get<SkeletonFrameState>(OtherMesh.ComponentId)!.ToJson()["code"]!.GetValue<string>() == "SKELETON_POSE_CONFLICT" &&
            Runtime.Frame.Get<SkeletonFrameState>(OtherMesh.ComponentId)!.Positions.SequenceEqual(Bind.Positions), "multiple sources select deterministic bind pose with an explicit conflict");
        _ = SecondController.RemoveComponent(Second).Value;
        Check(Runtime.Frame.Get<SkeletonFrameState>(OtherMesh.ComponentId)!.ToJson()["code"]!.GetValue<string>() == "SKELETON_READY", "source removal releases conflict without a stale pose input");
        _ = Runtime.DestroyEntity(Other.EntityId).Value;
        Check(Live.Target is null && Runtime.Frame.Get<SkeletonFrameState>(OtherMesh.ComponentId) is null &&
            Runtime.Frame.Get<AnimatorFrameState>(Live.ComponentId)!.ToJson()["code"]!.GetValue<string>() == "ANIMATOR_TARGET_MISSING", "target deletion atomically nullifies reference and removes render state");

        var Prefab = Data.ExportPrefab(Registry, Parent.EntityId);
        var Destination = new SceneData();
        var Copies = Destination.Instantiate(Prefab, Registry);
        var CopyAnimator = Copies.SelectMany(Entity => Entity.Components).Single(Component => Component.TypeId == "engine.animator");
        var CopyMesh = Copies.SelectMany(Entity => Entity.Components).Single(Component => Component.TypeId == "engine.skeletal-mesh");
        Check(CopyAnimator.State["Target"]!.GetValue<string>() == CopyMesh.ComponentId && CopyMesh.ComponentId != Mesh.ComponentId, "prefab instantiation remaps child target by stable component identity");
        using (var Reconstructed = new RuntimeWorld(Destination, Registry, Assets))
        {
            Check(((AnimatorComponent)Reconstructed.FindComponent(CopyAnimator.ComponentId)!).Target?.ComponentId == CopyMesh.ComponentId, "prefab reconstruction resolves the remapped live target");
        }

        var Legacy = Data.Clone();
        var LegacyAnimator = Legacy.Entities.SelectMany(Entity => Entity.Components).Single(Component => Component.TypeId == "engine.animator");
        var LegacyMesh = Legacy.Entities.Single(Entity => Entity.EntityId == Child.EntityId).Components.Single(Component => Component.TypeId == "engine.skeletal-mesh");
        // Version 1 could only target a mesh on its own entity.
        Legacy.Entities.Single(Entity => Entity.EntityId == Parent.EntityId).Components.Remove(LegacyAnimator);
        Legacy.Entities.Single(Entity => Entity.EntityId == Child.EntityId).Components.Add(LegacyAnimator);
        LegacyAnimator.SchemaVersion = 1;
        LegacyAnimator.State.Remove("Target");
        var LegacyJson = Legacy.Serialize();
        var Migrated = SceneData.Parse(LegacyJson, Registry);
        var MigratedAnimator = Migrated.Entities.SelectMany(Entity => Entity.Components).Single(Component => Component.TypeId == "engine.animator");
        Check(MigratedAnimator.SchemaVersion == 2 && MigratedAnimator.State["Target"]!.GetValue<string>() == LegacyMesh.ComponentId && Legacy.Serialize() == LegacyJson,
            "v1 parse migration records the original same-entity target and preserves input data");
        using (var MigratedRuntime = new RuntimeWorld(Legacy, Registry, Assets))
        {
            Check(((AnimatorComponent)MigratedRuntime.FindComponent(LegacyAnimator.ComponentId)!).Target?.ComponentId == LegacyMesh.ComponentId && LegacyAnimator.SchemaVersion == 1,
                "runtime reconstruction migrates a clone without mutating its source");
        }
        LegacyAnimator.State["Unknown"] = 1;
        var Rejected = false;
        try
        {
            SceneData.Parse(Legacy.Serialize(), Registry);
        }
        catch (ComponentValidationException Error)
        {
            Rejected = Error.Code == "ANIMATOR_MIGRATION";
        }
        Check(Rejected, "malformed legacy Animator fails migration with a structured diagnostic");

        var LateData = Data.Clone();
        var Late = LateData.AddComponent(Registry, Parent.EntityId, "95a487df-931e-4634-973f-d2260078ce99").Single();
        Late.State["Animator"] = Animator.ComponentId;
        using var LateRuntime = new RuntimeWorld(LateData, Registry, Assets);
        LateRuntime.Tick(0.25f);
        Check(LateRuntime.Frame.Get<SkeletonFrameState>(Mesh.ComponentId)!.ToJson()["localPose"]!["time"]!.GetValue<double>() == 0.6 &&
            ((AnimatorComponent)LateRuntime.FindComponent(Animator.ComponentId)!).Time == 0.6, "final publication reconciles LateUpdate seek with skeleton output");
        Check(Runtime.Diagnostics.Count == 0 && LateRuntime.Diagnostics.Count == 0, "pipeline lifecycle completes without callback exceptions");
    }
}

[ComponentTypeId("95a487df-931e-4634-973f-d2260078ce99"), ComponentSchemaVersion(1)]
public sealed partial class LateAnimationSeek : SharpComponent
{
    public AnimatorComponent? Animator;
    protected override void OnLateUpdate(float DeltaSeconds)
    {
        Animator!.Time = 0.6;
    }
}
