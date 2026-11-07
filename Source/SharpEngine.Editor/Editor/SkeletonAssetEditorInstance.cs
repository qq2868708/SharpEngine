using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

internal sealed partial class SkeletonAssetEditorInstance : IAssetEditorInstance, IAssetEditorLivePreview
{
    private readonly EditorAssetOpenContext Context;
    private readonly JsonAssetAuthoringDocument Document;
    private readonly AssetPreviewWorld PreviewWorld;
    private readonly WorldPreview Preview;
    private SkeletonAsset Skeleton = null!;
    private SkeletalMeshAsset? PreviewMesh;
    private bool HasFramed;
    private string? FramedMeshId;

    public SkeletonAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        PreviewWorld = new AssetPreviewWorld(Context.Runtime.Components ??
            throw new InvalidOperationException("Component registry is unavailable"), "skeleton-preview:" + Context.Asset.Id);
        Preview = new WorldPreview(PreviewWorld.World, Context.Runtime.Assets, "skeletonPreviewViewport", "asset-skeleton-preview");
        Preview.Viewport.PointerInput = true;
        Preview.Content.Overlay = DecoratePreview;

        Document = new JsonAssetAuthoringDocument(Context.Asset, Context.Path,
            Validate);
        RefreshAsset();
    }

    public SharpEngine.Assets.AssetDescriptor Asset => Context.Asset;
    public string EditorId => "skeleton-editor";
    public string Title => Asset.Name;
    public string PrimaryPaneId => "preview";
    public bool IsDirty => Document.IsDirty;
    public bool IsReadOnly => Document.IsReadOnly;
    public IReadOnlyList<AssetEditorPaneDefinition> Panes { get; } =
    [
        new("preview", "Preview", false),
        new("tree", "Skeleton Tree"),
        new("details", "Details"),
        new("source", "Source"),
        new("diagnostics", "Diagnostics"),
    ];
    public IReadOnlyList<AssetEditorCommandDefinition> Commands { get; } =
    [
        new("undo", "Undo", "Edit", 100, "reset", true),
        new("redo", "Redo", "Edit", 110, "reset", true),
        new("add-socket", "Add Socket", "Skeleton", 200, "add", true),
        new("add-virtual-bone", "Add Virtual Bone", "Skeleton", 210, "add", true),
        new("duplicate", "Duplicate Selected", "Skeleton", 220, "copy", false),
        new("remove", "Remove Selected", "Skeleton", 230, "delete", false),
        new("move", "Move", "Socket", 300, null, true, "Drag a Socket axis to move"),
        new("rotate", "Rotate", "Socket", 310, null, true, "Drag a Socket ring to rotate"),
        new("scale", "Scale", "Socket", 320, null, true, "Drag a Socket box to scale"),
    ];

    public EditorLayoutDocument CreateDefaultLayout() =>
        AssetEditorInstanceUtilities.CreatePreviewLayout("preview", "details",
            "tree", "source", "diagnostics");

    public bool CanExecuteCommand(string CommandId)
    {
        return CommandId switch
        {
            "undo" => !IsReadOnly && Document.CanUndo,
            "redo" => !IsReadOnly && Document.CanRedo,
            "add-socket" or "add-virtual-bone" => !IsReadOnly,
            "duplicate" or "remove" => !IsReadOnly && SelectedKind != "bone",
            "move" or "rotate" or "scale" => !IsReadOnly && SelectedKind == "socket",
            _ => false,
        };
    }

    public void ExecuteCommand(string CommandId)
    {
        CancelGizmoDrag();
        if (CommandId is "move" or "rotate" or "scale")
        {
            GizmoTool = CommandId;
            InteractionRevision++;
            return;
        }
        if (CommandId is not ("undo" or "redo"))
        {
            HandleRigAction(CommandId);
            return;
        }
        var Changed = CommandId == "undo" ? Document.Undo() :
            CommandId == "redo" && Document.Redo();
        if (Changed)
        {
            Diagnostic = string.Empty;
            RefreshAsset();
        }
    }

    public bool TryHandleAction(string Action)
    {
        if (TryHandleGizmoAction(Action))
        {
            return true;
        }
        if (Action == "ui.viewport3d:skeletonPreviewViewport:focus")
        {
            CancelGizmoDrag();
            FramePreview();
            return true;
        }
        if (Preview.Viewport.TryHandleAction(Action))
        {
            CancelGizmoDrag();
            return true;
        }
        const string TreePrefix = "editor.skeleton-tree-toggle:";
        if (Action.StartsWith(TreePrefix, StringComparison.Ordinal))
        {
            var Key = Action[TreePrefix.Length..];
            if (!CollapsedTreeNodes.Remove(Key))
            {
                CollapsedTreeNodes.Add(Key);
            }
            InteractionRevision++;
            return true;
        }
        const string RigPrefix = "editor.skeleton:";
        if (Action.StartsWith(RigPrefix, StringComparison.Ordinal))
        {
            CancelGizmoDrag();
            InteractionRevision++;
            HandleRigAction(Action[RigPrefix.Length..]);
            return true;
        }
        const string Prefix = "editor.skeleton-preview-mesh:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var Value = Action[Prefix.Length..].Trim();
        CancelGizmoDrag();
        EditRig("preview-mesh", Root =>
        {
            Root["previewMeshId"] = Value.Length == 0 ? null : Value;
        });
        return true;
    }

    public string BuildPaneMarkup(string PaneId)
    {
        return PaneId switch
        {
            "preview" => Preview.Viewport.BuildMarkup(Skeleton.Name + " · Socket " + GizmoTool + " · Esc cancels drag"),
            "tree" => BuildTreeMarkup(),
            "details" => BuildDetailsMarkup(),
            "source" => AssetEditorInstanceUtilities.Source(Document.Source),
            "diagnostics" => AssetEditorInstanceUtilities.Diagnostics(Diagnostic.Length == 0 ? [] :
                [new("error", "SKELETON_AUTHORING_INVALID", Diagnostic)]),
            _ => string.Empty,
        };
    }

    public void Save()
    {
        CancelGizmoDrag();
        EditRig("schema-upgrade", _ =>
        {
        });
        Document.Save();
        InteractionRevision++;
        SharpEngine.World.SceneRenderer.InvalidateAssets(Context.Runtime.Assets ??
            throw new InvalidOperationException("Asset registry is unavailable"));
        RefreshAsset();
    }

    public void Update(float DeltaSeconds)
    {
        Preview.Viewport.Render(Context);
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 3,
            ["stateRevision"] = Document.Revision + InteractionRevision +
                Preview.Viewport.Describe()["stateRevision"]!.GetValue<ulong>(),
            ["assetId"] = Asset.Id,
            ["editorRootId"] = Context.EditorRootId,
            ["dirty"] = IsDirty,
            ["readOnly"] = IsReadOnly,
            ["documentRevision"] = Document.Revision,
            ["savepointRevision"] = Document.SavepointRevision,
            ["previewWorld"] = PreviewWorld.Describe(),
            ["viewport"] = Preview.Viewport.Describe(),
            ["boneCount"] = Skeleton.Bones.Count,
            ["previewMeshId"] = Skeleton.PreviewMeshId,
            ["previewMeshAvailable"] = PreviewMesh is not null,
            ["selectedKind"] = SelectedKind,
            ["selectedId"] = SelectedId,
            ["collapsedTreeKeys"] = new JsonArray(CollapsedTreeNodes.Order(StringComparer.Ordinal).Select(Key => (JsonNode)Key).ToArray()),
            ["diagnostic"] = Diagnostic,
            ["document"] = Skeleton.ToJson(),
            ["rigPose"] = new SkeletonRigPose(Skeleton).ToJson(),
            ["gizmo"] = DescribeGizmo(),
        };
    }

    public void Dispose()
    {
        CancelGizmoDrag();
    }

    private void Validate(JsonObject Root)
    {
        var Value = SkeletonAsset.Parse(Root.ToJsonString(), Context.Path);
        if (!Value.AssetId.Equals(Asset.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Skeleton assetId does not match the manifest");
        }
        ResolvePreviewMesh(Value, true);
    }

    private void RefreshAsset()
    {
        Skeleton = SkeletonAsset.Parse(Document.Snapshot.ToJsonString(), Context.Path);
        EnsureSelection();
        PreviewMesh = ResolvePreviewMesh(Skeleton, false);
        if (PreviewMesh is null)
        {
            PreviewWorld.SetSkeleton(Skeleton);
        }
        else
        {
            PreviewWorld.SetMesh(PreviewMesh.AssetId, "engine.skeletal-mesh", PreviewMesh.DefaultMaterial, PreviewMesh, Skeleton);
        }
        if (!HasFramed || FramedMeshId != PreviewMesh?.AssetId)
        {
            FramePreview();
        }
    }

    private void FramePreview()
    {
        var MeshPositions = PreviewMesh?.Positions.Select(Position => new Vector3(Position.X, Position.Y, Position.Z)) ?? [];
        Preview.Viewport.Frame(MeshPositions.Concat(PreviewWorld.SkeletonPositions));
        HasFramed = true;
        FramedMeshId = PreviewMesh?.AssetId;
    }

    private SkeletalMeshAsset? ResolvePreviewMesh(SkeletonAsset Value, bool ValidateOnly)
    {
        if (Value.PreviewMeshId is null)
        {
            return null;
        }
        var Assets = Context.Runtime.Assets ?? throw new InvalidOperationException(
            "Asset registry is unavailable");
        var Mesh = SkeletalMeshContent.Load(Assets, Value.PreviewMeshId);
        if (!Mesh.SkeletonId.Equals(Value.AssetId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Preview mesh '{Mesh.AssetId}' uses skeleton '{Mesh.SkeletonId}'");
        }
        return ValidateOnly ? null : Mesh;
    }

    private string BuildDetailsMarkup()
    {
        return "<div class=\"asset-inspector\">" +
            AssetEditorInstanceUtilities.ReadOnlyField("Asset ID", Asset.Id) +
            AssetEditorInstanceUtilities.ReadOnlyField("Bones",
                Skeleton.Bones.Count.ToString()) +
            AssetEditorInstanceUtilities.Field("Preview Mesh",
                Skeleton.PreviewMeshId ?? string.Empty,
                "editor.skeleton-preview-mesh:", IsReadOnly,
                "Compatible SkeletalMesh asset URI", "skeletonField-previewMesh") + BuildRigDetails() + "</div>";
    }

    private readonly SharpEngine.UI.UiTreeView SkeletonTreeView = new([]);

    private readonly HashSet<string> CollapsedTreeNodes = new(StringComparer.Ordinal);

    private string BuildTreeMarkup()
    {
        var Entries = Skeleton.Bones.Select(Bone => (Id: Bone.BoneId, Parent: Bone.ParentBoneId, Bone.Name, Kind: "bone"))
            .Concat(Skeleton.VirtualBones.Select(Bone => (Id: Bone.BoneId, Parent: (string?)Bone.SourceBoneId, Bone.Name, Kind: "virtual")))
            .Concat(Skeleton.Sockets.Select(Socket => (Id: Socket.SocketId, Parent: (string?)Socket.ParentBoneId, Socket.Name, Kind: "socket"))).ToArray();
        var Nodes = Entries.Select(Entry => new SharpEngine.UI.UiTreeNode(Entry.Id, Entry.Parent)).ToArray();
        var ById = Entries.ToDictionary(Entry => Entry.Id, StringComparer.Ordinal);
        var Rows = SharpEngine.UI.UiTreeProjection.Visible(Nodes, CollapsedTreeNodes).Select(Node =>
        {
            var Entry = ById[Node.Key];
            return new SharpEngine.UI.UiTreeRow(Node.Key, Entry.Name, Entry.Kind + " / " + Entry.Id,
                EditorResourceUris.IconSource(Entry.Kind == "socket" ? "move" : "mesh"), Node.Depth,
                SelectedId == Entry.Id, Node.HasChildren, Node.Expanded,
                "editor.skeleton:select:" + Entry.Kind + ":" + Entry.Id,
                "editor.skeleton-tree-toggle:" + Entry.Id, "skeletonNode-" + Entry.Id, "skeletonToggle-" + Entry.Id);
        }).ToArray();
        SkeletonTreeView.SetRows(Rows);
        return "<div class=\"asset-tree\">" + SkeletonTreeView.BuildLayout(default) + "</div>";
    }
}
