using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using SharpEngine.Editor;
using SharpEngine.Rendering;
using SharpEngine.UI;

internal static class SocketGizmoVisibilityValidation
{
    internal static void Run(EditorRuntime Runtime, string ProjectRoot, Action<bool, string> Check)
    {
        Validate(BasicAssetFixture.StaticMeshId, "Validation.sstaticmesh", "mesh-editor", true);
        Validate(BasicAssetFixture.SkeletonId, "Validation.sskeleton", "skeleton-editor", false);

        void Validate(string AssetId, string SourceName, string EditorId, bool StaticMesh)
        {
            var SourcePath = Path.Combine(ProjectRoot, "Content", "Media", SourceName);
            var Source = JsonNode.Parse(File.ReadAllText(SourcePath))!.AsObject();
            Source["schemaVersion"] = 3;
            Source["sockets"] = new JsonArray(Enumerable.Range(0, 2).Select(Index => new JsonObject
            {
                ["socketId"] = "visibility-" + Index,
                ["name"] = "Visibility " + Index,
                [StaticMesh ? "parentSocketId" : "parentBoneId"] = StaticMesh ? null : "root",
                ["position"] = new JsonArray(0.2f + Index * 0.3f, 0.4f, 0.2f),
                ["rotation"] = new JsonArray(0, 0, 0, 1),
                ["scale"] = new JsonArray(1, 1, 1),
            }).ToArray());
            if (!StaticMesh)
            {
                Source["previewMeshId"] = null;
                Source["virtualBones"] = new JsonArray();
            }
            var TestPath = Path.Combine(ProjectRoot, "Content", "Media", "Visibility-" + SourceName);
            File.WriteAllText(TestPath, Source.ToJsonString());
            var Context = new EditorAssetOpenContext(Runtime, Runtime.Assets!.Find(AssetId)!, TestPath,
                EditorId, "visibility-" + EditorId);
            using IAssetEditorInstance Instance = StaticMesh ? new MeshAssetEditorInstance(Context) : new SkeletonAssetEditorInstance(Context);
            var Decorate = Instance.GetType().GetMethod("DecoratePreview", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var Bounds = new UiRect(100, 50, 800, 600);
            var Navigation = new OrbitCameraController();
            Navigation.Frame(new[]
            {
                new Vector3(-2), new Vector3(2),
            });
            var RenderContext = new ViewportRenderContext(Bounds, Navigation.Camera);
            var EmptyFrame = new ViewportFrame(Bounds, [], []);
            ViewportFrame Render() => (ViewportFrame)Decorate.Invoke(Instance, new object[]
            {
                RenderContext, EmptyFrame,
            })!;
            var Before = Instance.Describe()["document"]!.ToJsonString();
            var Unselected = Render();
            Check(Unselected.LineCount == (StaticMesh ? 2 : 3),
                EditorId + " draws only parent/bone links when no Socket is selected, with no persistent RGB axes");
            foreach (var Tool in new[]
            {
                "move", "rotate", "scale",
            })
            {
                Instance.ExecuteCommand(Tool);
                for (var Index = 0; Index < 2; Index++)
                {
                    Instance.TryHandleAction((StaticMesh ? "editor.mesh-socket:select:" : "editor.skeleton:select:socket:") + "visibility-" + Index);
                    Check(Render().LineCount > Unselected.LineCount,
                        EditorId + " displays " + Tool + " handles on the selected Socket");
                }
                Instance.TryHandleAction(StaticMesh ? "editor.mesh-socket:select-root" : "editor.skeleton:select:bone:root");
                Check(Render().Lines.SequenceEqual(Unselected.Lines),
                    EditorId + " immediately removes all manipulation handles after changing selection to the root/bone");
            }
            Check(Instance.Describe()["document"]!.ToJsonString() == Before && !Instance.IsDirty,
                EditorId + " selection and handle visibility preserve authoring data and dirty state");
        }
    }
}
