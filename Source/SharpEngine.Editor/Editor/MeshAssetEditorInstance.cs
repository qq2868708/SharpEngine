using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

internal sealed partial class MeshAssetEditorInstance : IAssetEditorInstance, IAssetEditorLivePreview
{
    private readonly EditorAssetOpenContext Context;
    private readonly JsonAssetAuthoringDocument Document;
    private readonly AssetPreviewWorld PreviewWorld;
    private readonly WorldPreview Preview;
    private StaticMeshAsset? StaticMesh;
    private SkeletalMeshAsset? SkeletalMesh;
    private readonly SharpEngine.UI.UiTreeView BoneTreeView = new([]);

    private bool HasFramed;
    private float DetailsContentWidth;
    private readonly Dictionary<string, (ulong DocumentRevision, ulong InteractionRevision, float Width, string Diagnostic, string Markup)> PaneMarkupCache = new(StringComparer.Ordinal);

    public MeshAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        PreviewWorld = new AssetPreviewWorld(Context.Runtime.Components ??
            throw new InvalidOperationException("Component registry is unavailable"), "mesh-preview:" + Context.Asset.Id);
        Preview = new WorldPreview(PreviewWorld.World, Context.Runtime.Assets, "meshPreviewViewport", "asset-mesh-preview");
        Preview.Viewport.PointerInput = IsStatic;
        Preview.Content.Overlay = DecoratePreview;

