using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

internal sealed class SkeletonAssetEditorInstance : IAssetEditorInstance
{
    private readonly EditorAssetOpenContext Context;
    private readonly JsonAssetAuthoringDocument Document;
    private readonly AssetPreviewWorld PreviewWorld;
    private readonly WorldViewportLayout Viewport = new("skeletonPreviewViewport", "asset-skeleton-preview");
    private SkeletonAsset Skeleton = null!;
    private SkeletalMeshAsset? PreviewMesh;

    public SkeletonAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        PreviewWorld = new AssetPreviewWorld(Context.Runtime.Components ??
            throw new InvalidOperationException("Component registry is unavailable"), "skeleton-preview:" + Context.Asset.Id);
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
            _ => false,
        };
    }

    public void ExecuteCommand(string CommandId)
    {
        var Changed = CommandId == "undo" ? Document.Undo() :
            CommandId == "redo" && Document.Redo();
        if (Changed)
        {
            RefreshAsset();
        }
    }

    public bool TryHandleAction(string Action)
    {
        if (Viewport.TryHandleAction(PreviewWorld.World, Action))
        {
            return true;
        }
        const string Prefix = "editor.skeleton-preview-mesh:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var Value = Action[Prefix.Length..].Trim();
        var Changed = Document.Edit("preview-mesh", Root =>
        {
            Root["previewMeshId"] = Value.Length == 0 ? null : Value;
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
        return PaneId switch
        {
            "preview" => Viewport.BuildMarkup(Skeleton.Name),
            "tree" => BuildTreeMarkup(),
            "details" => BuildDetailsMarkup(),
            "source" => AssetEditorInstanceUtilities.Source(Document.Source),
            "diagnostics" => AssetEditorInstanceUtilities.Diagnostics([]),
            _ => string.Empty,
        };
    }

    public void Save()
    {
        Document.Save();
        RefreshAsset();
    }

    public void Update(float DeltaSeconds)
    {
        Viewport.Render(Context, PreviewWorld.World, Context.Runtime.Assets, PreviewWorld.Decorate);
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["assetId"] = Asset.Id,
            ["editorRootId"] = Context.EditorRootId,
            ["dirty"] = IsDirty,
            ["readOnly"] = IsReadOnly,
            ["documentRevision"] = Document.Revision,
            ["savepointRevision"] = Document.SavepointRevision,
            ["previewWorld"] = PreviewWorld.Describe(),
            ["viewport"] = Viewport.Describe(),
            ["boneCount"] = Skeleton.Bones.Count,
            ["previewMeshId"] = Skeleton.PreviewMeshId,
            ["previewMeshAvailable"] = PreviewMesh is not null,
        };
    }

    public void Dispose()
    {
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
        PreviewMesh = ResolvePreviewMesh(Skeleton, false);
        if (PreviewMesh is null)
        {
            PreviewWorld.SetSkeleton(Skeleton);
            Viewport.Frame(PreviewWorld.World, PreviewWorld.SkeletonPositions);
        }
        else
        {
            PreviewWorld.SetMesh(PreviewMesh.AssetId, "engine.skeletal-mesh", PreviewMesh.DefaultMaterial, PreviewMesh);
            Viewport.Frame(PreviewWorld.World, PreviewMesh.Positions.Select(Position => new Vector3(Position.X, Position.Y, Position.Z)));
        }
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
                "Compatible SkeletalMesh asset URI") + "</div>";
    }

    private string BuildTreeMarkup()
    {
        var Markup = new StringBuilder("<div class=\"asset-tree\">");
        foreach (var Bone in Skeleton.Bones)
        {
            var Depth = 0;
            var ParentId = Bone.ParentBoneId;
            while (ParentId is not null)
            {
                Depth++;
                ParentId = Skeleton.Bones.First(Value =>
                    Value.BoneId == ParentId).ParentBoneId;
            }
            Markup.Append("<div class=\"asset-tree-row\" style=\"padding-left:")
                .Append(8 + Depth * 14).Append("px\"><strong>")
                .Append(AssetEditorInstanceUtilities.Escape(Bone.Name))
                .Append("</strong><span>")
                .Append(AssetEditorInstanceUtilities.Escape(Bone.BoneId))
                .Append("</span></div>");
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }
}
