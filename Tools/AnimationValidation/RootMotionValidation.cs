using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Mathematics;
using SharpEngine.World;

internal static class RootMotionValidation
{
    internal static void Run(string Root, ComponentRegistry Registry, Action<bool, string> Check)
    {
        var Source = SkeletalAnimationAsset.Parse(File.ReadAllText(Path.Combine(Root,
            "Content/Assets/Animations/RobotWalk.skeletalanimation.json")));
        var Motion = Source.WithRootMotion(new(true, "root"));
        var Tracks = Source.Tracks.Select(Track => Track.BoneId == "root"
            ? new BoneTrack(Track.TrackId, Track.BoneId,
            [
                new(0, BoneTransform.Identity),
                new(1, new(new(0, 0, -2), Quaternion.Identity, Vector3.One)),
            ]) : Track).ToArray();
        Motion = new(Motion.AssetId, Motion.Name, 1, 30, Motion.Bones, Tracks,
            Motion.SkeletonId, null, null, Motion.Notifies, Motion.RootMotion);
        bool Near(Vector3 First, Vector3 Second) { return Vector3.Distance(First, Second) < 0.0001f; }
        Check(Near(RootMotionSampler.Delta(Motion, 0.75, 0.5, true).Translation, new(0, 0, -1)),
            "root motion crosses loop endpoints without resetting entity position");
        Check(Near(RootMotionSampler.Delta(Motion, 0.5, 3.25, true).Translation, new(0, 0, -6.5f)),
            "root motion preserves advances across multiple cycles");
        Check(Near(RootMotionSampler.Delta(Motion, 0.75, 0.5, false).Translation, new(0, 0, -0.5f)),
            "non-loop root motion clamps at the endpoint");
        Check(RootMotionSampler.Delta(Motion, 1, 0.5, false) == Matrix4x4.Identity &&
            RootMotionSampler.Delta(Motion, 0.7, 0, true) == Matrix4x4.Identity,
            "paused and completed root motion do not drift");
        var Disabled = Motion.WithRootMotion(new(false, "root"));
        Check(Near(Disabled.Sample(0.5).Get("root").Position, new(0, 0, -1)) &&
            Disabled.SamplePreview(0.5).Get("root").Position == Vector3.Zero &&
            RootMotionSampler.Delta(Disabled, 0, 0.5, true) == Matrix4x4.Identity,
            "disabled root motion strips output while preserving source tracks");
        Check(SkeletalAnimationAsset.Parse(Motion.Serialize(false)).Serialize(false) == Motion.Serialize(false),
            "root motion v2 is deterministic and round trips");
        var Legacy = Motion.ToJson();
        Legacy["schemaVersion"] = 1;
        Legacy.Remove("rootMotion");
        var Migrated = SkeletalAnimationAsset.Parse(Legacy.ToJsonString());
        Check(!Migrated.RootMotion.Enabled && Migrated.RootMotion.BoneId == "root" &&
            Migrated.ToJson()["schemaVersion"]!.GetValue<int>() == 2,
            "legacy animation migrates explicitly to disabled root motion");
        var Invalid = Motion.ToJson();
        Invalid["rootMotion"]!["boneId"] = "left-foot";
        var Rejected = false;
        try
        {
            SkeletalAnimationAsset.Parse(Invalid.ToJsonString());
        }
        catch (InvalidDataException)
        {
            Rejected = true;
        }
        Check(Rejected, "root motion rejects a child bone as the root");
        foreach (var Field in new[]
        {
            "schemaVersion", "rootMotion",
        })
        {
            var Missing = Motion.ToJson();
            Missing[Field] = null;
            Rejected = false;
            try
            {
                SkeletalAnimationAsset.Parse(Missing.ToJsonString());
            }
            catch (InvalidDataException)
            {
                Rejected = true;
            }
            Check(Rejected, "required animation settings reject null with a data diagnostic: " + Field);
        }

        var RotatedTracks = Tracks.Select(Track => Track.BoneId == "root"
            ? new BoneTrack(Track.TrackId, Track.BoneId,
            [
                new(0, BoneTransform.Identity),
                new(1, new(new(0, 0, -2), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), Vector3.One)),
            ]) : Track).ToArray();
        var Turning = new SkeletalAnimationAsset(Motion.AssetId, Motion.Name, 1, 30,
            Motion.Bones, RotatedTracks, Motion.SkeletonId, null, null, null, Motion.RootMotion);
        var Accumulated = Matrix4x4.Identity;
        for (var Index = 0; Index < 12; Index++)
        {
            Accumulated = RootMotionSampler.Delta(Turning, Index * 0.25 % 1, 0.25, true) * Accumulated;
        }
        var Whole = RootMotionSampler.Delta(Turning, 0, 3, true);
        Check(Near(Accumulated.Translation, Whole.Translation) &&
            Near(Vector3.TransformNormal(Vector3.UnitZ, Accumulated), Vector3.TransformNormal(Vector3.UnitZ, Whole)),
            "translation and rotation compose identically for stepped and multi-loop sampling");

