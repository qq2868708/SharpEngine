using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Editor.Layout;
using SharpEngine.World;

namespace SharpEngine.Editor;

internal sealed class PrefabAssetEditorInstance : IAssetEditorInstance
{
    private readonly EditorAssetOpenContext Context;
    private readonly WorldDocument Document;
    private readonly WorldAuthoringSession Authoring;
    private readonly PrefabPreviewWorld PreviewWorld;
    private readonly WorldPreview Preview;
    private ulong PreviewDocumentRevision;
    private string? SelectedEntityId;

    public PrefabAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        var Components = Context.Runtime.Components ?? throw new InvalidOperationException(
            "Component registry is unavailable");
        Document = WorldDocument.Load(Context.Path, Components);
        if (!Document.Data.IsPrefab)
        {
            throw new InvalidDataException("Expected a Prefab document");
        }
        Authoring = new WorldAuthoringSession(Document);
        SelectedEntityId = Document.Entities.FirstOrDefault()?.Id;
        PreviewWorld = new PrefabPreviewWorld(Document, Context.Runtime.Assets ??
            throw new InvalidOperationException("Asset registry is unavailable"), "prefab-preview:" + Context.Asset.Id);

        Preview = new WorldPreview(PreviewWorld.World, Context.Runtime.Assets, "prefabPreviewViewport", "asset-prefab-preview");
        PreviewDocumentRevision = Authoring.DocumentRevision;
        Preview.Viewport.Frame(PreviewWorld.FramePositions());
    }

    public AssetDescriptor Asset => Context.Asset;
    public string EditorId => "prefab-editor";
    public string Title => Asset.Name;
    public string PrimaryPaneId => "viewport";
    public bool IsDirty => Authoring.IsDirty;
    public bool IsReadOnly => Asset.ReadOnly;
    public IReadOnlyList<AssetEditorPaneDefinition> Panes { get; } =
    [
        new("viewport", "Viewport", false),
        new("hierarchy", "Hierarchy"),
        new("details", "Details"),
    ];
    public IReadOnlyList<AssetEditorCommandDefinition> Commands { get; } =
    [
        new("add-entity", "Add Entity", "Edit", 100, "add", true),
        new("undo", "Undo", "Edit", 200, "reset", true),
        new("redo", "Redo", "Edit", 210, "reset", true),
    ];

    public EditorLayoutDocument CreateDefaultLayout()
    {
        return new EditorLayoutDocument
        {
            MainHost = new DockHostState
            {
                HostId = "main",
                Root = new DockNodeState
                {
                    NodeId = "prefab.root",
                    Kind = DockNodeKind.Split,
                    Orientation = DockSplitOrientation.Horizontal,
                    Ratio = 0.2f,
                    First = Tabs("prefab.hierarchy", "prefab.hierarchy.tabs", "hierarchy"),
                    Second = new DockNodeState
                    {
                        NodeId = "prefab.work",
                        Kind = DockNodeKind.Split,
                        Orientation = DockSplitOrientation.Horizontal,
                        Ratio = 0.76f,
                        First = Tabs("prefab.viewport", "prefab.viewport.tabs", "viewport"),
                        Second = Tabs("prefab.details", "prefab.details.tabs", "details"),
                    },
                },
            },
            HiddenPaneIds = [],
            ActiveContentManagerId = string.Empty,
            ContentManagers = [],
        };
    }

    public bool CanExecuteCommand(string CommandId)
    {
        return CommandId switch
        {
            "add-entity" => !IsReadOnly,
            "undo" => !IsReadOnly && Authoring.CanUndo,
            "redo" => !IsReadOnly && Authoring.CanRedo,
            _ => false,
        };
    }

    public void ExecuteCommand(string CommandId)
    {
        if (CommandId == "add-entity")
        {
            AddEntity();
        }
        else if (CommandId == "undo")
        {
            Authoring.Undo(Authoring.DocumentRevision);
        }
        else if (CommandId == "redo")
        {
            Authoring.Redo(Authoring.DocumentRevision);
        }
        SynchronizePreview();
    }

    public bool TryHandleAction(string Action)
    {
        if (Preview.Viewport.TryHandleAction(Action))
        {
            return true;
        }
        const string SelectPrefix = "editor.prefab-select:";
        if (Action.StartsWith(SelectPrefix, StringComparison.Ordinal))
        {
            var EntityId = Action[SelectPrefix.Length..];
            if (Document.Entities.Any(Entity => Entity.Id == EntityId))
            {
                SelectedEntityId = EntityId;
            }
            return true;
        }
        return false;
    }

    public string BuildPaneMarkup(string PaneId)
    {
        return PaneId switch
        {
            "viewport" => Preview.Viewport.BuildMarkup("Prefab Preview"),
            "hierarchy" => BuildHierarchyMarkup(),
            "details" => BuildDetailsMarkup(),
            _ => string.Empty,
        };
    }

    public void Save()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("This Prefab is read only");
        }
        Authoring.Save(Context.Path);
    }

    public void Update(float DeltaSeconds)
    {
        try
        {
            SynchronizePreview();
            Preview.Viewport.Render(Context);
        }
        catch (Exception Exception)
        {
            SharpEngine.Logging.SharpLog.Error("PrefabEditor",
                "Prefab preview frame failed", Exception);
        }
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["assetId"] = Asset.Id,
            ["editorRootId"] = Context.EditorRootId,
            ["previewWorld"] = PreviewWorld.Describe(),
            ["viewport"] = Preview.Viewport.Describe(),
            ["dirty"] = IsDirty,
            ["readOnly"] = IsReadOnly,
            ["documentRevision"] = Authoring.DocumentRevision,
            ["selectedEntityId"] = SelectedEntityId,
            ["entityCount"] = Document.Entities.Count,
        };
    }

    public void Dispose()
    {
    }

    private void SynchronizePreview()
    {
        if (PreviewDocumentRevision != Authoring.DocumentRevision)
        {
            PreviewWorld.Synchronize(Document);
            PreviewDocumentRevision = Authoring.DocumentRevision;
        }
    }

    private void AddEntity()
    {
        string? CreatedId = null;
        var Result = Authoring.Edit("prefab.entity.create", Authoring.DocumentRevision,
            Data =>
            {
                var ParentId = SelectedEntityId ?? Data.Entities[0].EntityId;
                var Entity = Data.CreateEntity(Document.Registry, "Entity", ParentId);
                CreatedId = Entity.EntityId;
            }, []);
        if (Result.Success)
        {
            SelectedEntityId = CreatedId;
        }
        else
        {
            Context.SetStatus(Result.Message);
        }
    }

    private string BuildHierarchyMarkup()
    {
        var Markup = new StringBuilder("<div class=\"prefab-asset-hierarchy\">");
        foreach (var Entity in Document.Entities)
        {
            Markup.Append("<button class=\"prefab-entity")
                .Append(Entity.Id == SelectedEntityId ? " selected" : string.Empty)
                .Append("\" data-action=\"editor.prefab-select:")
                .Append(WebUtility.HtmlEncode(Entity.Id)).Append("\">")
                .Append(WebUtility.HtmlEncode(Entity.Name)).Append("</button>");
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }

    private string BuildDetailsMarkup()
    {
        var Entity = Document.Entities.FirstOrDefault(Entity => Entity.Id == SelectedEntityId);
        if (Entity is null)
        {
            return "<div class=\"ui-editor-empty\">Select an entity</div>";
        }

        var Markup = new StringBuilder("<div class=\"pane-content asset-prefab-details\"><div class=\"component-title\">")
            .Append(WebUtility.HtmlEncode(Entity.Name)).Append("</div><div class=\"field\"><label>Entity ID</label><span>")
            .Append(WebUtility.HtmlEncode(Entity.Id)).Append("</span></div><div class=\"field\"><label>Parent</label><span>")
            .Append(WebUtility.HtmlEncode(Entity.ParentId)).Append("</span></div>");
        foreach (var Component in Entity.Components)
        {
            Markup.Append("<div class=\"component\"><div class=\"component-title\">")
                .Append(WebUtility.HtmlEncode(Component.TypeId))
                .Append("</div><pre class=\"timeline-details\">")
                .Append(WebUtility.HtmlEncode(Component.State.ToJsonString()))
                .Append("</pre></div>");
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }

    private static DockNodeState Tabs(string NodeId, string GroupId, string PaneId) =>
        new()
        {
            NodeId = NodeId,
            Kind = DockNodeKind.Tabs,
            TabGroup = new DockTabGroupState
            {
                GroupId = GroupId,
                ActivePaneId = PaneId,
                Panes = [new DockPaneState { PaneId = PaneId }],
            },
        };
}
