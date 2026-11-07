using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.World;

internal static class AnimationBlendValidation
{
    internal static void Run(string Root, ComponentRegistry Registry, Action<bool, string> Check)
    {
        var Bones = new SkeletonBone[]
        {
            new("root", "Root", null, BoneTransform.Identity),
            new("child", "Child", "root", BoneTransform.Identity),
        };
        SkeletalAnimationAsset Clip(string Id, float Offset, float RootSpeed, bool Motion = false)
        {
            return new SkeletalAnimationAsset("engine://animations/" + Id, Id, 2, 30, Bones,
            [
                new("root", "root",
                [
                    new(0, BoneTransform.Identity),
                    new(2, BoneTransform.Identity with
                    {
                        Position = new Vector3(RootSpeed * 2, 0, 0),
                    }),
                ]),
                new("child", "child",
                [
                    new(0, new(new(Offset, 0, 0),
                        Quaternion.CreateFromAxisAngle(Vector3.UnitY, Offset * MathF.PI / 20), Vector3.One * (1 + Offset / 10))),
                ]),
            ], null, null, null, null, new(Motion, "root"));
        }
        var From = Clip("from", 0, 1, true);
        var To = Clip("to", 10, 3, true);
        var Clips = new Dictionary<string, SkeletalAnimationAsset>
        {
            [From.AssetId] = From,
            [To.AssetId] = To,
        };
        JsonObject Graph(double Duration = 1, string Curve = "Linear")
        {
            return new JsonObject
            {
                ["schemaVersion"] = 2,
                ["kind"] = "animation-state-machine",
                ["assetId"] = "engine://animation-state-machines/blend",
                ["name"] = "Blend Validation",
                ["initialStateId"] = "A",
                ["parameters"] = new JsonArray(new JsonObject
                {
                    ["parameterId"] = "Go",
                    ["defaultValue"] = 0,
                }),
                ["states"] = new JsonArray(State("A", From.AssetId), State("B", To.AssetId)),
                ["transitions"] = new JsonArray(new JsonObject
                {
                    ["transitionId"] = "A-B",
                    ["fromStateId"] = "A",
                    ["toStateId"] = "B",
                    ["exitTime"] = null,
                    ["blendDuration"] = Duration,
                    ["blendCurve"] = Curve,
                    ["conditions"] = new JsonArray(new JsonObject
                    {
                        ["parameterId"] = "Go",
                        ["comparison"] = "Greater",
                        ["value"] = 0,
                    }),
                }),
            };
        }
        var Asset = AnimationStateMachineAsset.Parse(Graph().ToJsonString());
        var Values = new Dictionary<string, double>
        {
            ["Go"] = 0,
        };
        var Player = new AnimationStateMachinePlayer(Asset, Clips, Bones);
        var Independent = new AnimationStateMachinePlayer(Asset, Clips, Bones);
        Player.Advance(0.25, Values);
        Check(Player.StateId == "A" && Player.Time == 0.25, "unmatched conditions retain the advancing source state");
        Values["Go"] = 1;
        Player.Advance(0.25, Values);
        Check(Player.StateId == "B" && Player.IsBlending && Player.Sample().Get("child").Position.X == 0 &&
            Player.Describe()["sourceTime"]!.GetValue<double>() == 0.5,
            "an automatic transition starts continuously at the advanced source pose");
        Player.Advance(0.5, Values);
        var Half = Player.Sample().Get("child");
        Check(Math.Abs(Half.Position.X - 5) < 1e-5 && Math.Abs(Half.Scale.X - 1.5) < 1e-5 &&
            Math.Abs(Quaternion.Dot(Half.Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4))) > 0.9999,
            "halfway blend interpolates position, scale and shortest-arc quaternion rotation");
        Check(Math.Abs(Player.RootDelta.Translation.X - 0.75) < 1e-5 &&
            Player.Sample().Get("root").Position == Vector3.Zero && Player.Sample(true).Get("root").Position.X > 0,
            "root deltas mix by interval weight while Runtime strips root and preview retains enabled trajectories");
        var Paused = Player.Describe().ToJsonString();
        Player.Advance(0, Values);
        Check(Player.Describe().ToJsonString() == Paused && Player.RootDelta == Matrix4x4.Identity,
            "zero advance freezes transition clocks and emits no root movement");
        Player.Advance(0.75, Values);
        Check(!Player.IsBlending && Player.Time == 1.25 && Player.Sample().Get("child").Position.X == 10 &&
            Math.Abs(Player.RootDelta.Translation.X - 2) < 1e-5,
            "overshooting a blend finishes at the target and preserves post-blend root movement");
        Check(Independent.StateId == "A" && Independent.Time == 0 && !Independent.IsBlending,
            "two users of the same immutable graph retain independent clocks and transitions");
        var Smooth = new AnimationStateMachinePlayer(AnimationStateMachineAsset.Parse(Graph(1, "SmoothStep").ToJsonString()), Clips, Bones);
        Smooth.TriggerTransition("A-B");
        Smooth.Advance(0.25, Values);
        Check(Math.Abs(Smooth.Sample().Get("child").Position.X - 1.5625) < 1e-5,
            "SmoothStep weights follow the declared curve");
        Smooth.Seek("A", 3.5);
        Check(!Smooth.IsBlending && Smooth.Time == 1.5 && Smooth.TransitionId is null &&
            Smooth.RootDelta == Matrix4x4.Identity, "seek wraps and cancels a blend without root displacement");
        Smooth.RequestState("B");
        Check(Smooth.IsBlending, "explicit state switching uses the declared outgoing blend");
        var Instant = new AnimationStateMachinePlayer(AnimationStateMachineAsset.Parse(Graph(0).ToJsonString()), Clips, Bones);
        Instant.Advance(0.1, Values);
        Check(Instant.StateId == "B" && !Instant.IsBlending && Instant.Sample().Get("child").Position.X == 10,
            "zero-duration transitions preserve instant switching");
        var Legacy = Graph();
        Legacy["schemaVersion"] = 1;
        Legacy["transitions"]![0]!.AsObject().Remove("blendDuration");
        Legacy["transitions"]![0]!.AsObject().Remove("blendCurve");
        var Migrated = AnimationStateMachineAsset.Parse(Legacy.ToJsonString());
        Check(Migrated.Transitions[0].BlendDuration == 0 && Migrated.Transitions[0].BlendCurve == "Linear" &&
            Migrated.ToJson()["schemaVersion"]!.GetValue<int>() == 2,
            "v1 migrates explicitly to v2 with zero-duration Linear transitions");
        Check(AnimationStateMachineAsset.Parse(Asset.Serialize(false)).Serialize(false) == Asset.Serialize(false),
            "all blend settings serialize deterministically");
        foreach (var Invalid in new Action<JsonObject>[]
        {
            Value => Value["schemaVersion"] = 3,
            Value => Value["transitions"]![0]!["blendDuration"] = -1,
            Value => Value["transitions"]![0]!["blendDuration"] = null,
            Value => Value["transitions"]![0]!["blendCurve"] = "Unknown",
            Value => Value["transitions"]![0]!["unknown"] = 1,
            Value => Value["transitions"]![0]!.AsObject().Remove("blendCurve"),
        })
        {
            var Candidate = Graph();
            Invalid(Candidate);
            var Rejected = false;
            try
            {
                _ = AnimationStateMachineAsset.Parse(Candidate.ToJsonString());
            }
            catch (InvalidDataException)
            {
                Rejected = true;
            }
            Check(Rejected, "invalid blend contracts reject without silently defaulting");
        }
        var ExitGraph = Graph();
        ExitGraph["transitions"]![0]!["exitTime"] = 0.8;
        var Exit = new AnimationStateMachinePlayer(AnimationStateMachineAsset.Parse(ExitGraph.ToJsonString()), Clips, Bones);
        Exit.Advance(1.5, Values);
        Check(Exit.StateId == "A", "exit time gates a satisfied condition");
        Exit.Advance(0.2, Values);
        Check(Exit.IsBlending, "exit time starts a blend once its normalized threshold is reached");
        var Partial = new Dictionary<string, SkeletalAnimationAsset>(Clips)
        {
            [To.AssetId] = new(To.AssetId, "Partial", 2, 30, Bones.Take(1).ToArray(), [], null),
        };
        var PartialPlayer = new AnimationStateMachinePlayer(Asset, Partial, Bones);
        PartialPlayer.RequestState("B");
        PartialPlayer.Advance(0.5, Values);
        Check(PartialPlayer.Sample().Bones.Count == Bones.Length, "partial clips blend against the mesh bind pose");
        RunRuntime(Root, Registry, Check);
    }

    private static JsonObject State(string Id, string ClipId)
    {
        return new JsonObject
        {
            ["stateId"] = Id,
            ["clipId"] = ClipId,
            ["loop"] = true,
            ["speed"] = 1,
        };
    }

    private static void RunRuntime(string Root, ComponentRegistry Registry, Action<bool, string> Check)
    {
        var Fixture = Directory.CreateTempSubdirectory("SharpEngine-Blend-");
        try
        {
            File.WriteAllText(Path.Combine(Fixture.FullName, "engine.json"), "{}");
            var Content = Directory.CreateDirectory(Path.Combine(Fixture.FullName, "Content/Assets"));
            var Original = SkeletalAnimationContent.Load(AssetRegistry.Load(Root), "engine://animations/robot-walk");
            var Source = Original.WithRootMotion(new(false, "root"));
            var Target = new SkeletalAnimationAsset("engine://animations/target", "Target", Source.Duration, Source.SampleRate,
                Source.Bones, Source.Tracks.Select(Track => new BoneTrack(Track.TrackId, Track.BoneId,
                    Track.Keyframes.Select(Key => Key with
                    {
                        Transform = Key.Transform with
                        {
                            Position = Key.Transform.Position + (Track.BoneId == "root" ? Vector3.Zero : Vector3.UnitX),
                        },
                    }).ToArray())).ToArray(), Source.SkeletonId);
            File.WriteAllText(Path.Combine(Content.FullName, "from.json"), Source.Serialize());
            File.WriteAllText(Path.Combine(Content.FullName, "to.json"), Target.Serialize());
            File.Copy(Path.Combine(Root, "Content/Assets/Meshes/Robot.skeletalmesh.json"), Path.Combine(Content.FullName, "mesh.json"));
            File.Copy(Path.Combine(Root, "Content/Assets/Skeletons/Robot.skeleton.json"), Path.Combine(Content.FullName, "skeleton.json"));
            var Controller = new JsonObject
            {
                ["schemaVersion"] = 2,
                ["kind"] = "animation-state-machine",
                ["assetId"] = "engine://animation-state-machines/blend",
                ["name"] = "Runtime Blend",
                ["initialStateId"] = "A",
                ["parameters"] = new JsonArray(),
                ["states"] = new JsonArray(State("A", Source.AssetId), State("B", Target.AssetId)),
                ["transitions"] = new JsonArray(new JsonObject
                {
                    ["transitionId"] = "blend",
                    ["fromStateId"] = "A",
                    ["toStateId"] = "B",
                    ["exitTime"] = null,
                    ["conditions"] = new JsonArray(),
                    ["blendDuration"] = 1,
                    ["blendCurve"] = "Linear",
                }),
            };
            var ControllerPath = Path.Combine(Content.FullName, "controller.sanimationstatemachine");
            File.WriteAllText(ControllerPath, Controller.ToJsonString());
            File.WriteAllText(Path.Combine(Content.FullName, "assets.json"), new JsonObject
            {
                ["schemaVersion"] = 2,
                ["assets"] = new JsonArray(
                    Entry(Source.AssetId[9..], "SkeletalAnimation", "from.json"),
                    Entry(Target.AssetId[9..], "SkeletalAnimation", "to.json"),
                    Entry("meshes/robot-skeletal", "SkeletalMesh", "mesh.json"),
                    Entry("skeletons/robot", "Skeleton", "skeleton.json"),
                    Entry("animation-state-machines/blend", "AnimationStateMachine", "controller.sanimationstatemachine")),
            }.ToJsonString());
            var Assets = AssetRegistry.Load(Fixture.FullName);
            var Data = new SceneData();
            var Entity = Data.CreateEntity(Registry, "Blend");
            var Mesh = Data.AddComponent(Registry, Entity.EntityId, "engine.skeletal-mesh", new JsonObject
            {
                ["Mesh"] = "engine://meshes/robot-skeletal",
            }).Single();
            var Record = Data.AddComponent(Registry, Entity.EntityId, "engine.animator", new JsonObject
            {
                ["Controller"] = "engine://animation-state-machines/blend",
                ["Target"] = Mesh.ComponentId,
            }).Single();
            using var Runtime = new RuntimeWorld(Data, Registry, Assets);
            var Animator = (AnimatorComponent)Runtime.FindComponent(Record.ComponentId)!;
            Runtime.Tick(0.25f);
            Runtime.Tick(0.5f);
            var Frame = Runtime.Frame.Get<AnimatorFrameState>(Record.ComponentId)!.ToJson();
            Check(Frame["schemaVersion"]!.GetValue<int>() == 5 &&
                Frame["controllerPlayback"]!["blendWeight"]!.GetValue<double>() == 0.5 &&
                Frame["stateId"]!.GetValue<string>() == "B", "Runtime publishes the committed transition progress in frame schema v5");
            var MeshFrame = Runtime.Frame.Get<SkeletonFrameState>(Mesh.ComponentId)!.ToJson();
            var Child = Source.Bones.First(Bone => Bone.ParentBoneId is not null).BoneId;
            var Actual = MeshFrame["localPose"]!["bones"]!.AsArray().Single(Bone => Bone!["boneId"]!.GetValue<string>() == Child)!;
            var Expected = (Source.SampleLooped(0.75).Get(Child).Position + Target.SampleLooped(0.5).Get(Child).Position) * 0.5f;
            Check(Math.Abs(Actual["position"]![0]!.GetValue<float>() - Expected.X) < 1e-5,
                "the final Runtime mesh receives the blended pose rather than a target-only sample");
            Animator.Playing = false;
            Runtime.Tick(0.25f);
            Check(Runtime.Frame.Get<AnimatorFrameState>(Record.ComponentId)!.ToJson()["controllerPlayback"]!["blendWeight"]!.GetValue<double>() == 0.5,
                "Runtime pause freezes blend and both animation clocks");
            Animator.Time = 0.1;
            Runtime.Tick(0);
            Check(!Runtime.Frame.Get<AnimatorFrameState>(Record.ComponentId)!.ToJson()["controllerPlayback"]!["blending"]!.GetValue<bool>(),
                "an explicit Runtime seek cancels the transition");
            File.WriteAllText(ControllerPath, "{}");
            SceneRenderer.InvalidateAssets(Assets);
            Runtime.Tick(0.25f);
            Check(Runtime.Frame.Get<AnimatorFrameState>(Record.ComponentId)!.ToJson()["code"]!.GetValue<string>() == "ANIMATOR_BINDING_INVALID",
                "invalid controller reload reports failure while retaining the previous committed pose");
            File.WriteAllText(ControllerPath, Controller.ToJsonString());
            SceneRenderer.InvalidateAssets(Assets);
            Runtime.Tick(0.25f);
            Check(Runtime.Frame.Get<AnimatorFrameState>(Record.ComponentId)!.ToJson()["code"]!.GetValue<string>() == "ANIMATOR_READY",
                "a repaired controller stages and resumes successfully");
            SkeletalAnimationAsset Moving(SkeletalAnimationAsset Animation, float Velocity, bool Enabled)
            {
                var RootBone = Animation.Bones.Single(Bone => Bone.BoneId == "root");
                var Tracks = Animation.Tracks.Where(Track => Track.BoneId != "root").Append(new BoneTrack("root-motion", "root",
                [
                    new(0, RootBone.BindTransform),
                    new(Animation.Duration, RootBone.BindTransform with
                    {
                        Position = RootBone.BindTransform.Position + Vector3.UnitX * Velocity * (float)Animation.Duration,
                    }),
                ])).ToArray();
                return new SkeletalAnimationAsset(Animation.AssetId, Animation.Name, Animation.Duration, Animation.SampleRate,
                    Animation.Bones, Tracks, Animation.SkeletonId, null, null, null, new(Enabled, "root"));
            }
            void ReloadMotion(bool TargetEnabled)
            {
                File.WriteAllText(Path.Combine(Content.FullName, "from.json"), Moving(Source, 1, true).Serialize());
                File.WriteAllText(Path.Combine(Content.FullName, "to.json"), Moving(Target, 3, TargetEnabled).Serialize());
                SceneRenderer.InvalidateAssets(Assets);
                Animator.StateId = "A";
                Animator.Time = 0;
                Animator.Playing = true;
                Runtime.Tick(0);
            }
            var Transform = Animator.Entity.GetComponent<TransformComponent>()!;
            var BeforeMotion = Transform.Position.X;
            ReloadMotion(true);
            Runtime.Tick(0.25f);
            Runtime.Tick(0.5f);
            Check(Math.Abs(Transform.Position.X - BeforeMotion - 1) < 1e-4 &&
                Runtime.Frame.Get<AnimatorFrameState>(Record.ComponentId)!.ToJson()["rootMotion"]!["applied"]!.GetValue<bool>(),
                "Runtime applies mixed root movement once to the character while blending both clips");
            var RootPose = Runtime.Frame.Get<SkeletonFrameState>(Mesh.ComponentId)!.ToJson()["localPose"]!["bones"]!.AsArray()
                .Single(Bone => Bone!["boneId"]!.GetValue<string>() == "root")!;
            Check(Math.Abs(RootPose["position"]![0]!.GetValue<float>()) < 1e-5,
                "the rendered blended skeleton does not apply entity root motion a second time");
            BeforeMotion = Transform.Position.X;
            ReloadMotion(false);
            Runtime.Tick(0.25f);
            Runtime.Tick(0.5f);
            Runtime.Tick(0.5f);
            Check(Math.Abs(Transform.Position.X - BeforeMotion - 0.75) < 1e-4,
                "the final fade-out interval retains source root motion when the destination disables it");
            var AfterFade = Transform.Position.X;
            Runtime.Tick(0.25f);
            Check(Math.Abs(Transform.Position.X - AfterFade) < 1e-5,
                "source root motion stops completely after the blend ends");
            AnimationStateMachineContent.ValidateDependencies(Assets, "engine://animation-state-machines/blend", "engine://meshes/robot-skeletal");
            Check(true, "the v2 controller resolves every clip through the Runtime/Cook dependency contract");
        }
        finally
        {
            Fixture.Delete(true);
        }
    }

    private static JsonObject Entry(string Id, string Type, string Name)
    {
        return new JsonObject
        {
            ["id"] = Id,
            ["type"] = Type,
            ["path"] = "Assets/" + Name,
        };
    }
}
