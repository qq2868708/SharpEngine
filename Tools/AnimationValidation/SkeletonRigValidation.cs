using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Mathematics;
using SharpEngine.World;

internal static class SkeletonRigValidation
{
    internal static void Run(string Root, ComponentRegistry Registry, Action<bool, string> Check)
    {
        var Bones = new[]
        {
            new SkeletonBone("root", "Root", null, new BoneTransform(new Vector3(1, 2, 3), QuaternionMath.Euler(new Vector3(0, 35, 0)), new Vector3(2, 1, 0.5f))),
            new SkeletonBone("hand", "Hand", "root", new BoneTransform(new Vector3(0, 1, 0), QuaternionMath.Euler(new Vector3(10, 0, 20)), Vector3.One)),
        };
        var Sockets = new[]
        {
            new SkeletonSocket("weapon", "Weapon", "aim", new BoneTransform(new Vector3(0.2f, 0.1f, -0.3f), QuaternionMath.Euler(new Vector3(10, 20, 30)), new Vector3(1, 2, 3))),
        };
        var Virtual = new[]
        {
            new SkeletonVirtualBone("follow", "Follow", "aim", "root"),
            new SkeletonVirtualBone("aim", "Aim", "root", "hand"),
        };
        var Asset = new SkeletonAsset("engine://skeletons/rig", "Rig", Bones, Sockets: Sockets, VirtualBones: Virtual);
        var Json = Asset.Serialize(false);
        Check(SkeletonAsset.Parse(Json).Serialize(false) == Json, "Skeleton v3 preserves all rig fields deterministically");
        Check(Asset.Sockets[0] == Sockets[0] && Asset.VirtualBones[1] == Virtual[1], "Non-default authored definitions round-trip by stable identity");
        var Empty = new SkeletonAsset("empty", "Empty", Bones).ToJson();
        Check(Empty["sockets"]!.AsArray().Count == 0 && Empty["virtualBones"]!.AsArray().Count == 0 &&
            Empty.ContainsKey("previewMeshId"), "Default rig fields serialize explicitly");
        foreach (var Version in new[]
        {
            1, 2,
        })
        {
            var Legacy = Empty.DeepClone().AsObject();
            Legacy["schemaVersion"] = Version;
            Legacy.Remove("sockets");
            Legacy.Remove("virtualBones");
            if (Version == 1)
            {
                Legacy.Remove("previewMeshId");
            }
            var Loaded = SkeletonAsset.Parse(Legacy.ToJsonString());
            Check(Loaded.Sockets.Count == 0 && Loaded.VirtualBones.Count == 0 &&
                Loaded.ToJson()["schemaVersion"]!.GetValue<int>() == 3,
                "Skeleton legacy migration produces explicit empty definitions: v" + Version);
        }
        void Reject(Action<JsonObject> Mutation, string Message)
        {
            var Candidate = Asset.ToJson();
            Mutation(Candidate);
            var Rejected = false;
            try
            {
                SkeletonAsset.Parse(Candidate.ToJsonString());
            }
            catch (InvalidDataException)
            {
                Rejected = true;
            }
            Check(Rejected, Message);
        }
        Reject(Value => Value["schemaVersion"] = 4, "Future Skeleton schema is rejected");
        Reject(Value => Value.Remove("sockets"), "Current schema requires sockets even when empty");
        Reject(Value => Value["sockets"] = null, "Null Socket arrays are rejected with an input diagnostic");
        Reject(Value => Value["virtualBones"] = null, "Null virtual bone arrays are rejected with an input diagnostic");
        Reject(Value => Value["sockets"]![0]!["extra"] = true, "Unknown Socket fields are rejected");
        Reject(Value => Value["virtualBones"]![0]!["extra"] = true, "Unknown virtual bone fields are rejected");
        Reject(Value => Value["sockets"]![0]!["socketId"] = "root", "Socket IDs cannot shadow imported bone IDs");
        Reject(Value => Value["virtualBones"]![0]!["boneId"] = "aim", "Virtual bone IDs must be unique");
        Reject(Value => Value["virtualBones"]![0]!["name"] = "Aim", "Authored virtual bone names must be unique");
        Reject(Value => Value["virtualBones"]![0]!["sourceBoneId"] = "missing", "Virtual source cannot dangle");
        Reject(Value => Value["virtualBones"]![1]!["targetBoneId"] = "follow", "Cross virtual dependency cycles are rejected");
        Reject(Value => Value["sockets"]![0]!["parentBoneId"] = "weapon", "Sockets cannot parent to other sockets or themselves");
        Reject(Value => Value["sockets"]![0]!["rotation"] = new JsonArray(0, 0, 0, 0), "Zero Socket rotation is rejected");
        Reject(Value => Value["sockets"]![0]!["scale"] = new JsonArray(1, 0, 1), "Singular Socket scale is rejected");
        Reject(Value => Value["sockets"]!.AsArray().Add(Value["sockets"]![0]!.DeepClone()), "Duplicate sockets are rejected");

        var Animated = new SkeletalPose(1, new[]
        {
            ("root", new BoneTransform(new Vector3(5, 3, 1), QuaternionMath.Euler(new Vector3(20, 60, 10)), new Vector3(2, 1, 0.5f))),
            ("hand", new BoneTransform(new Vector3(1, 2, 0), QuaternionMath.Euler(new Vector3(30, 20, 10)), Vector3.One)),
        });
        var Bind = new SkeletonRigPose(Asset);
        var Pose = new SkeletonRigPose(Asset, Animated);
        var ExpectedHand = TransformMath.Trs(Animated.Get("hand").Position, Animated.Get("hand").Rotation, Animated.Get("hand").Scale) *
            TransformMath.Trs(Animated.Get("root").Position, Animated.Get("root").Rotation, Animated.Get("root").Scale);
        Check(Near(ExpectedHand, Pose.BoneModelMatrices["hand"]), "Real bone hierarchy uses the complete sampled local transforms");
        var Local = Sockets[0].LocalTransform;
        var Expected = TransformMath.Trs(Local.Position, Local.Rotation, Local.Scale) * Pose.BoneModelMatrices["hand"];
        Check(Near(Expected, Pose.SocketModelMatrices["weapon"]), "Socket model matrix composes local TRS after animated bone, including nonuniform scale and rotation");
        Check(Near(Pose.VirtualBoneLocalMatrices["aim"] * Pose.BoneModelMatrices["root"], Pose.BoneModelMatrices["hand"]),
            "Virtual local frame resolves target relative to source without losing affine shear");
        Check(Pose.BoneModelMatrices["aim"] == Pose.BoneModelMatrices["hand"] &&
            Pose.BoneModelMatrices["follow"] == Pose.BoneModelMatrices["root"], "Virtual dependencies evaluate regardless of serialized order");
        Check(Bind.ToJson().ToJsonString() == new SkeletonRigPose(Asset).ToJson().ToJsonString() &&
            Bind.SocketModelMatrices["weapon"] != Pose.SocketModelMatrices["weapon"] && Asset.Serialize(false) == Json,
            "Two rig evaluations remain independent and never mutate the source definition");
        var Mesh = new SkeletalMeshAsset("engine://meshes/rig", "Rig Mesh", Asset.AssetId, Bones,
            new[]
            {
                new AssetVector3(0, 0, 0), new AssetVector3(1, 0, 0), new AssetVector3(0, 1, 0),
            }, new[]
            {
                0, 1, 2,
            }, Enumerable.Repeat(BoneInfluences.Four(0, 0, 0, 0, 1, 0, 0, 0), 3).ToArray());
        var Plain = new SkeletonInstance(Mesh).Prepare("e", "m", Animated, "READY");
        var Frame = new SkeletonInstance(Mesh, Asset).Prepare("e", "m", Animated, "READY");
        Check(Frame.SkinMatrices.Count == Bones.Length && Frame.SkinMatrices.SequenceEqual(Plain.SkinMatrices) &&
            Frame.Positions.SequenceEqual(Plain.Positions), "Virtual bones never change skin palette indices, matrices or weighted vertices");
        Check(Frame.RigPose!.SocketModelMatrices["weapon"] == Pose.SocketModelMatrices["weapon"] &&
            Frame.ToJson()["schemaVersion"]!.GetValue<int>() == 2, "Frozen Skeleton frame v2 exposes the same rig output");
        var Mutated = Frame.ToJson();
        Mutated["rigPose"] = null;
        Check(Frame.ToJson()["rigPose"] is not null, "Frame observation returns independent JSON");

        var Fixture = Directory.CreateTempSubdirectory("SharpEngine-SkeletonRig-");
        try
        {
            File.WriteAllText(Path.Combine(Fixture.FullName, "engine.json"), "{}");
            var Content = Directory.CreateDirectory(Path.Combine(Fixture.FullName, "Content/Assets"));
            File.WriteAllText(Path.Combine(Content.FullName, "assets.json"), """
                {"schemaVersion":2,"assets":[
                {"id":"skeletons/rig","type":"Skeleton","path":"Assets/rig.sskeleton"},
                {"id":"meshes/rig","type":"SkeletalMesh","path":"Assets/rig.sskeletalmesh"},
                {"id":"animations/rig","type":"SkeletalAnimation","path":"Assets/rig.sskeletalanimation"}]}
                """);
            File.WriteAllText(Path.Combine(Content.FullName, "rig.sskeleton"), Json);
            File.WriteAllText(Path.Combine(Content.FullName, "rig.sskeletalmesh"), Mesh.Serialize(false));
            var Clip = new SkeletalAnimationAsset("engine://animations/rig", "Animated Rig", 1, 30,
                Bones, Bones.Select(Bone => new BoneTrack(Bone.BoneId, Bone.BoneId, new[]
                {
                    new BoneKeyframe(0, Bone.BindTransform), new BoneKeyframe(1, Animated.Get(Bone.BoneId)),
                })).ToArray(), Asset.AssetId);
            File.WriteAllText(Path.Combine(Content.FullName, "rig.sskeletalanimation"), Clip.Serialize(false));
            var Assets = AssetRegistry.Load(Fixture.FullName);
            var Data = new SceneData();
            var Entity = Data.CreateEntity(Registry, "Rig");
            var Component = Data.AddComponent(Registry, Entity.EntityId, "engine.skeletal-mesh").Single(Component => Component.TypeId == "engine.skeletal-mesh");
            Component.State["Mesh"] = Mesh.AssetId;
            var Animator = Data.AddComponent(Registry, Entity.EntityId, "engine.animator").Single();
            Animator.State["Target"] = Component.ComponentId;
            Animator.State["Clip"] = Clip.AssetId;
            using var Runtime = new RuntimeWorld(SceneData.Parse(Data.Serialize(), Registry), Registry, Assets);
            var Live = (SkeletalMeshComponent)Runtime.FindComponent(Component.ComponentId)!;
            var Initial = Runtime.Frame.Get<SkeletonFrameState>(Component.ComponentId)!;
            Check(Initial.Code == "SKELETON_READY" && Live.RigPose!.SocketModelMatrices["weapon"] == Bind.SocketModelMatrices["weapon"],
                "Runtime reconstruction loads canonical rig definitions through the mesh dependency");
            Runtime.Tick(0.5f);
            var AnimatedFrame = Runtime.Frame.Get<SkeletonFrameState>(Component.ComponentId)!;
            Check(Near(Live.RigPose!.SocketModelMatrices["weapon"],
                new SkeletonRigPose(Asset, Clip.SamplePreview(0.5)).SocketModelMatrices["weapon"]) &&
                Live.RigPose.SocketModelMatrices["weapon"] != Bind.SocketModelMatrices["weapon"],
                "Animator playback evaluates sockets and virtual bones from the committed sampled pose");
            var Changed = Asset.ToJson();
            Changed["sockets"]![0]!["position"] = new JsonArray(5, 0, 0);
            File.WriteAllText(Path.Combine(Content.FullName, "rig.sskeleton"), Changed.ToJsonString());
            SceneRenderer.InvalidateAssets(Assets);
            Runtime.Tick(0);
            Check(Near(Live.RigPose!.SocketModelMatrices["weapon"],
                new SkeletonRigPose(SkeletonAsset.Parse(Changed.ToJsonString()), Clip.SamplePreview(0.5)).SocketModelMatrices["weapon"]) &&
                Live.RigPose.SocketModelMatrices["weapon"] != AnimatedFrame.RigPose!.SocketModelMatrices["weapon"] &&
                Initial.RigPose!.SocketModelMatrices["weapon"] == Bind.SocketModelMatrices["weapon"],
                "Asset invalidation rebuilds the runtime rig while old committed frames remain frozen");
            Check(!Runtime.Snapshot().Serialize().Contains("socketModelMatrices", StringComparison.Ordinal),
                "Derived rig matrices never enter World source state");
        }
        finally
        {
            Fixture.Delete(true);
        }
    }

    private static bool Near(Matrix4x4 First, Matrix4x4 Second)
    {
        var Delta = First - Second;
        return MathF.Abs(Delta.M11) + MathF.Abs(Delta.M12) + MathF.Abs(Delta.M13) + MathF.Abs(Delta.M14) +
            MathF.Abs(Delta.M21) + MathF.Abs(Delta.M22) + MathF.Abs(Delta.M23) + MathF.Abs(Delta.M24) +
            MathF.Abs(Delta.M31) + MathF.Abs(Delta.M32) + MathF.Abs(Delta.M33) + MathF.Abs(Delta.M34) +
            MathF.Abs(Delta.M41) + MathF.Abs(Delta.M42) + MathF.Abs(Delta.M43) + MathF.Abs(Delta.M44) < 0.0001f;
    }
}
