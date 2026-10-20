using System.Net;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

internal sealed class UiAssetEditorInstance : IAssetEditorInstance
{
    private readonly EditorAssetOpenContext Context;
    private readonly UiEditorDocument Document;

    public UiAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        var OwnerRoot = Context.Asset.Source == AssetSource.Project ?
            Path.GetDirectoryName(Context.Runtime.ProjectManifest ?? string.Empty) :
            Context.Runtime.EngineRoot;
        if (string.IsNullOrWhiteSpace(OwnerRoot))
        {
            OwnerRoot = Context.Runtime.EngineRoot;
        }
        Document = UiEditorDocument.Load(Context.Asset, Context.Path, OwnerRoot);
    }

    public AssetDescriptor Asset => Document.Asset;
    public string EditorId => "ui-editor";
    public string Title => Document.Asset.Name;
    public string PrimaryPaneId => "design";
    public bool IsDirty => Document.IsDirty;
    public bool IsReadOnly => Document.IsReadOnly;
    public IReadOnlyList<AssetEditorPaneDefinition> Panes { get; } =
    [
        new("design", "Design", false),
        new("elements", "Elements"),
        new("inspector", "Inspector"),
        new("source", "Source"),
        new("diagnostics", "Diagnostics"),
    ];
    public IReadOnlyList<AssetEditorCommandDefinition> Commands { get; } =
    [
        new("undo", "Undo", "Edit", 100, "reset", true),
        new("redo", "Redo", "Edit", 110, "reset", true),
        new("add-div", "Add Container", "Elements", 100, "add", true),
        new("add-span", "Add Text", "Elements", 110, "add", true),
        new("add-button", "Add Button", "Elements", 120, "add", true),
        new("delete", "Delete Element", "Elements", 200, null, false),
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
                    NodeId = "ui.root",
                    Kind = DockNodeKind.Split,
                    Orientation = DockSplitOrientation.Horizontal,
                    Ratio = 0.18f,
                    First = Tabs("ui.elements", "ui.elements.tabs", "elements"),
                    Second = new DockNodeState
                    {
                        NodeId = "ui.main",
                        Kind = DockNodeKind.Split,
                        Orientation = DockSplitOrientation.Horizontal,
                        Ratio = 0.76f,
                        First = new DockNodeState
                        {
                            NodeId = "ui.center",
                            Kind = DockNodeKind.Split,
                            Orientation = DockSplitOrientation.Vertical,
                            Ratio = 0.72f,
                            First = Tabs("ui.design", "ui.design.tabs", "design"),
                            Second = Tabs("ui.bottom", "ui.bottom.tabs", "source",
                                "diagnostics"),
                        },
                        Second = Tabs("ui.inspector", "ui.inspector.tabs", "inspector"),
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
            "undo" => !IsReadOnly && Document.CanUndo,
            "redo" => !IsReadOnly && Document.CanRedo,
            "add-div" or "add-span" or "add-button" or "delete" => !IsReadOnly,
            _ => false,
        };
    }

    public void ExecuteCommand(string CommandId)
    {
        string Message;
        var Changed = CommandId switch
        {
            "undo" => Document.Undo(),
            "redo" => Document.Redo(),
            "add-div" => Document.AddElement("div", out Message),
            "add-span" => Document.AddElement("span", out Message),
            "add-button" => Document.AddElement("button", out Message),
            "delete" => Document.DeleteSelected(out Message),
            _ => false,
        };
        if (Changed)
        {
            Context.SetStatus($"UI Editor · {Document.Asset.Name} · revision {Document.Revision}");
        }
    }

    public bool TryHandleAction(string Action)
    {
        if (Action.StartsWith("editor.ui-select:", StringComparison.Ordinal))
        {
            Document.Select(Action["editor.ui-select:".Length..]);
            return true;
        }
        if (Action.StartsWith("editor.ui-change:", StringComparison.Ordinal))
        {
            var Payload = Action["editor.ui-change:".Length..];
            var Separator = Payload.IndexOf(':');
            if (Separator > 0 && !Document.SetField(Payload[..Separator],
                    Payload[(Separator + 1)..], out var Message) && Message.Length != 0)
            {
                Context.SetStatus(Message);
            }
            return true;
        }
        if (Action == "editor.ui-position:flex" ||
            Action == "editor.ui-position:absolute")
        {
            if (!Document.SetPositionMode(Action.EndsWith("absolute",
                    StringComparison.Ordinal), out var Message) && Message.Length != 0)
            {
                Context.SetStatus(Message);
            }
            return true;
        }
        if (Action == "editor.ui-delete")
        {
            ExecuteCommand("delete");
            return true;
        }

        return false;
    }

    public string BuildPaneMarkup(string PaneId)
    {
        return PaneId switch
        {
            "design" => "<div class=\"ui-editor-canvas-shell asset-ui-design\"><div class=\"ui-canvas-rulers\"><span>SCREEN PREVIEW</span><div class=\"grow\" /><span>1280 x 720</span></div><div class=\"ui-editor-canvas-scroll\"><div class=\"ui-editor-canvas\">" + Document.BuildPreviewMarkup() + "</div></div></div>",
            "elements" => "<div class=\"ui-panel-title\">ELEMENTS</div><div class=\"ui-element-hierarchy\">" + Document.BuildHierarchyMarkup() + "</div>",
            "inspector" => "<div class=\"ui-panel-title\">UI INSPECTOR</div><div class=\"ui-element-inspector\">" + Document.BuildInspectorMarkup() + Document.BuildAssetSummaryMarkup() + "</div>",
            "source" => Document.BuildSourceMarkup(),
            "diagnostics" => "<div class=\"ui-diagnostics-pane\">" + Document.BuildDiagnosticsMarkup() + "</div>",
            _ => string.Empty,
        };
    }

    public void Save()
    {
        Document.Save();
    }

    public void Update(float DeltaSeconds)
    {
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
            ["revision"] = Document.Revision,
            ["selectedElement"] = Document.SelectedElementId,
            ["viewModelContract"] = Document.ViewModelContract,
            ["viewModelContractAvailable"] = Document.ViewModelContract.Length == 0 ||
                Context.Runtime.UiRuntime.ViewModelContracts.Any(Contract =>
                    Contract.Key.Equals(Document.ViewModelContract, StringComparison.Ordinal)),
            ["diagnostics"] = new JsonArray(Document.Diagnostics.Select(Diagnostic =>
                (JsonNode)new JsonObject
                {
                    ["severity"] = Diagnostic.Severity,
                    ["code"] = Diagnostic.Code,
                    ["message"] = Diagnostic.Message,
                }).ToArray()),
        };
    }

    public void Dispose()
    {
    }

    private static DockNodeState Tabs(string NodeId, string GroupId,
        params string[] PaneIds)
    {
        return new DockNodeState
        {
            NodeId = NodeId,
            Kind = DockNodeKind.Tabs,
            TabGroup = new DockTabGroupState
            {
                GroupId = GroupId,
                ActivePaneId = PaneIds[0],
                Panes = PaneIds.Select(PaneId => new DockPaneState
                {
                    PaneId = PaneId,
                }).ToList(),
            },
        };
    }
}
