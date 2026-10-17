using System.Net;
using System.Text;
using System.Text.Json;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private readonly List<UiEditorDocument> _uiEditorDocuments = [];
    private string? _activeUiDocumentId;

    public string? ActiveUiDocumentId => _activeUiDocumentId;
    public int OpenUiDocumentCount => _uiEditorDocuments.Count;

    public string DescribeUiEditorJson() => JsonSerializer.Serialize(
        DescribeUiEditorState(), new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

    private object DescribeUiEditorState() => new
    {
        schemaVersion = 1,
        activeDocument = _activeUiDocumentId,
        documents = _uiEditorDocuments.Select(document => new
        {
            assetId = document.Asset.Id,
            title = document.Title,
            path = document.Path,
            readOnly = document.IsReadOnly,
            dirty = document.IsDirty,
            revision = document.Revision,
            selectedElement = document.SelectedElementId,
            viewModelContract = document.ViewModelContract,
            viewModelContractAvailable = IsViewModelContractAvailable(document),
            diagnostics = document.Diagnostics.Select(diagnostic => new
            {
                diagnostic.Severity,
                diagnostic.Code,
                diagnostic.Message,
            }),
        }),
    };

    private bool IsViewModelContractAvailable(UiEditorDocument document) =>
        document.ViewModelContract.Length == 0 ||
        _uiRuntime.ViewModelContracts.Any(contract => contract.Key.Equals(
            document.ViewModelContract, StringComparison.Ordinal));

    private UiEditorDocument? ActiveUiDocument() => _uiEditorDocuments.FirstOrDefault(
        document => string.Equals(document.Asset.Id, _activeUiDocumentId,
            StringComparison.OrdinalIgnoreCase));

    private void OpenUiAsset(AssetDescriptor asset)
    {
        if (_assets is null || !asset.Type.Equals("RmlDocument",
                StringComparison.OrdinalIgnoreCase)) return;
        var path = _assets.ResolvePath(asset);
        if (path is null)
        {
            SetStatus($"UI Editor could not resolve {asset.Id}");
            return;
        }
        var document = _uiEditorDocuments.FirstOrDefault(value =>
            value.Asset.Id.Equals(asset.Id, StringComparison.OrdinalIgnoreCase));
        if (document is null)
        {
            try
            {
                var ownerRoot = asset.Source == AssetSource.Project
                    ? _projectRoot ?? _engineRoot : _engineRoot;
                document = UiEditorDocument.Load(asset, path, ownerRoot);
                _uiEditorDocuments.Add(document);
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException or InvalidDataException)
            {
                SetStatus($"UI Editor could not open {asset.Name}: {exception.Message}");
                return;
            }
        }
        _activeUiDocumentId = document.Asset.Id;
        ShowUiEditorPane();
        RefreshUiEditor();
        SetStatus($"UI Editor · {document.Asset.Name} · 1280 x 720");
    }

    private void ShowUiEditorPane()
    {
        if (_layout is null) return;
        if (!IsPaneVisible(EditorPaneIds.UiEditor))
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.ReopenAsFloating,
                EditorPaneIds.UiEditor));
        var location = FindPaneGroup(EditorPaneIds.UiEditor);
        if (location.HostId is not null && location.GroupId is not null)
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.ActivateTab,
                EditorPaneIds.UiEditor, TargetGroupId: location.GroupId,
                TargetHostId: location.HostId));
    }

    private bool HandleUiEditorAction(string action)
    {
        if (!action.StartsWith("editor.ui-", StringComparison.Ordinal)) return false;
        if (action.StartsWith("editor.ui-activate:", StringComparison.Ordinal))
        {
            var assetId = action["editor.ui-activate:".Length..];
            if (_uiEditorDocuments.Any(document => document.Asset.Id.Equals(assetId,
                    StringComparison.OrdinalIgnoreCase)))
                _activeUiDocumentId = assetId;
            RefreshUiEditor();
            return true;
        }
        if (action.StartsWith("editor.ui-close:", StringComparison.Ordinal))
        {
            var assetId = action["editor.ui-close:".Length..];
            var document = _uiEditorDocuments.FirstOrDefault(value => value.Asset.Id.Equals(
                assetId, StringComparison.OrdinalIgnoreCase));
            if (document?.IsDirty == true)
            {
                SetStatus($"Save {document.Asset.Name} before closing it");
                return true;
            }
            if (document is not null) _uiEditorDocuments.Remove(document);
            if (string.Equals(_activeUiDocumentId, assetId,
                    StringComparison.OrdinalIgnoreCase))
                _activeUiDocumentId = _uiEditorDocuments.LastOrDefault()?.Asset.Id;
            RefreshUiEditor();
            return true;
        }
        var active = ActiveUiDocument();
        if (active is null)
        {
            SetStatus("Open an RML asset before using UI Editor tools");
            return true;
        }
        string message;
        var changed = false;
        if (action.StartsWith("editor.ui-select:", StringComparison.Ordinal))
            changed = active.Select(action["editor.ui-select:".Length..]);
        else if (action.StartsWith("editor.ui-change:", StringComparison.Ordinal))
        {
            var payload = action["editor.ui-change:".Length..];
            var separator = payload.IndexOf(':');
            if (separator > 0)
                changed = active.SetField(payload[..separator], payload[(separator + 1)..],
                    out message);
            else message = "Malformed UI property change";
            if (!changed && message.Length != 0) SetStatus(message);
        }
        else if (action.StartsWith("editor.ui-add:", StringComparison.Ordinal))
        {
            changed = active.AddElement(action["editor.ui-add:".Length..], out message);
            if (!changed && message.Length != 0) SetStatus(message);
        }
        else if (action == "editor.ui-delete")
        {
            changed = active.DeleteSelected(out message);
            if (!changed && message.Length != 0) SetStatus(message);
        }
        else if (action == "editor.ui-position:flex" ||
                 action == "editor.ui-position:absolute")
        {
            changed = active.SetPositionMode(action.EndsWith("absolute",
                StringComparison.Ordinal), out message);
            if (!changed && message.Length != 0) SetStatus(message);
        }
        else if (action == "editor.ui-undo") changed = active.Undo();
        else if (action == "editor.ui-redo") changed = active.Redo();
        else if (action == "editor.ui-save")
        {
            try
            {
                active.Save();
                changed = true;
                SetStatus($"Saved UI document {active.Asset.Name}");
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException)
            {
                SetStatus($"UI document was not saved: {exception.Message}");
            }
        }
        RefreshUiEditor();
        if (changed && action != "editor.ui-save")
            SetStatus($"UI Editor · {active.Asset.Name} · revision {active.Revision}");
        return true;
    }

    private void RefreshUiEditor()
    {
        if (!_nativeApi.HasDocumentUi) return;
        var active = ActiveUiDocument();
        var tabs = new StringBuilder();
        foreach (var document in _uiEditorDocuments)
        {
            var selected = ReferenceEquals(document, active) ? " active" : string.Empty;
            tabs.Append("<button class=\"ui-document-tab").Append(selected)
                .Append("\" action=\"editor.ui-activate:")
                .Append(EscapeUi(document.Asset.Id)).Append("\"><img class=\"icon\" src=\"")
                .Append(EditorResourceUris.IconSource("ui")).Append("\" /><span>")
                .Append(EscapeUi(document.Title)).Append("</span><span class=\"ui-document-close\" data-action=\"editor.ui-close:")
                .Append(EscapeUi(document.Asset.Id)).Append("\">x</span></button>");
        }
        if (tabs.Length == 0)
            tabs.Append("<div class=\"ui-editor-empty-tab\">Open an RML asset from Content Manager</div>");
        _nativeApi.SetUiMarkup("uiDocumentTabs", tabs.ToString());
        if (active is null)
        {
            _nativeApi.SetUiMarkup("uiElementHierarchy",
                "<div class=\"ui-editor-empty\">No UI document open</div>");
            _nativeApi.SetUiMarkup("uiEditorCanvas",
                "<div class=\"ui-editor-empty-canvas\"><div class=\"ui-empty-icon\">UI</div><div>Open a project RML asset to begin</div></div>");
            _nativeApi.SetUiMarkup("uiElementInspector",
                "<div class=\"ui-editor-empty\">Select an element</div>");
            SetText("uiEditorDiagnostics", "No document loaded");
            return;
        }
        _nativeApi.SetUiMarkup("uiElementHierarchy", active.BuildHierarchyMarkup());
        _nativeApi.SetUiMarkup("uiEditorCanvas", active.BuildPreviewMarkup());
        _nativeApi.SetUiMarkup("uiElementInspector", active.BuildInspectorMarkup());
        var errors = active.Diagnostics.Count(value => value.Severity == "error");
        var warnings = active.Diagnostics.Count(value => value.Severity == "warning") +
            (IsViewModelContractAvailable(active) ? 0 : 1);
        var firstMessage = active.Diagnostics.FirstOrDefault()?.Message ??
            $"ViewModel contract '{active.ViewModelContract}' is not registered by a loaded Runtime Module";
        SetText("uiEditorDiagnostics", errors == 0 && warnings == 0
            ? $"Ready · {active.Asset.RelativePath}"
            : $"{errors} errors · {warnings} warnings · {firstMessage}");
    }

    private static string EscapeUi(string value) => WebUtility.HtmlEncode(value);
}
