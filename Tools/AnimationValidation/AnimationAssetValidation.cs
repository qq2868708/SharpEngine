using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.World;

internal static class AnimationAssetValidation
{
    internal static void Run(string Root, SceneData Source, ComponentRegistry Registry, string AnimatorId, Action<bool, string> Check)
    {
        var Fixture = Directory.CreateTempSubdirectory("SharpEngine-AnimationAssets-");
        try
        {
            File.WriteAllText(Path.Combine(Fixture.FullName, "engine.json"), "{}");
            var Content = Directory.CreateDirectory(Path.Combine(Fixture.FullName, "Content", "Assets"));
            var Manifest = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["assets"] = new JsonArray
                {
                    Entry("animations/robot-walk", "SkeletalAnimation", "clip.json"),
                    Entry("meshes/robot-skeletal", "SkeletalMesh", "mesh.json"),
                    Entry("skeletons/robot", "Skeleton", "skeleton.json"),
                },
            };
            File.WriteAllText(Path.Combine(Content.FullName, "assets.json"), Manifest.ToJsonString());
            var ClipPath = Path.Combine(Content.FullName, "clip.json");
            var Clip = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "Content/Assets/Animations/RobotWalk.skeletalanimation.json")))!.AsObject();
            var Mesh = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "Content/Assets/Meshes/Robot.skeletalmesh.json")))!.AsObject();
            Mesh.Remove("sourcePath");
            Mesh.Remove("sourceSha256");
            File.WriteAllText(Path.Combine(Content.FullName, "mesh.json"), Mesh.ToJsonString());
            File.Copy(Path.Combine(Root, "Content/Assets/Skeletons/Robot.skeleton.json"), Path.Combine(Content.FullName, "skeleton.json"));
            File.WriteAllText(ClipPath, Clip.ToJsonString());
            var Assets = AssetRegistry.Load(Fixture.FullName);
            Check(Assets.Diagnostics.All(Diagnostic => !Diagnostic.IsError), "isolated animation asset fixture resolves");
            using var Runtime = new RuntimeWorld(Source, Registry, Assets);
            var Animator = (AnimatorComponent)Runtime.FindComponent(AnimatorId)!;
            Runtime.Tick(0.125f);
            Check(Animator.Time > 0, "valid clip binding advances in isolated fixture");
            var BeforeFailure = Animator.Time;
            Clip["skeletonId"] = "engine://skeletons/other";
            File.WriteAllText(ClipPath, Clip.ToJsonString());
            SceneRenderer.InvalidateAssets(Assets);
            Runtime.Tick(0.125f);
            Check(Animator.Time == BeforeFailure && new AnimationSnapshot(Runtime.Frame).ToJson()["animators"]![0]!["code"]!.GetValue<string>() == "ANIMATOR_BINDING_INVALID", "mismatched skeleton rejects binding after asset invalidation");
            Clip["skeletonId"] = "engine://skeletons/robot";
            Clip["bones"]![1]!["parentBoneId"] = null;
            File.WriteAllText(ClipPath, Clip.ToJsonString());
            SceneRenderer.InvalidateAssets(Assets);
            Runtime.Tick(0.125f);
            Check(Animator.Time == BeforeFailure, "mismatched bone hierarchy does not advance");
            Clip["bones"]![1]!["parentBoneId"] = "root";
            Clip["assetId"] = "engine://animations/wrong-id";
            File.WriteAllText(ClipPath, Clip.ToJsonString());
            var IdentityRejected = false;
            try
            {
                SkeletalAnimationContent.Load(Assets, "engine://animations/robot-walk");
            }
            catch (InvalidDataException)
            {
                IdentityRejected = true;
            }
            Check(IdentityRejected, "registered and serialized animation IDs must agree");
            Clip["assetId"] = "engine://animations/robot-walk";
            File.WriteAllText(ClipPath, Clip.ToJsonString());
            SceneRenderer.InvalidateAssets(Assets);
            Runtime.Tick(0.125f);
            Check(Animator.Time > BeforeFailure, "repaired asset resumes after cache invalidation");
        }
        finally
        {
            // Delete only the exact temporary directory created for this validation.
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
            ["path"] = "Content/Assets/" + FileName,
        };
    }
}
