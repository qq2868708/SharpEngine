using System.Net;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;
using SharpEngine.UI;

namespace SharpEngine.Editor;

/// <summary>Formal RML UI asset editor. The authored RML source stays authoritative;
/// selection, resolved styles, diagnostics and drag transactions are editor state.</summary>
internal sealed class UiAssetEditorInstance : IAssetEditorInstance, IAssetEditorLivePreview,
    IAssetEditorDocumentPreview
{
    private readonly EditorAssetOpenContext Context;
    private readonly UiEditorDocument Document;
    private readonly IEditorUiPreview? Preview;
    private readonly List<AssetEditorLiveProperty> PendingLiveProperties = [];
    private bool MarkupRefreshRequested;

    public UiAssetEditorInstance(EditorAssetOpenContext Context, IEditorUiPreview? Preview = null)
    {
        this.Context = Context;
        this.Preview = Preview;
        var OwnerRoot = Context.Asset.Source == AssetSource.Project ?
            Path.GetDirectoryName(Context.Runtime.ProjectManifest ?? string.Empty) :
            Context.Runtime.EngineRoot;
        if (string.IsNullOrWhiteSpace(OwnerRoot))
        {
            OwnerRoot = Context.Runtime.EngineRoot;
        }
        Document = UiEditorDocument.Load(Context.Asset, Context.Path, OwnerRoot);
        if (Context.Runtime.Assets is not null)
        {
            Document.SetGlobalStyleResolver(GlobalStyleSheetAsset.CreateResolver(Context.Runtime.Assets));
            Document.SetAssetResolver(UiLayoutAssets.CreateResolver(Context.Runtime.Assets));
        }
        if (Preview is not null)
        {
            Panes = new[]
            {
                new AssetEditorPaneDefinition("preview", "Runtime Preview"),
            }
                .Concat(Panes).ToArray();
        }
    }

    public AssetDescriptor Asset => Document.Asset;
    public string EditorId => "ui-editor";
    public string Title => Document.Title;
    public string PrimaryPaneId => "design";
    public string PreviewElementId => "uiRuntimePreview";
    public bool IsDirty => Document.IsDirty;
    public bool IsReadOnly => Document.IsReadOnly;
    public bool SuppressMarkupRefresh => Document.IsGesturing && !Document.IsReadOnly;

    public IReadOnlyList<AssetEditorPaneDefinition> Panes { get; } =
    [
        new("design", "Design", false),
        new("elements", "Widget Tree"),
        new("controls", "Controls"),
        new("inspector", "Inspector"),
        new("style", "Styles"),
        new("diagnostics", "Diagnostics"),
        new("source", "Source"),
    ];

    public IReadOnlyList<AssetEditorCommandDefinition> Commands { get; } = new AssetEditorCommandDefinition[]
    {
        new("undo", "Undo", "Edit", 100, "reset", true),
        new("redo", "Redo", "Edit", 110, "reset", true),
        new("duplicate", "Duplicate Element", "Elements", 200, null, true),
        new("delete", "Delete Element", "Elements", 210, null, true),
        new("move-up", "Move Earlier", "Elements", 300),
        new("move-down", "Move Later", "Elements", 310),
    }.Concat(UiControlCatalog.All.Select((Control, Index) =>
        new AssetEditorCommandDefinition(UiControlCatalog.CommandId(Control), "Add " + Control.Label,
            "Elements", 100 + Index * 5, "add", Control.Id == "container"))).ToArray();

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
                    Ratio = 0.23f,
                    First = new DockNodeState
                    {
                        NodeId = "ui.left",
                        Kind = DockNodeKind.Split,
                        Orientation = DockSplitOrientation.Vertical,
                        Ratio = 0.42f,
                        First = Tabs("ui.controls", "ui.controls.tabs", "controls"),
                        Second = Tabs("ui.elements", "ui.elements.tabs", "elements"),
                    },
                    Second = new DockNodeState
                    {
                        NodeId = "ui.main",
                        Kind = DockNodeKind.Split,
                        Orientation = DockSplitOrientation.Horizontal,
                        Ratio = 0.74f,
                        First = new DockNodeState
                        {
                            NodeId = "ui.center",
                            Kind = DockNodeKind.Split,
                            Orientation = DockSplitOrientation.Vertical,
                            Ratio = 0.74f,
                            First = Tabs("ui.design", "ui.design.tabs",
                                Preview is null ? ["design"] : ["design", "preview"]),
                            Second = Tabs("ui.bottom", "ui.bottom.tabs", "style",
                                "diagnostics", "source"),
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
        if (UiControlCatalog.ForCommand(CommandId) is not null)
        {
            return !IsReadOnly;
        }
        return CommandId switch
        {
            "undo" => !IsReadOnly && Document.CanUndo,
            "redo" => !IsReadOnly && Document.CanRedo,
            "duplicate" or "delete" or "move-up" or "move-down" =>
                !IsReadOnly && Document.SelectedKey is not null,
            _ => false,
        };
    }

    public void ExecuteCommand(string CommandId)
    {
        var Control = UiControlCatalog.ForCommand(CommandId);
        if (Control is not null)
        {
            Report(Document.AddControl(Control.Id, out var Message), Message);
            return;
        }
        switch (CommandId)
        {
            case "undo":
                Document.Undo();
                break;
            case "redo":
                Document.Redo();
                break;
            case "duplicate":
                Report(Document.DuplicateSelected(out var DuplicateMessage),
                    DuplicateMessage);
                break;
            case "delete":
                Report(Document.DeleteSelected(out var DeleteMessage), DeleteMessage);
                break;
            case "move-up":
                Report(Document.MoveSelected(-1, out var UpMessage), UpMessage);
                break;
            case "move-down":
                Report(Document.MoveSelected(1, out var DownMessage), DownMessage);
                break;
            default:
                Context.SetStatus($"Unknown UI editor command '{CommandId}'");
                return;
        }
        if (!Document.IsGesturing)
        {
            Context.SetStatus($"{Document.Asset.Name} / revision {Document.Revision}");
        }
    }

    public bool TryHandleAction(string Action)
    {
        if (Action.StartsWith("editor.ui-control-add:", StringComparison.Ordinal))
        {
            Report(Document.AddControl(Action["editor.ui-control-add:".Length..], out var Message), Message);
            return true;
        }
        if (Action.StartsWith("editor.ui-control-property:", StringComparison.Ordinal))
        {
            var Payload = Action["editor.ui-control-property:".Length..];
            var Separator = Payload.IndexOf(':');
            if (Separator > 0)
            {
                Report(Document.SetControlProperty(Payload[..Separator], Payload[(Separator + 1)..], out var Message), Message);
            }
            return true;
        }
        if (Action.StartsWith("editor.ui-tree-search:", StringComparison.Ordinal))
        {
            Document.SetTreeSearch(Action["editor.ui-tree-search:".Length..]);
            MarkupRefreshRequested = true;
            return true;
        }
        if (Action.StartsWith("editor.ui-tree-toggle:", StringComparison.Ordinal))
        {
            Document.ToggleTree(Action["editor.ui-tree-toggle:".Length..]);
            MarkupRefreshRequested = true;
            return true;
        }
        if (Action is "editor.ui-tree-expand" or "editor.ui-tree-collapse")
        {
            Document.ExpandTree(Action == "editor.ui-tree-expand");
            MarkupRefreshRequested = true;
            return true;
        }
        const string PrivatePrefix = "editor.ui-private-style:";
        if (Action.StartsWith("editor.ui-private-rule-add:", StringComparison.Ordinal))
        {
            Report(Document.AddPrivateRule(Action["editor.ui-private-rule-add:".Length..], out var Message), Message);
            return true;
        }
        if (Action.StartsWith(PrivatePrefix, StringComparison.Ordinal))
        {
            var Payload = Action[PrivatePrefix.Length..];
            var First = Payload.IndexOf(':');
            var Second = First < 0 ? -1 : Payload.IndexOf(':', First + 1);
            var Index = -1;
            var Valid = First > 0 && int.TryParse(Payload[..First], out Index);
            if (Valid && Second > First)
            {
                var Name = Uri.UnescapeDataString(Payload[(First + 1)..Second]);
                Report(Document.SetPrivateStyleValue(Index, Name, Payload[(Second + 1)..], out var Message), Message);
            }
            return true;
        }
        if (Action.StartsWith("editor.ui-global-style-add:", StringComparison.Ordinal) ||
            Action.StartsWith("editor.ui-global-style-remove:", StringComparison.Ordinal))
        {
            var Add = Action.StartsWith("editor.ui-global-style-add:", StringComparison.Ordinal);
            var Prefix = Add ? "editor.ui-global-style-add:" : "editor.ui-global-style-remove:";
            Report(Document.SetGlobalStyleReference(Action[Prefix.Length..].Trim(), Add, out var Message), Message);
            return true;
        }
        if (Preview is not null && Preview.TryHandleAction(Action))
        {
            return true;
        }
        if (Action.StartsWith("editor.ui-use-asset:", StringComparison.Ordinal))
        {
            Report(Document.SetUseAsset(Action["editor.ui-use-asset:".Length..], out var Message), Message);
            return true;
        }
        if (Action is "editor.ui-enabled:1" or "editor.ui-enabled:0")
        {
            Report(Document.SetEnabled(Action.EndsWith(":1", StringComparison.Ordinal), out var Message), Message);
            return true;
        }
        if (Action.StartsWith("editor.ui-open-style:", StringComparison.Ordinal))
        {
            Context.Runtime.InvokeUiAction("editor.open-asset:" + Action["editor.ui-open-style:".Length..]);
            return true;
        }
        if (Action.StartsWith("editor.ui-add-use:", StringComparison.Ordinal))
        {
            Report(Document.AddUse(Action["editor.ui-add-use:".Length..], out var Message), Message);
            return true;
        }
        if (Action.StartsWith("editor.ui-open-use:", StringComparison.Ordinal))
        {
            Context.Runtime.InvokeUiAction("editor.open-asset:" + Action["editor.ui-open-use:".Length..]);
            return true;
        }
        if (Action.StartsWith("editor.ui-parameter:", StringComparison.Ordinal) ||
            Action.StartsWith("editor.ui-template-parameter:", StringComparison.Ordinal))
        {
            var Template = Action.StartsWith("editor.ui-template-parameter:", StringComparison.Ordinal);
            var Prefix = Template ? "editor.ui-template-parameter:" : "editor.ui-parameter:";
            var Payload = Action[Prefix.Length..];
            var Separator = Payload.IndexOf(':');
            if (Separator >= 0)
            {
                var Name = Uri.UnescapeDataString(Payload[..Separator]);
                Report(Document.SetParameter(Name, Payload[(Separator + 1)..], Template, out var Message), Message);
            }
            return true;
        }
        if (Action == "editor.ui-select-parent")
        {
            Document.SelectParent();
            return true;
        }
        if (Action == "editor.ui-duplicate")
        {
            ExecuteCommand("duplicate");
            return true;
        }
        if (Action == "editor.ui-move-up")
        {
            ExecuteCommand("move-up");
            return true;
        }
        if (Action == "editor.ui-move-down")
        {
            ExecuteCommand("move-down");
            return true;
        }
        if (Action == "editor.ui-indent")
        {
            Report(Document.IndentSelected(out var IndentMessage), IndentMessage);
            return true;
        }
        if (Action == "editor.ui-outdent")
        {
            Report(Document.OutdentSelected(out var OutdentMessage), OutdentMessage);
            return true;
        }
        if (Action == "editor.ui-drag-end")
        {
            Document.CommitGesture();
            MarkupRefreshRequested = true;
            Context.SetStatus(Document.IsDirty
                ? "Element moved / unsaved changes"
                : "Element geometry restored");
            return true;
        }
        if (Action is "editor.ui-drag-cancel" or "editor.ui-drag-discard")
        {
            Document.CancelGesture();
            MarkupRefreshRequested = true;
            Context.SetStatus("Element gesture canceled");
            return true;
        }
        if (Action.StartsWith("editor.ui-drag-start:", StringComparison.Ordinal))
        {
            BeginGesture(Action["editor.ui-drag-start:".Length..]);
            return true;
        }
        if (Action.StartsWith("editor.ui-drag:", StringComparison.Ordinal))
        {
            ApplyGesture(Action["editor.ui-drag:".Length..]);
            return true;
        }
        if (Action.StartsWith("editor.ui-select:", StringComparison.Ordinal))
        {
            Document.Select(Action["editor.ui-select:".Length..]);
            return true;
        }
        if (Action.StartsWith("editor.ui-add:", StringComparison.Ordinal))
        {
            var Tag = Action["editor.ui-add:".Length..];
            Report(Document.AddElement(Tag, out var Message), Message);
            return true;
        }
        if (Action.StartsWith("editor.ui-style-clear:", StringComparison.Ordinal))
        {
            var Name = Action["editor.ui-style-clear:".Length..];
            Report(Document.RemoveStyle(Name, out var Message), Message);
            return true;
        }
        if (Action.StartsWith("editor.ui-style:", StringComparison.Ordinal))
        {
            ApplyStyle(Action["editor.ui-style:".Length..]);
            return true;
        }
        if (Action.StartsWith("editor.ui-class-add:", StringComparison.Ordinal))
        {
            var Name = Uri.UnescapeDataString(Action["editor.ui-class-add:".Length..]);
            Report(Document.AddClass(Name, out var Message), Message);
            return true;
        }
        if (Action.StartsWith("editor.ui-class-remove:", StringComparison.Ordinal))
        {
            var Name = Uri.UnescapeDataString(Action["editor.ui-class-remove:".Length..]);
            Report(Document.RemoveClass(Name, out var Message), Message);
            return true;
        }
        if (Action.StartsWith("editor.ui-class-toggle:", StringComparison.Ordinal))
        {
            var Name = Uri.UnescapeDataString(Action["editor.ui-class-toggle:".Length..]);
            Report(Document.ToggleClass(Name, out var Message), Message);
            return true;
        }
        if (Action.StartsWith("editor.ui-field:", StringComparison.Ordinal))
        {
            ApplyField(Action["editor.ui-field:".Length..]);
            return true;
        }
        if (Action.StartsWith("editor.ui-change:", StringComparison.Ordinal))
        {
            // Documented legacy form kept for automation and saved editor state.
            ApplyField(Action["editor.ui-change:".Length..]);
            return true;
        }
        if (Action == "editor.ui-position:flex" ||
            Action == "editor.ui-position:absolute")
        {
            SetPositionMode(Action.EndsWith("absolute", StringComparison.Ordinal));
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
            "preview" => Preview is null
                ? "<div class=\"ui-editor-empty\">No project preview factory is registered for this asset.</div>"
                : "<div id=\"uiRuntimePreview\" class=\"ui-runtime-preview\"></div>",
            "design" => UiEditorMarkup.BuildCanvas(Document),
            "controls" => UiEditorMarkup.BuildControls(Document),
            "elements" => "<div class=\"ui-tree-panel\"><div class=\"ui-panel-title\">WIDGET TREE</div>" +
                UiEditorMarkup.BuildHierarchy(Document) + "</div>",
            "inspector" => "<div class=\"ui-panel-title\">UI INSPECTOR</div>" +
                "<div id=\"uiInspectorBody\" class=\"ui-element-inspector\">" +
                UiEditorMarkup.BuildInspector(Document) + "</div>",
            "style" => "<div class=\"ui-panel-title\">STYLES</div>" +
                BuildStyleLinks() + UiEditorMarkup.BuildStylePane(Document),
            "diagnostics" => "<div class=\"ui-panel-title\">DIAGNOSTICS</div>" +
                UiEditorMarkup.BuildDiagnostics(Document),
            "source" => UiEditorMarkup.BuildSource(Document),
            _ => string.Empty,
        };
    }

    public void Save()
    {
        Document.Save();
        MarkupRefreshRequested = true;
    }

    public void Update(float DeltaSeconds)
    {
        _ = DeltaSeconds;
        MarkupRefreshRequested |= Document.RefreshChangedDependencies();
    }

    private string BuildStyleLinks()
    {
        var Assets = Context.Runtime.Assets;
        if (Assets is null)
        {
            return string.Empty;
        }
        var Paths = Document.Sheet.Files.Select(FileValue => System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Context.Path)!, FileValue)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var Builder = new System.Text.StringBuilder("<div class=\"ui-element-tools\">");
        foreach (var Asset in Assets.Assets.Where(Asset => Asset.Type is "StyleSheet" or "GlobalStyleSheet"))
        {
            var PathValue = Assets.ResolvePath(Asset);
            if (PathValue is not null && Paths.Contains(System.IO.Path.GetFullPath(PathValue)))
            {
                Builder.Append("<button class=\"ui-tool-button\" action=\"editor.ui-open-style:")
                    .Append(WebUtility.HtmlEncode(Asset.Id)).Append("\">Edit ")
                    .Append(WebUtility.HtmlEncode(Asset.Name)).Append("</button>");
            }
        }
        return Builder.Append("</div>").ToString();
    }

    public bool ConsumeMarkupRefreshRequest()
    {
        var Requested = MarkupRefreshRequested;
        MarkupRefreshRequested = false;
        return Requested;
    }

    public IReadOnlyList<AssetEditorLiveProperty> DrainLiveProperties()
    {
        if (PendingLiveProperties.Count == 0)
        {
            return Array.Empty<AssetEditorLiveProperty>();
        }
        var Result = PendingLiveProperties.ToArray();
        PendingLiveProperties.Clear();
        return Result;
    }

    public JsonObject Describe()
    {
        var State = Document.Describe();
        State["editorRootId"] = Context.EditorRootId;
        State["viewModelContractAvailable"] = Document.ViewModelContract.Length == 0 ||
            Context.Runtime.UiRuntime.ViewModelContracts.Any(Contract =>
                Contract.Key.Equals(Document.ViewModelContract, StringComparison.Ordinal));
        State["panes"] = new JsonArray(Panes.Select(Pane => (JsonNode)Pane.PaneId).ToArray());
        State["runtimePreview"] = Preview?.Describe();
        return State;
    }

    public EditorUiPreviewDocument? BuildPreviewDocument()
    {
        return Preview?.BuildDocument(new EditorUiPreviewSource(Asset.Id, Context.Path,
            Document.SerializeForSave(), Document.Revision, Document.DependencyRevision));
    }

    public void Dispose()
    {
        PendingLiveProperties.Clear();
        Preview?.Dispose();
    }

    private void BeginGesture(string Payload)
    {
        var Separator = Payload.IndexOf('|');
        var Handle = Separator < 0 ? Payload : Payload[..Separator];
        var NodeKey = Separator < 0 ? string.Empty : Payload[(Separator + 1)..];
        if (NodeKey.Length != 0 && !Document.Select(NodeKey))
        {
            Context.SetStatus($"Unknown design node '{NodeKey}'");
            return;
        }
        var Bounds = CanvasRelativeSelectionRect();
        var Started = Document.BeginGesture(Handle, Bounds.X, Bounds.Y, Bounds.Width,
            Bounds.Height, out var Message);
        MarkupRefreshRequested = false;
        if (!Started)
        {
            if (Message.Length != 0)
            {
                Context.SetStatus(Message);
            }
            return;
        }
        Context.SetStatus($"Drag <{Document.SelectedTag}>; Shift snaps to 10px, release to commit");
    }

    /// <summary>Selection bounds in design-canvas space. The document stores left/top
    /// relative to the authored screen, so window coordinates must be rebased on the
    /// canvas origin before they become authored offsets.</summary>
    private (int X, int Y, int Width, int Height) CanvasRelativeSelectionRect()
    {
        if (!Context.TryGetElementRect(SelectedPreviewId, out var Element))
        {
            return (0, 0, 0, 0);
        }
        var OriginX = 0f;
        var OriginY = 0f;
        if (Context.TryGetElementRect("uiDesignCanvas", out var Canvas))
        {
            OriginX = Canvas.X;
            OriginY = Canvas.Y;
        }
        return ((int)MathF.Round(Element.X - OriginX),
            (int)MathF.Round(Element.Y - OriginY),
            (int)MathF.Round(Element.Width), (int)MathF.Round(Element.Height));
    }

    private void ApplyGesture(string Payload)
    {
        var Parts = Payload.Split(':', 4);
        if (Parts.Length != 4 ||
            !int.TryParse(Parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var DeltaX) ||
            !int.TryParse(Parts[2], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var DeltaY))
        {
            return;
        }
        var Snap = Parts[3] == "snap";
        var Applied = Parts[0] == "move"
            ? Document.ApplyGestureMove(Parts[0], DeltaX, DeltaY, Snap, out var Message)
            : Document.ApplyGestureResize(Parts[0], DeltaX, DeltaY, Snap, out Message);
        if (!Applied)
        {
            if (Message.Length != 0)
            {
                Context.SetStatus(Message);
            }
            return;
        }
        PublishLiveStyle();
        Context.SetStatus($"Drag {Parts[0]} {DeltaX}, {DeltaY} px" +
            (Snap ? " (10px steps)" : string.Empty));
    }

    private void PublishLiveStyle()
    {
        var ElementId = SelectedPreviewId;
        if (ElementId.Length == 0)
        {
            return;
        }
        foreach (var (Name, Value) in Document.LiveStyleProperties())
        {
            PendingLiveProperties.Add(new(ElementId, Name, Value));
        }
    }

    private string SelectedPreviewId =>
        Document.SelectedElement() is { } Element
            ? UiEditorDocument.PreviewNodeIdOf(Element)
            : string.Empty;

    private void ApplyStyle(string Payload)
    {
        var Separator = Payload.IndexOf(':');
        if (Separator <= 0)
        {
            return;
        }
        var Name = Payload[..Separator];
        var Value = Payload[(Separator + 1)..];
        Report(Document.SetStyle(Name, Value, out var Message), Message);
    }

    private void ApplyField(string Payload)
    {
        var Separator = Payload.IndexOf(':');
        if (Separator <= 0)
        {
            return;
        }
        var Field = Payload[..Separator];
        var Value = Payload[(Separator + 1)..];
        var Changed = Field switch
        {
            "name" => Document.SetAttribute("data-ui-name", Value.Trim(), out var NameMessage)
                ? ReportMessage(true, NameMessage) : ReportMessage(false, NameMessage),
            "id" => Document.RenameId(Value, out var IdMessage)
                ? ReportMessage(true, IdMessage) : ReportMessage(false, IdMessage),
            "text" => Document.SetText(Value, out var TextMessage)
                ? ReportMessage(true, TextMessage) : ReportMessage(false, TextMessage),
            "class" => Document.SetAttribute("class", Value, out var ClassMessage)
                ? ReportMessage(true, ClassMessage) : ReportMessage(false, ClassMessage),
            "binding" or "data-bind" => Document.SetAttribute("data-bind", Value,
                out var BindMessage)
                ? ReportMessage(true, BindMessage) : ReportMessage(false, BindMessage),
            "command" or "data-command" => Document.SetAttribute("data-command", Value,
                out var CommandMessage)
                ? ReportMessage(true, CommandMessage) : ReportMessage(false, CommandMessage),
            "view-model" => Document.SetViewModel(Value, out var ViewModelMessage)
                ? ReportMessage(true, ViewModelMessage)
                : ReportMessage(false, ViewModelMessage),
            "class-add" => Document.AddClass(Value, out var AddMessage)
                ? ReportMessage(true, AddMessage) : ReportMessage(false, AddMessage),
            _ => UnknownField(Field),
        };
        if (!Changed)
        {
            return;
        }
        MarkupRefreshRequested = true;
        Context.SetStatus($"{Document.Asset.Name} / revision {Document.Revision}");
    }

    private bool ReportMessage(bool Changed, string Message)
    {
        if (!Changed && Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
        return Changed;
    }

    private bool UnknownField(string Field)
    {
        Context.SetStatus($"Unknown UI field '{Field}'");
        return false;
    }

    private void SetPositionMode(bool Absolute)
    {
        var Bounds = CanvasRelativeSelectionRect();
        var X = Math.Max(0, Bounds.X);
        var Y = Math.Max(0, Bounds.Y);
        Report(Document.SetPositionMode(Absolute, X, Y, out var Message), Message);
    }

    private void Report(bool Changed, string Message)
    {
        if (Changed)
        {
            Context.SetStatus($"{Document.Asset.Name} / revision {Document.Revision}");
        }
        else if (Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
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
