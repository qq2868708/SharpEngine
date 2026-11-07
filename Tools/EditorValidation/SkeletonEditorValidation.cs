using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Editor;

internal static class SkeletonEditorValidation
{
    internal static void Run(EditorRuntime Runtime, string ProjectRoot, Action<bool, string> Check)
    {
        SkeletonGizmoValidation.Run(Check);
        Check(Runtime.OpenAsset(BasicAssetFixture.SkeletonId).Success, "Writable Skeleton editor opens");
        JsonObject Editor() => JsonNode.Parse(Runtime.DescribeEditorJson())!["assetTools"]!["editors"]!.AsArray()
            .Single(Node => Node!["assetId"]!.GetValue<string>() == BasicAssetFixture.SkeletonId)!.AsObject();
        JsonObject State() => Editor()["state"]!.AsObject();
        void Action(string Operation) => Runtime.InvokeUiAction(
            $"editor.host-action:{Editor()["rootId"]}:editor.skeleton:{Operation}");
        void Command(string Operation) => Runtime.InvokeUiAction(
            $"editor.host-action:{Editor()["rootId"]}:editor.asset-command:{Operation}");
        var PathValue = Path.Combine(ProjectRoot, "Content", "Media", "Validation.sskeleton");
        var Original = File.ReadAllText(PathValue);
        var BeforeSelection = State()["documentRevision"]!.GetValue<ulong>();
        Action("select:bone:hand");
        Check(!State()["dirty"]!.GetValue<bool>() && State()["selectedId"]!.GetValue<string>() == "hand" &&
            State()["documentRevision"]!.GetValue<ulong>() == BeforeSelection,
            "Bone selection is transient and never migrates or dirties a legacy source");
        Command("add-socket");
        var SocketId = State()["selectedId"]!.GetValue<string>();
        Check(State()["document"]!["sockets"]![0]!["parentBoneId"]!.GetValue<string>() == "hand" &&
            State()["document"]!["schemaVersion"]!.GetValue<int>() == 3,
            "Add Socket uses the selected parent and atomically upgrades to schema v3");
        Action("field:name:Weapon");
        Runtime.InvokeUiAction($"editor.host-action:{Editor()["rootId"]}:ui.viewport3d:skeletonPreviewViewport:orbit:40:20");
        var ViewBeforeEdit = State()["viewport"]!.ToJsonString();
        Action("field:position:0.2, 0.1, -0.3");
        Action("field:rotation:20, 45, 10");
        Action("field:scale:2, 1, 0.5");
        Check(State()["document"]!["sockets"]![0]!["socketId"]!.GetValue<string>() == SocketId &&
            State()["document"]!["sockets"]![0]!["name"]!.GetValue<string>() == "Weapon",
            "Socket rename preserves stable ID and non-default local TRS");
        Check(State()["viewport"]!.ToJsonString() == ViewBeforeEdit,
            "Socket edits preserve the independently navigated preview camera");
        var Snapshot = State()["document"]!.ToJsonString();
        var Revision = State()["documentRevision"]!.GetValue<ulong>();
        foreach (var Invalid in new[]
        {
            "field:scale:0,1,1", "field:position:NaN,0,0", "field:rotation:0,0", "field:parentBoneId:missing",
            "field:name:", "field:socketId:changed", "field",
        })
        {
            Action(Invalid);
            Check(State()["document"]!.ToJsonString() == Snapshot &&
                State()["documentRevision"]!.GetValue<ulong>() == Revision &&
                State()["diagnostic"]!.GetValue<string>().Length > 0,
                "Invalid Skeleton edit rejects atomically: " + Invalid);
        }
        Command("duplicate");
        Check(State()["document"]!["sockets"]!.AsArray().Count == 2 &&
            State()["selectedId"]!.GetValue<string>() != SocketId, "Duplicate Socket has independent identity");
        Command("remove");
        Check(State()["document"]!["sockets"]!.AsArray().Count == 1, "Unreferenced Socket can be removed");
        Command("undo");
        Command("redo");
        Check(State()["document"]!["sockets"]!.AsArray().Count == 1, "Socket deletion participates in Undo/Redo");
        Action("select:bone:hand");
        Command("add-virtual-bone");
        var VirtualId = State()["selectedId"]!.GetValue<string>();
        Action("field:name:Aim");
        Action("field:sourceBoneId:hand");
        Action("field:targetBoneId:root");
        Check(State()["rigPose"]!["virtualBoneLocalMatrices"]![VirtualId] is not null,
            "Authored virtual bone is evaluated and exposed in the preview snapshot");
        Action("select:socket:" + SocketId);
        Action("field:parentBoneId:" + VirtualId);
        Action("select:virtual:" + VirtualId);
        var BeforeInvalid = State()["document"]!.ToJsonString();
        foreach (var Invalid in new[]
        {
            "remove", "field:sourceBoneId:" + VirtualId, "field:targetBoneId:missing",
        })
        {
            Action(Invalid);
            Check(State()["document"]!.ToJsonString() == BeforeInvalid &&
                State()["diagnostic"]!.GetValue<string>().Length > 0,
                "Referenced virtual bone removal and cyclic/dangling references are rejected: " + Invalid);
        }
        Check(File.ReadAllText(PathValue) == Original, "Socket and virtual bone edits never save implicitly");
        File.WriteAllText(PathValue, Original + "\n");
        Runtime.InvokeUiAction($"editor.host-action:{Editor()["rootId"]}:editor.asset-save");
        Check(State()["dirty"]!.GetValue<bool>() && File.ReadAllText(PathValue) == Original + "\n",
            "Skeleton Save detects an external source conflict");
        File.WriteAllText(PathValue, Original);
        Runtime.InvokeUiAction($"editor.host-action:{Editor()["rootId"]}:editor.asset-save");
        var Saved = SkeletonAsset.Parse(File.ReadAllText(PathValue));
        Check(!State()["dirty"]!.GetValue<bool>() && Saved.Sockets.Count == 1 &&
            Saved.VirtualBones.Count == 1 && Saved.Sockets[0].ParentBoneId == VirtualId,
            "Explicit Save persists validated rig definitions and clears dirty state");
        Command("undo");
        Check(State()["dirty"]!.GetValue<bool>(), "Undo after Save compares against the rig savepoint");
        Command("redo");
        Check(!State()["dirty"]!.GetValue<bool>(), "Redo returns exactly to the rig savepoint");
        Check(Runtime.OpenAsset(BasicAssetFixture.SkeletonId).Code == "ASSET_EDITOR_REUSED", "Skeleton editor reuses its document root");
        Runtime.OpenAsset("engine://skeletons/robot");
        var EngineEditor = JsonNode.Parse(Runtime.DescribeEditorJson())!["assetTools"]!["editors"]!.AsArray()
            .Single(Node => Node!["assetId"]!.GetValue<string>() == "engine://skeletons/robot")!;
        Runtime.InvokeUiAction($"editor.host-action:{EngineEditor["rootId"]}:editor.skeleton:add-socket");
        var ReadOnlyState = JsonNode.Parse(Runtime.DescribeEditorJson())!["assetTools"]!["editors"]!.AsArray()
            .Single(Node => Node!["assetId"]!.GetValue<string>() == "engine://skeletons/robot")!;
        Check(ReadOnlyState["state"]!["readOnly"]!.GetValue<bool>() &&
            !ReadOnlyState["dirty"]!.GetValue<bool>() &&
            ReadOnlyState["state"]!["document"]!["sockets"]!.AsArray().Count == 0,
            "Engine Skeleton remains read only after a direct semantic mutation request");
        Check(!ReadOnlyState["state"]!["gizmo"]!["editable"]!.GetValue<bool>() &&
            ReadOnlyState["state"]!["gizmo"]!["handles"]!.AsArray().Count == 0,
            "Read-only Skeleton never exposes editable Gizmo handles");
    }
}
