using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor;

internal static class StaticMeshEditorValidation
{
    internal static void Run(EditorRuntime Runtime, string ProjectRoot, Action<bool, string> Check)
    {
        // The large-presentation fixture uses a separate instance and never enters the live editor registry.
        Check(Runtime.OpenAsset(BasicAssetFixture.StaticMeshId).Success, "StaticMesh editor opens a writable v1 asset");
        JsonObject Editor() => JsonNode.Parse(Runtime.DescribeEditorJson())!["assetTools"]!["editors"]!.AsArray()
            .Single(Node => Node!["assetId"]!.GetValue<string>() == BasicAssetFixture.StaticMeshId)!.AsObject();
        JsonObject State() => Editor()["state"]!.AsObject();
        void Action(string Operation) => Runtime.InvokeUiAction(
            $"editor.host-action:{Editor()["rootId"]}:editor.mesh-socket:{Operation}");
        void Command(string Operation) => Runtime.InvokeUiAction(
            $"editor.host-action:{Editor()["rootId"]}:editor.asset-command:{Operation}");
        var PathValue = Path.Combine(ProjectRoot, "Content", "Media", "Validation.sstaticmesh");
        var Original = File.ReadAllText(PathValue);
        Check(!State()["dirty"]!.GetValue<bool>() && File.ReadAllText(PathValue) == Original,
            "Reading legacy Socket state never migrates the file or dirties its document");
        Command("add-socket");
        var Id = State()["selectedSocketId"]!.GetValue<string>();
        Action("field:name:Grip");
        Runtime.InvokeUiAction($"editor.host-action:{Editor()["rootId"]}:ui.viewport3d:meshPreviewViewport:orbit:40:20");
        var Camera = State()["viewport"]!.ToJsonString();
        Action("field:position:0.1, 0.2, -0.3");
        Action("field:rotation:15, 40, 10");
        Action("field:scale:2, 1, 0.5");
        Check(State()["document"]!["sockets"]![0]!["socketId"]!.GetValue<string>() == Id &&
            State()["viewport"]!.ToJsonString() == Camera && State()["document"]!["schemaVersion"]!.GetValue<int>() == 3,
            "Socket edits preserve stable identity and camera, and commit schema v3 atomically");
        var Snapshot = State()["document"]!.ToJsonString();
        var Revision = State()["documentRevision"]!.GetValue<ulong>();
        foreach (var Invalid in new[]
        {
            "field:scale:0,1,1", "field:position:NaN,0,0", "field:rotation:0,0", "field:name:", "field:socketId:other", "field",
        })
        {
            Action(Invalid);
            Check(State()["document"]!.ToJsonString() == Snapshot && State()["documentRevision"]!.GetValue<ulong>() == Revision &&
                State()["diagnostics"]!.AsArray().Count == 1, "Invalid static Socket edits reject atomically: " + Invalid);
        }
        Command("duplicate");
        Check(State()["socketCount"]!.GetValue<int>() == 2 && State()["selectedSocketId"]!.GetValue<string>() != Id,
            "Duplicate creates a distinct Socket ID and unique name");
        var BeforeDuplicateName = State()["document"]!.ToJsonString();
        Action("field:name:Grip");
        Check(State()["document"]!.ToJsonString() == BeforeDuplicateName, "Duplicate Socket names reject atomically");
        Command("remove");
        Command("undo");
        Check(State()["socketCount"]!.GetValue<int>() == 2, "Socket removal can be undone");
        Command("redo");
        Check(State()["socketCount"]!.GetValue<int>() == 1 && File.ReadAllText(PathValue) == Original,
            "Socket editing and history never save implicitly");
        File.WriteAllText(PathValue, Original + "\n");
        Runtime.InvokeUiAction($"editor.host-action:{Editor()["rootId"]}:editor.asset-save");
        Check(State()["dirty"]!.GetValue<bool>() && File.ReadAllText(PathValue) == Original + "\n",
            "StaticMesh Save refuses external source conflicts");
        File.WriteAllText(PathValue, Original);
        Runtime.InvokeUiAction($"editor.host-action:{Editor()["rootId"]}:editor.asset-save");
        var Saved = StaticMeshAsset.Parse(File.ReadAllText(PathValue));
        Check(!State()["dirty"]!.GetValue<bool>() && Saved.Sockets.Count == 1 && Saved.Sockets[0].SocketId == Id &&
            AssetContent.LoadStaticMesh(Runtime.Assets!, BasicAssetFixture.StaticMeshId).Sockets[0] == Saved.Sockets[0],
            "Explicit Save persists v3 Socket data and the runtime asset reader reconstructs it");
        Command("undo");
        Check(State()["dirty"]!.GetValue<bool>(), "Undo after Socket Save leaves the savepoint");
        Command("redo");
        Check(!State()["dirty"]!.GetValue<bool>(), "Redo returns exactly to the Socket savepoint");
        Action("select-root");
        var BeforeRoot = State()["document"]!.ToJsonString();
        var RootRevision = State()["documentRevision"]!.GetValue<ulong>();
        foreach (var Invalid in new[]
        {
            "field:name:ChangedMesh", "field:position:1,2,3", "field:parentSocketId:" + Id, "remove", "duplicate",
        })
        {
            Action(Invalid);
            Check(State()["document"]!.ToJsonString() == BeforeRoot && State()["documentRevision"]!.GetValue<ulong>() == RootRevision,
                "The immutable Mesh root cannot be renamed, moved, reparented, removed or duplicated: " + Invalid);
        }
        Check(!State()["hierarchy"]!["root"]!["editable"]!.GetValue<bool>() &&
            State()["hierarchy"]!["selectedKind"]!.GetValue<string>() == "mesh" &&
            !State()["gizmo"]!["editable"]!.GetValue<bool>(), "The Mesh root is discoverable, selectable and has no transform Gizmo");
        Command("add-socket");
        var ParentId = State()["selectedSocketId"]!.GetValue<string>();
        Action("field:name:Pivot");
        Action("field:position:0.2,0.3,-0.4");
        Action("field:rotation:10,30,15");
        Action("field:scale:2,0.5,1.3");
        Command("add-socket");
        var ChildId = State()["selectedSocketId"]!.GetValue<string>();
        Action("field:name:Child");
        Action("field:position:0.2,0.1,0");
        Command("add-socket");
        var LeafId = State()["selectedSocketId"]!.GetValue<string>();
        Action("field:name:Leaf");
        Action("field:position:0.1,0,0.1");
        StaticMeshAsset Current() => StaticMeshAsset.Parse(State()["document"]!.ToJsonString());
        var Graph = Current();
        Check(Graph.Sockets.Single(Socket => Socket.SocketId == ParentId).ParentSocketId is null &&
            Graph.Sockets.Single(Socket => Socket.SocketId == ChildId).ParentSocketId == ParentId &&
            Graph.Sockets.Single(Socket => Socket.SocketId == LeafId).ParentSocketId == ChildId,
            "Add Socket uses the selected Mesh root or Socket as its persistent parent");
        var LeafBefore = Graph.SocketHierarchy.ModelMatrices[LeafId].Translation;
        var LeafLocal = Graph.Sockets.Single(Socket => Socket.SocketId == LeafId);
        Action("select:" + ParentId);
        Action("field:position:0.4,0.3,-0.4");
        Graph = Current();
        Check(System.Numerics.Vector3.Distance(Graph.SocketHierarchy.ModelMatrices[LeafId].Translation - LeafBefore,
            new System.Numerics.Vector3(0.2f, 0, 0)) < 0.0001f && Graph.Sockets.Single(Socket => Socket.SocketId == LeafId) == LeafLocal,
            "An ancestor edit updates descendant Mesh-space frames while preserving descendant local data");
        var BeforeCycle = State()["document"]!.ToJsonString();
        var CycleRevision = State()["documentRevision"]!.GetValue<ulong>();
        foreach (var ParentValue in new[]
        {
            ParentId, ChildId, LeafId, "missing-parent",
        })
        {
            Action("field:parentSocketId:" + ParentValue);
            Check(State()["document"]!.ToJsonString() == BeforeCycle && State()["documentRevision"]!.GetValue<ulong>() == CycleRevision,
                "Invalid, self and descendant reparenting are atomic rejections");
        }
        Action("select:" + ChildId);
        var ChildModel = Current().SocketHierarchy.ModelMatrices[ChildId];
        var LeafModel = Current().SocketHierarchy.ModelMatrices[LeafId];
        var BeforeReparent = State()["document"]!.ToJsonString();
        Action("field:parentSocketId:");
        Graph = Current();
        Check(Graph.Sockets.Single(Socket => Socket.SocketId == ChildId).ParentSocketId is null &&
            System.Numerics.Vector3.Distance(Graph.SocketHierarchy.ModelMatrices[ChildId].Translation, ChildModel.Translation) < 0.0001f &&
            System.Numerics.Vector3.Distance(Graph.SocketHierarchy.ModelMatrices[LeafId].Translation, LeafModel.Translation) < 0.0001f,
            "Reparenting to Mesh root preserves the selected frame and descendant placement");
        Command("undo");
        Check(State()["document"]!.ToJsonString() == BeforeReparent, "One Undo restores both the old parent and local TRS");
        Command("redo");
        Action("field:parentSocketId:" + ParentId);
        Action("field:rotation:0,45,0");
        var BeforeShear = State()["document"]!.ToJsonString();
        var ShearRevision = State()["documentRevision"]!.GetValue<ulong>();
        Action("field:parentSocketId:");
        Check(State()["document"]!.ToJsonString() == BeforeShear && State()["documentRevision"]!.GetValue<ulong>() == ShearRevision &&
            State()["diagnostic"]!.GetValue<string>().Contains("shear", StringComparison.Ordinal),
            "Reparenting refuses a lossy TRS decomposition under a rotated non-uniform parent");
        Runtime.InvokeUiAction($"editor.host-action:{Editor()["rootId"]}:editor.asset-save");
        Check(StaticMeshAsset.Parse(File.ReadAllText(PathValue)).Sockets.Single(Socket => Socket.SocketId == LeafId).ParentSocketId == ChildId &&
            !State()["dirty"]!.GetValue<bool>(), "Explicit Save preserves the complete Socket hierarchy");
        var BeforeParentRemoval = State()["document"]!.ToJsonString();
        var RemovalRevision = State()["documentRevision"]!.GetValue<ulong>();
        foreach (var NonLeaf in new[]
        {
            ParentId, ChildId,
        })
        {
            Action("select:" + NonLeaf);
            Action("remove");
            Check(State()["document"]!.ToJsonString() == BeforeParentRemoval && State()["documentRevision"]!.GetValue<ulong>() == RemovalRevision &&
                State()["diagnostic"]!.GetValue<string>().Contains("child", StringComparison.Ordinal),
                "Non-leaf Socket deletion is rejected atomically without orphaning or cascading");
        }
        Action("select:" + LeafId);
        Action("field:parentSocketId:" + ParentId);
        Action("select:" + ChildId);
        Command("remove");
        Check(Current().Sockets.All(Socket => Socket.SocketId != ChildId) &&
            Current().Sockets.Single(Socket => Socket.SocketId == LeafId).ParentSocketId == ParentId,
            "A former parent can be deleted after its child is reparented");
        Command("undo");
        Command("undo");
        Check(State()["document"]!.ToJsonString() == BeforeParentRemoval && !State()["dirty"]!.GetValue<bool>(),
            "Undo restores the leaf deletion and reparent operation back to the complete saved hierarchy");
        Action("select:" + ParentId);
        var BeforeStaleField = State()["document"]!.ToJsonString();
        Action("field-for:" + Uri.EscapeDataString(ChildId) + ":name:WrongTarget");
        Check(State()["document"]!.ToJsonString() == BeforeStaleField,
            "A stale text field cannot apply its value to a newly selected Socket");
        Action("select:" + ChildId);
        Runtime.OpenAsset("engine://meshes/robot-static");
        var ReadOnly = JsonNode.Parse(Runtime.DescribeEditorJson())!["assetTools"]!["editors"]!.AsArray()
            .Single(Node => Node!["assetId"]!.GetValue<string>() == "engine://meshes/robot-static")!;
        Runtime.InvokeUiAction($"editor.host-action:{ReadOnly["rootId"]}:editor.mesh-socket:add-socket");
        ReadOnly = JsonNode.Parse(Runtime.DescribeEditorJson())!["assetTools"]!["editors"]!.AsArray()
            .Single(Node => Node!["assetId"]!.GetValue<string>() == "engine://meshes/robot-static")!;
        Check(ReadOnly["state"]!["socketCount"]!.GetValue<int>() == 0 && !ReadOnly["dirty"]!.GetValue<bool>(),
            "Engine read-only assets reject direct Socket mutations");
        StaticMeshMarkupPerformanceValidation.Run(Runtime, ProjectRoot, Check);
    }
}
