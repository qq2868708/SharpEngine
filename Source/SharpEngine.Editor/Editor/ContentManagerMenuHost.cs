namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_contentManagers))]
    private void SetContentManagers(ContentManagerCoordinator Managers)
    {
        _contentManagers?.Dispose();
        _contentManagers = Managers;
        AttachContentCommands(Managers);
        Managers.AttachUi(new ContentManagerMenuHost(this), _commands, _contextMenus, PopupMenus, ContextMenuRouting);
    }

    /// <summary>Connects the browser feature to editor selection, layout and asset services.</summary>
    private sealed class ContentManagerMenuHost(EditorRuntime Runtime) : IContentManagerMenuHost
    {
        public string? ActivePaneId
        {
            get
            {
                return Runtime._contentManagers?.ActivePaneId;
            }
        }

        public void ActivateContentPane(string PaneId)
        {
            Runtime._contentManagers?.Activate(PaneId);
        }

        public string? FindContentHost(string PaneId)
        {
            return Runtime.IsPaneVisible(PaneId) ? Runtime.FindPaneGroup(PaneId).HostId : null;
        }

        public EditorCommandContext BindContentContext(EditorSelection Selection, string PaneId, string HostId)
        {
            return new EditorCommandContext(Runtime, Selection, PaneId, HostId);
        }

        public void ContentSelectionChanged(string Status, bool Rebuild = false)
        {
            Runtime.ApplyActiveContentSelection();
            Runtime.CommitContentManagerState(Status, Rebuild);
        }

        public bool CanOpenContentAsset(string AssetId)
        {
            var Asset = Runtime._assets?.Find(AssetId);
            return Asset is not null && File.Exists(Runtime._assets!.ResolvePath(Asset)) &&
                Runtime.AssetOpeners.Resolve(Asset) is not null;
        }

        public void OpenContentAsset(string AssetId) { Runtime.OpenAsset(AssetId); }
    }
}