        // Stepped sampling must match a single span that starts mid-cycle and crosses the boundary.
        foreach (var Start in new[] { 0.6, 0.9, 0.75 })
        {
            var Span = Matrix4x4.Identity;
            const double Step = 0.05;
            for (var Index = 0; Index < 16; Index++)
            {
                var From = (Start + Index * Step) % 1;
                Span = RootMotionSampler.Delta(Turning, From, Step, true) * Span;
            }
            var Direct = RootMotionSampler.Delta(Turning, Start, 0.8, true);
            Check(Near(Span.Translation, Direct.Translation) &&
                Near(Vector3.TransformNormal(Vector3.UnitZ, Span),
                    Vector3.TransformNormal(Vector3.UnitZ, Direct)),
                "a loop-crossing span from " + Start.ToString("0.00",
                    System.Globalization.CultureInfo.InvariantCulture) +
                " matches stepped playback: " + Direct.Translation);
        }
        Check(Near(RootMotionSampler.Delta(Motion, 0.9, 0.5, true).Translation, new(0, 0, -1)),
            "a span that wraps once keeps the pre-wrap remainder and the new partial cycle");

        var Fixture = Directory.CreateTempSubdirectory("SharpEngine-RootMotion-");
        try
        {
            File.WriteAllText(Path.Combine(Fixture.FullName, "engine.json"), "{}");
            var Content = Directory.CreateDirectory(Path.Combine(Fixture.FullName, "Content/Assets"));
            var Manifest = new JsonObject
            {
                ["schemaVersion"] = 2,
                ["assets"] = new JsonArray
                {
                    Entry(Motion.AssetId[9..], "SkeletalAnimation", "clip.json"),
                    Entry("meshes/robot-skeletal", "SkeletalMesh", "mesh.json"),
                    Entry("skeletons/robot", "Skeleton", "skeleton.json"),
                },
            };
            File.WriteAllText(Path.Combine(Content.FullName, "assets.json"), Manifest.ToJsonString());
            var ClipPath = Path.Combine(Content.FullName, "clip.json");
            File.WriteAllText(ClipPath, Motion.Serialize(false));
            var Mesh = JsonNode.Parse(File.ReadAllText(Path.Combine(Root,
                "Content/Assets/Meshes/Robot.skeletalmesh.json")))!.AsObject();
            Mesh.Remove("sourcePath");
            Mesh.Remove("sourceSha256");
            File.WriteAllText(Path.Combine(Content.FullName, "mesh.json"), Mesh.ToJsonString());
            File.Copy(Path.Combine(Root, "Content/Assets/Skeletons/Robot.skeleton.json"),
                Path.Combine(Content.FullName, "skeleton.json"));
            var Assets = AssetRegistry.Load(Fixture.FullName);
            var Scene = new SceneData();
            var Parent = Scene.CreateEntity(Registry, "Parent");
            Parent.Components[0].State["Rotation"] = new JsonArray(0, 90, 0);
            var Character = Scene.CreateEntity(Registry, "Character", Parent.EntityId);
            Scene.AddComponent(Registry, Character.EntityId, "engine.movement");
            var Collider = Scene.AddComponent(Registry, Character.EntityId, "engine.capsule-collider")
                .Single(Component => Component.TypeId == "engine.capsule-collider");
            var Model = Scene.CreateEntity(Registry, "Model", Character.EntityId);
            Model.Components[0].State["Position"] = new JsonArray(0, -1, 0);
            var ModelMesh = Scene.AddComponent(Registry, Model.EntityId, "engine.skeletal-mesh")
                .Single(Component => Component.TypeId == "engine.skeletal-mesh");
            ModelMesh.State["Mesh"] = "engine://meshes/robot-skeletal";
            var Animator = Scene.AddComponent(Registry, Character.EntityId, "engine.animator").Single();
            Animator.State["Target"] = ModelMesh.ComponentId;
            Animator.State["Clip"] = Motion.AssetId;
            Animator.State["MovementSource"] = Character.Components.Single(Component => Component.TypeId == "engine.movement").ComponentId;
            using var Runtime = new RuntimeWorld(Scene, Registry, Assets);
            var Live = (AnimatorComponent)Runtime.FindComponent(Animator.ComponentId)!;
            var Transform = Runtime.FindEntity(Character.EntityId)!.GetComponent<TransformComponent>()!;
            Runtime.Tick(0.75f);
            Runtime.Tick(0.5f);
            Check(Near(Transform.Position, new(0, 0, -2.5f)),
                "root motion moves the owning entity continuously under a rotated parent");
            var Hit = Runtime.Collision.Raycast(new(-2.5f, 3, 0), -Vector3.UnitY, 10);
            Check(Hit?.ComponentId == Collider.ComponentId &&
                Runtime.Collision.Raycast(new(0, 3, 0), -Vector3.UnitY, 10) is null,
                "root motion moves capsule queries together with the owning entity and mesh hierarchy");
            var Frame = Runtime.Frame.Get<SkeletonFrameState>(ModelMesh.ComponentId)!.ToJson();
            Check(Frame["localPose"]!["bones"]!.AsArray().Single(Bone =>
                Bone!["boneId"]!.GetValue<string>() == "root")!["position"]!.AsArray()
                .All(Value => Math.Abs(Value!.GetValue<float>()) < 0.0001f),
                "root motion is removed from the committed mesh pose to prevent double movement");
            var BeforeSeek = Transform.Position;
            Runtime.Patch(Live.ComponentId, new JsonObject
            {
                ["Time"] = 0.9,
                ["Playing"] = false,
            }, Runtime.Revision);
            Runtime.Tick(0.2f);
            Check(Transform.Position == BeforeSeek, "seek and paused animation never teleport the entity");
            File.WriteAllText(ClipPath, Disabled.Serialize(false));
            SceneRenderer.InvalidateAssets(Assets);
            Live.Playing = true;
            Runtime.Tick(0.2f);
            Check(Transform.Position == BeforeSeek, "asset reload disabling root motion stops entity drive");
            File.WriteAllText(ClipPath, Motion.Serialize(false));
            SceneRenderer.InvalidateAssets(Assets);
            var Other = Runtime.CreateEntity("Other animator", Character.EntityId).Value;
            Other.AddComponent<AnimatorComponent>(new AnimatorComponent.InitialState
            {
                Target = Live.Target,
                Clip = Live.Clip,
                MovementSource = Live.MovementSource,
            });
            Runtime.Tick(0.2f);
            Check(Transform.Position == BeforeSeek && Runtime.Frame.Get<AnimatorFrameState>(Live.ComponentId)!
                .ToJson()["rootMotion"]!["code"]!.GetValue<string>() == "ROOT_MOTION_CONFLICT",
                "multiple drivers reject root movement deterministically with a published diagnostic");
            Other.GetComponent<AnimatorComponent>()!.Enabled = false;
            var Player = Runtime.FindEntity(Character.EntityId)!.AddComponent<PlayerControllerComponent>(
                new PlayerControllerComponent.InitialState
                {
                    ControlledEntity = Runtime.FindEntity(Character.EntityId),
                    FacingMesh = Live.Target,
                    FaceMoveDirection = false,
                }).Value;
            Live.Target!.Entity.GetComponent<TransformComponent>()!.Rotation = new(0, 180, 0);
            Player.SetMoveInput(new(1, 0));
            var BeforePlayer = Vector3.Transform(Transform.Position, Matrix4x4.CreateRotationY(MathF.PI / 2));
            Runtime.Tick(0.25f);
            var AfterPlayer = Vector3.Transform(Transform.Position, Matrix4x4.CreateRotationY(MathF.PI / 2));
            Check(Near(AfterPlayer - BeforePlayer, new(0.5f, 0, 0)) && Near(Transform.Rotation, Vector3.Zero),
                "Player root motion follows its independent Mesh heading and preserves root facing: " + (AfterPlayer - BeforePlayer) + " rotation=" + Transform.Rotation);
            var VerticalTracks = Motion.Tracks.Select(Track => Track.BoneId == "root"
                ? new BoneTrack(Track.TrackId, Track.BoneId,
                [
                    new(0, BoneTransform.Identity),
                    new(1, new(new(0, 1, -2), Quaternion.Identity, Vector3.One)),
                ]) : Track).ToArray();
            var Vertical = new SkeletalAnimationAsset(Motion.AssetId, Motion.Name, 1, 30,
                Motion.Bones, VerticalTracks, Motion.SkeletonId, null, null, null, Motion.RootMotion);
            File.WriteAllText(ClipPath, Vertical.Serialize(false));
            SceneRenderer.InvalidateAssets(Assets);
            Live.Time = 0;
            Runtime.Tick(0.25f);
            var AfterVertical = Vector3.Transform(Transform.Position, Matrix4x4.CreateRotationY(MathF.PI / 2));
            Check(Near(AfterVertical - AfterPlayer, new(0.5f, 0.25f, 0)),
                "Mesh-facing root locomotion retains source distance and vertical jump displacement");
        }
        finally
        {
            Fixture.Delete(true);
        }
    }

    private static JsonObject Entry(string Id, string Type, string FileName)
    {
        return new JsonObject
        {
            ["id"] = Id,
            ["type"] = Type,
            ["name"] = Id,
            ["path"] = "Assets/" + FileName,
        };
    }
}