        Document = new JsonAssetAuthoringDocument(Context.Asset, Context.Path,
            Validate);
        RefreshAsset();
        var PaneValues = new List<AssetEditorPaneDefinition>
        {
            new("preview", "Preview", false),
            new("details", "Details"),
            new("diagnostics", "Diagnostics"),
        };
        if (IsStatic)
        {
            PaneValues.Add(new AssetEditorPaneDefinition("sockets", "Sockets"));
        }
        if (IsSkeletal)
        {
            PaneValues.Add(new AssetEditorPaneDefinition("skeleton", "Skeleton"));
        }
        Panes = PaneValues;
    }

    public AssetDescriptor Asset
    {
        get
        {
            return Context.Asset;
        }
    }
    public string EditorId
    {
        get
        {
            return IsSkeletal ? "skeletal-mesh-editor" : "mesh-editor";
        }
    }
    public string Title
    {
        get
        {
            return Asset.Name;
        }
    }
    public string PrimaryPaneId
    {
        get
        {
            return "preview";
        }
    }
    public bool IsDirty
    {
        get
        {
            return Document.IsDirty;
        }
    }
    public bool IsReadOnly
    {
        get
        {
            return Document.IsReadOnly;
        }
    }
    public IReadOnlyList<AssetEditorPaneDefinition> Panes
    {
        get;
    }
    public IReadOnlyList<AssetEditorCommandDefinition> Commands
    {
        get
        {
            return IsStatic ?
            [
                new("undo", "Undo", "Edit", 100, "reset", true),
                new("redo", "Redo", "Edit", 110, "reset", true),
                new("add-socket", "Add Socket", "Socket", 200, "add", true),
                new("duplicate", "Duplicate Selected", "Socket", 210, null, true),
                new("remove", "Remove Selected", "Socket", 220, "trash", true),
                new("move", "Move", "Socket", 300, null, true),
                new("rotate", "Rotate", "Socket", 310, null, true),
                new("scale", "Scale", "Socket", 320, null, true),
            ] :
            [
                new("undo", "Undo", "Edit", 100, "reset", true),
                new("redo", "Redo", "Edit", 110, "reset", true),
            ];
        }
    }
    private bool IsSkeletal
    {
        get
        {
            return Asset.Type.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase);
        }
    }
    private bool IsStatic
    {
        get
        {
            return Asset.Type.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase);
        }
    }

    public EditorLayoutDocument CreateDefaultLayout()
    {
        var Center = AssetEditorInstanceUtilities.Tabs("asset.preview", "asset.preview.tabs", "preview");
        if (IsStatic || IsSkeletal)
        {
            Center = new DockNodeState
            {
                NodeId = "asset.center",
                Kind = DockNodeKind.Split,
                Orientation = DockSplitOrientation.Horizontal,
                Ratio = 0.18f,
                First = AssetEditorInstanceUtilities.Tabs("asset.tree", "asset.tree.tabs", IsStatic ? "sockets" : "skeleton"),
                Second = Center,
            };
        }
        return new EditorLayoutDocument
        {
            MainHost = new DockHostState
            {
                HostId = "main",
                Root = new DockNodeState
                {
                    NodeId = "asset.root",
                    Kind = DockNodeKind.Split,
                    Orientation = DockSplitOrientation.Horizontal,
                    Ratio = 0.76f,
                    First = Center,
                    Second = AssetEditorInstanceUtilities.Tabs("asset.details", "asset.details.tabs", "details"),
                },
            },
            HiddenPaneIds = ["diagnostics"],
            ActiveContentManagerId = string.Empty,
            ContentManagers = [],
        };
    }

    public bool CanExecuteCommand(string CommandId)
    {
        return CommandId switch
        {
            "undo" => !IsReadOnly && Document.CanUndo,
            "redo" => !IsReadOnly && Document.CanRedo,
            "add-socket" => IsStatic && !IsReadOnly,
            "remove" => IsStatic && !IsReadOnly && SelectedSocketId is not null && !StaticMesh!.SocketHierarchy.HasChildren(SelectedSocketId),
            "duplicate" or "move" or "rotate" or "scale" => IsStatic && !IsReadOnly && SelectedSocketId is not null,
            _ => false,
        };
    }

    public void ExecuteCommand(string CommandId)
    {
        CancelGizmoDrag();
        EndTextInput();
        if (!CanExecuteCommand(CommandId))
        {
            return;
        }
        if (CommandId is "move" or "rotate" or "scale")
        {
            GizmoTool = CommandId;
            InteractionRevision++;
            return;
        }
        if (CommandId is "add-socket" or "duplicate" or "remove")
        {
            HandleSocketAction(CommandId);
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
        const string BeginInputPrefix = "editor.mesh-input:begin:";
        const string EndInputPrefix = "editor.mesh-input:end:";
        if (Action.StartsWith(BeginInputPrefix, StringComparison.Ordinal))
        {
            CancelGizmoDrag();
            ActiveInputId = Action[BeginInputPrefix.Length..];
            InteractionRevision++;
            return true;
        }
        if (Action.StartsWith(EndInputPrefix, StringComparison.Ordinal))
        {
            if (ActiveInputId == Action[EndInputPrefix.Length..])
            {
                EndTextInput();
            }
            return true;
        }
        if (IsStatic && TryHandleGizmoAction(Action))
        {
            return true;
        }
        if (IsStatic && Action == "ui.viewport3d:meshPreviewViewport:focus")
        {
            CancelGizmoDrag();
            EndTextInput();
            FrameMeshPreview();
            return true;
        }
        if (Preview.Viewport.TryHandleAction(Action))
        {
            CancelGizmoDrag();
            EndTextInput();
            return true;
        }
        const string TreePrefix = "editor.mesh-tree-toggle:";
        if (Action.StartsWith(TreePrefix, StringComparison.Ordinal))
        {
            var Key = Action[TreePrefix.Length..];
            if (!CollapsedSocketNodes.Remove(Key))
            {
                CollapsedSocketNodes.Add(Key);
            }
            InteractionRevision++;
            return true;
        }
        const string SocketPrefix = "editor.mesh-socket:";
        if (IsStatic && Action.StartsWith(SocketPrefix, StringComparison.Ordinal))
        {
            CancelGizmoDrag();
            HandleSocketAction(Action[SocketPrefix.Length..]);
            return true;
        }
        const string MaterialPrefix = "editor.mesh-default-material:";
        if (!Action.StartsWith(MaterialPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var Value = Action[MaterialPrefix.Length..].Trim();
        CancelGizmoDrag();
        var Changed = Document.Edit("default-material", Root =>
        {
            Root["defaultMaterial"] = Value.Length == 0 ? null : Value;
        }, out var Message);
        if (Changed)
        {
            RefreshAsset();
        }
        else if (Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
        return true;
    }

    public string BuildPaneMarkup(string PaneId)
    {
        if (PaneId == "preview")
        {
            return BuildPreviewMarkup();
        }
        var Width = PaneId == "details" ? DetailsContentWidth : 0;
        var Cached = PaneMarkupCache.GetValueOrDefault(PaneId);
        if (Cached.Markup is not null && Cached.DocumentRevision == Document.Revision &&
            Cached.InteractionRevision == InteractionRevision && Cached.Width == Width && Cached.Diagnostic == Diagnostic)
        {
            return Cached.Markup;
        }
        var Markup = PaneId switch
        {
            "details" => BuildDetailsMarkup(),
            "sockets" => BuildSocketsMarkup(),
            "skeleton" => BuildSkeletonMarkup(),
            "diagnostics" => AssetEditorInstanceUtilities.Diagnostics(SocketDiagnostics()),
            _ => string.Empty,
        };
        PaneMarkupCache[PaneId] = (Document.Revision, InteractionRevision, Width, Diagnostic, Markup);
        return Markup;
    }

    public void Save()
    {
        CancelGizmoDrag();
        EndTextInput();
        if (IsStatic)
        {
            EditSockets("schema-upgrade", _ =>
            {
            });
        }
        Document.Save();
        SharpEngine.World.SceneRenderer.InvalidateAssets(Context.Runtime.Assets ??
            throw new InvalidOperationException("Asset registry is unavailable"));
        RefreshAsset();
    }

    public void Update(float DeltaSeconds)
    {
        var HasDetails = Context.TryGetElementRect("meshDetails", out var Bounds);
        if (HasDetails && Bounds.Width > 0)
        {
            DetailsContentWidth = MathF.Max(1, MathF.Floor(Bounds.Width));
        }
        Preview.Viewport.Render(Context);
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 3,
            ["stateRevision"] = Document.Revision + InteractionRevision + Preview.Viewport.Describe()["stateRevision"]!.GetValue<ulong>(),
            ["assetId"] = Asset.Id,
            ["editorRootId"] = Context.EditorRootId,
            ["assetType"] = Asset.Type,
            ["previewWorld"] = PreviewWorld.Describe(),
            ["viewport"] = Preview.Viewport.Describe(),
            ["dirty"] = IsDirty,
            ["readOnly"] = IsReadOnly,
            ["documentRevision"] = Document.Revision,
            ["savepointRevision"] = Document.SavepointRevision,
            ["vertexCount"] = IsSkeletal ? SkeletalMesh!.Positions.Count :
                StaticMesh!.Positions.Count,
            ["triangleCount"] = (IsSkeletal ? SkeletalMesh!.Indices.Count :
                StaticMesh!.Indices.Count) / 3,
            ["boneCount"] = SkeletalMesh?.Bones.Count ?? 0,
            ["socketCount"] = IsStatic ? StaticMesh!.Sockets.Count : 0,
            ["selectedSocketId"] = SelectedSocketId,
            ["activeInputId"] = ActiveInputId,
            ["hierarchy"] = IsStatic ? DescribeSocketTree() : null,
            ["socketPose"] = IsStatic ? StaticMesh!.SocketHierarchy.ToJson() : null,
            ["document"] = IsStatic ? StaticMesh!.ToJson() : null,
            ["diagnostic"] = Diagnostic,
            ["diagnostics"] = DescribeSocketDiagnostics(),
            ["gizmo"] = IsStatic ? DescribeGizmo() : null,
            ["defaultMaterial"] = IsSkeletal ? SkeletalMesh!.DefaultMaterial :
                StaticMesh!.DefaultMaterial,
        };
    }

    public void Dispose()
    {
        CancelGizmoDrag();
        EndTextInput();
    }

    private void Validate(JsonObject Root)
    {
        var Source = Root.ToJsonString();
        if (IsSkeletal)
        {
            var Value = SkeletalMeshAsset.Parse(Source, Context.Path);
            if (!Value.AssetId.Equals(Asset.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Skeletal mesh assetId does not match the manifest");
            }
            ValidateMaterial(Value.DefaultMaterial);
            return;
        }

        var Mesh = StaticMeshAsset.Parse(Source, Context.Path, Asset.Id);
        if (!Mesh.AssetId.Equals(Asset.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Static mesh assetId does not match the manifest");
        }
        ValidateMaterial(Mesh.DefaultMaterial);
    }

    private void ValidateMaterial(string? MaterialId)
    {
        if (string.IsNullOrWhiteSpace(MaterialId))
        {
            return;
        }

        var Material = Context.Runtime.Assets?.Find(MaterialId);
        if (Material is null || !Material.Exists ||
            !Material.Type.Equals("Material", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Default material is missing or not a Material asset: {MaterialId}");
        }
    }

    private void RefreshAsset()
    {
        var Source = Document.Snapshot.ToJsonString();
        if (IsSkeletal)
        {
            SkeletalMesh = SkeletalMeshAsset.Parse(Source, Context.Path);
            StaticMesh = null;
        }
        else
        {
            StaticMesh = StaticMeshAsset.Parse(Source, Context.Path, Asset.Id);
            SkeletalMesh = null;
            EnsureSocketSelection();
        }
        PreviewWorld.SetMesh(Asset.Id, IsSkeletal ? "engine.skeletal-mesh" :
            Asset.Type == "StaticMesh" ? "engine.static-mesh" : "engine.mesh-renderer",
            IsSkeletal ? SkeletalMesh!.DefaultMaterial : StaticMesh!.DefaultMaterial, SkeletalMesh);
        if (!HasFramed)
        {
            FrameMeshPreview();
            HasFramed = true;
        }
    }

    private void FrameMeshPreview()
    {
        var Positions = IsSkeletal ? SkeletalMesh!.Positions : StaticMesh!.Positions;
        var Points = Positions.Select(Position => new Vector3(Position.X, Position.Y, Position.Z));
        var SocketPoints = IsStatic ? StaticMesh!.SocketHierarchy.ModelMatrices.Values.Select(Matrix => Matrix.Translation) : [];
        Preview.Viewport.Frame(Points.Concat(SocketPoints));
    }

    private string BuildPreviewMarkup()
    {
        return Preview.Viewport.BuildMarkup(Asset.Name);
    }

    private string BuildDetailsMarkup()
    {
        var VertexCount = IsSkeletal ? SkeletalMesh!.Positions.Count :
            StaticMesh!.Positions.Count;
        var IndexCount = IsSkeletal ? SkeletalMesh!.Indices.Count :
            StaticMesh!.Indices.Count;
        var Material = IsSkeletal ? SkeletalMesh!.DefaultMaterial :
            StaticMesh!.DefaultMaterial;
        var Markup = new StringBuilder("<div id=\"meshDetails\" class=\"asset-inspector mesh-asset-inspector\">");
        if (IsStatic && SelectedSocketId is not null)
        {
            Markup.Append(BuildSocketDetails());
        }
        Markup.Append(AssetEditorInstanceUtilities.ReadOnlyField("Asset ID", Asset.Id, "meshAssetId"))
            .Append(AssetEditorInstanceUtilities.ReadOnlyField("Vertices",
                VertexCount.ToString(), "meshVertexCount"))
            .Append(AssetEditorInstanceUtilities.ReadOnlyField("Triangles",
                (IndexCount / 3).ToString(), "meshTriangleCount"));
        if (IsSkeletal)
        {
            Markup.Append(AssetEditorInstanceUtilities.ReadOnlyField("Skeleton",
                SkeletalMesh!.SkeletonId))
                .Append(AssetEditorInstanceUtilities.ReadOnlyField("Bones",
                    SkeletalMesh.Bones.Count.ToString()))
                .Append(AssetEditorInstanceUtilities.ReadOnlyField("Influence sets",
                    SkeletalMesh.Influences.Count.ToString()));
        }
        else
        {
            Markup.Append(AssetEditorInstanceUtilities.ReadOnlyField("Primitive",
                StaticMesh!.Primitive))
                .Append(AssetEditorInstanceUtilities.ReadOnlyField("Coordinates",
                    StaticMesh.CoordinateSystem));
        }
        Markup.Append(AssetEditorInstanceUtilities.Field("Default Material",
            Material ?? string.Empty, "editor.mesh-default-material:", IsReadOnly,
            "Stable Material asset URI", "meshDefaultMaterial",
            "editor.mesh-input:begin:meshDefaultMaterial", "editor.mesh-input:end:meshDefaultMaterial"));
        if (IsStatic && SelectedSocketId is null)
        {
            Markup.Append(BuildSocketDetails());
        }
        Markup.Append("</div>");
        // Native Dock sizing resolves after the new pane markup is first measured.
        // Feed its measured content width back into fields so intrinsic text cannot
        // leave them formatted against the provisional zero-width allocation.
        var Source = Markup.ToString();
        if (DetailsContentWidth <= 0)
        {
            return Source;
        }
        var Width = DetailsContentWidth.ToString("G9", System.Globalization.CultureInfo.InvariantCulture) + "px";
        return Source.Replace("class=\"asset-field\"", "class=\"asset-field\" style=\"width:" + Width + ";\"", StringComparison.Ordinal)
            .Replace("class=\"se-value-field", "style=\"width:" + Width + ";\" class=\"se-value-field", StringComparison.Ordinal)
            .Replace("<p>", "<p style=\"width:" + Width + ";\">", StringComparison.Ordinal);
    }

    private string BuildSkeletonMarkup()
    {
        if (SkeletalMesh is null)
        {
            return "<div class=\"ui-editor-empty\">No skeleton</div>";
        }

        var Nodes = SkeletalMesh.Bones.Select(Bone => new SharpEngine.UI.UiTreeNode(Bone.BoneId, Bone.ParentBoneId)).ToArray();
        var ById = SkeletalMesh.Bones.ToDictionary(Bone => Bone.BoneId, StringComparer.Ordinal);
        var Rows = SharpEngine.UI.UiTreeProjection.Visible(Nodes, CollapsedSocketNodes).Select(Node => new SharpEngine.UI.UiTreeRow(
            Node.Key, ById[Node.Key].Name, Node.Key, EditorResourceUris.IconSource("mesh"), Node.Depth, false,
            Node.HasChildren, Node.Expanded, string.Empty, "editor.mesh-tree-toggle:" + Node.Key,
            "meshBone-" + Node.Key, "meshBoneToggle-" + Node.Key)).ToArray();
        BoneTreeView.SetRows(Rows);
        return "<div class=\"asset-tree\">" + BoneTreeView.BuildLayout(default) + "</div>";
    }
}
