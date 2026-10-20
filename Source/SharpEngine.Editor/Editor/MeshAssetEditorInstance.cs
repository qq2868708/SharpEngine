using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

internal sealed class MeshAssetEditorInstance : IAssetEditorInstance
{
    private readonly EditorAssetOpenContext Context;
    private readonly JsonAssetAuthoringDocument Document;
    private StaticMeshAsset? StaticMesh;
    private SkeletalMeshAsset? SkeletalMesh;

    public MeshAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        Document = new JsonAssetAuthoringDocument(Context.Asset, Context.Path,
            Validate);
        RefreshAsset();
        var PaneValues = new List<AssetEditorPaneDefinition>
        {
            new("preview", "Preview", false),
            new("details", "Details"),
            new("source", "Source"),
            new("diagnostics", "Diagnostics"),
        };
        if (IsSkeletal)
        {
            PaneValues.Add(new AssetEditorPaneDefinition("skeleton", "Skeleton"));
        }
        Panes = PaneValues;
    }

    public AssetDescriptor Asset => Context.Asset;
    public string EditorId => IsSkeletal ? "skeletal-mesh-editor" : "mesh-editor";
    public string Title => Asset.Name;
    public string PrimaryPaneId => "preview";
    public bool IsDirty => Document.IsDirty;
    public bool IsReadOnly => Document.IsReadOnly;
    public IReadOnlyList<AssetEditorPaneDefinition> Panes { get; }
    public IReadOnlyList<AssetEditorCommandDefinition> Commands { get; } =
    [
        new("undo", "Undo", "Edit", 100, "reset", true),
        new("redo", "Redo", "Edit", 110, "reset", true),
    ];
    private bool IsSkeletal => Asset.Type.Equals("SkeletalMesh",
        StringComparison.OrdinalIgnoreCase);

    public EditorLayoutDocument CreateDefaultLayout()
    {
        var Bottom = IsSkeletal ? new[] { "source", "skeleton", "diagnostics" } :
            ["source", "diagnostics"];
        return AssetEditorInstanceUtilities.CreatePreviewLayout("preview", "details",
            Bottom);
    }

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
        const string MaterialPrefix = "editor.mesh-default-material:";
        if (!Action.StartsWith(MaterialPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var Value = Action[MaterialPrefix.Length..].Trim();
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
        return PaneId switch
        {
            "preview" => BuildPreviewMarkup(),
            "details" => BuildDetailsMarkup(),
            "source" => AssetEditorInstanceUtilities.Source(Document.Source),
            "skeleton" => BuildSkeletonMarkup(),
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
        if (!Context.TryGetElementRect("meshPreviewViewport", out var Bounds))
        {
            return;
        }

        var Positions = IsSkeletal ? SkeletalMesh!.Positions : StaticMesh!.Positions;
        var Indices = IsSkeletal ? SkeletalMesh!.Indices : StaticMesh!.Indices;
        Context.SubmitViewportFrame("asset-mesh-preview",
            AssetPreviewRenderer.BuildMeshFrame(Positions, Indices, Bounds,
                PreviewColor(), $"mesh-preview-camera:{Asset.Id}"));
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["assetId"] = Asset.Id,
            ["editorRootId"] = Context.EditorRootId,
            ["assetType"] = Asset.Type,
            ["dirty"] = IsDirty,
            ["readOnly"] = IsReadOnly,
            ["documentRevision"] = Document.Revision,
            ["savepointRevision"] = Document.SavepointRevision,
            ["vertexCount"] = IsSkeletal ? SkeletalMesh!.Positions.Count :
                StaticMesh!.Positions.Count,
            ["triangleCount"] = (IsSkeletal ? SkeletalMesh!.Indices.Count :
                StaticMesh!.Indices.Count) / 3,
            ["boneCount"] = SkeletalMesh?.Bones.Count ?? 0,
            ["defaultMaterial"] = IsSkeletal ? SkeletalMesh!.DefaultMaterial :
                StaticMesh!.DefaultMaterial,
        };
    }

    public void Dispose()
    {
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
        }
    }

    private Vector4 PreviewColor()
    {
        var MaterialId = IsSkeletal ? SkeletalMesh!.DefaultMaterial :
            StaticMesh!.DefaultMaterial;
        if (MaterialId is not null && Context.Runtime.Assets is { } Assets)
        {
            try
            {
                var Material = AssetContent.LoadMaterial(Assets, MaterialId);
                return new Vector4(Material.BaseColor.R, Material.BaseColor.G,
                    Material.BaseColor.B, Material.BaseColor.A);
            }
            catch (Exception Exception) when (Exception is InvalidDataException or
                IOException or KeyNotFoundException)
            {
            }
        }
        return new Vector4(0.58f, 0.7f, 0.86f, 1);
    }

    private string BuildPreviewMarkup()
    {
        return "<div class=\"asset-preview-surface\"><viewport id=\"meshPreviewViewport\" data-viewport-kind=\"asset-mesh-preview\" data-input-surface=\"mesh-preview\" /><div class=\"asset-preview-overlay\"><span>" +
            AssetEditorInstanceUtilities.Escape(Asset.Name) +
            "</span><span>" + (IsSkeletal ? "Skeletal Mesh" : "Static Mesh") +
            "</span></div></div>";
    }

    private string BuildDetailsMarkup()
    {
        var VertexCount = IsSkeletal ? SkeletalMesh!.Positions.Count :
            StaticMesh!.Positions.Count;
        var IndexCount = IsSkeletal ? SkeletalMesh!.Indices.Count :
            StaticMesh!.Indices.Count;
        var Material = IsSkeletal ? SkeletalMesh!.DefaultMaterial :
            StaticMesh!.DefaultMaterial;
        var Markup = new StringBuilder("<div class=\"asset-inspector\">")
            .Append(AssetEditorInstanceUtilities.ReadOnlyField("Asset ID", Asset.Id))
            .Append(AssetEditorInstanceUtilities.ReadOnlyField("Vertices",
                VertexCount.ToString()))
            .Append(AssetEditorInstanceUtilities.ReadOnlyField("Triangles",
                (IndexCount / 3).ToString()));
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
            "Stable Material asset URI"))
            .Append("</div>");
        return Markup.ToString();
    }

    private string BuildSkeletonMarkup()
    {
        if (SkeletalMesh is null)
        {
            return "<div class=\"ui-editor-empty\">No skeleton</div>";
        }

        var Markup = new StringBuilder("<div class=\"asset-tree\">");
        foreach (var Bone in SkeletalMesh.Bones)
        {
            var Depth = 0;
            var ParentId = Bone.ParentBoneId;
            while (ParentId is not null)
            {
                Depth++;
                ParentId = SkeletalMesh.Bones.First(Value =>
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
