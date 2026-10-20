using SharpEngine.World;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private void OpenWorldAsset(EditorAssetOpenContext Context)
    {
        if (PlayState != EditorPlayState.Stopped)
        {
            throw new InvalidOperationException("Stop Play before opening another World");
        }
        if (_worldAssetId?.Equals(Context.Asset.Id,
                StringComparison.OrdinalIgnoreCase) == true)
        {
            SetStatus($"World already open · {Context.Asset.Name}");
            return;
        }
        if (!ConfirmCurrentWorldReplacement())
        {
            throw new EditorAssetOpenCanceledException("World switch was cancelled");
        }

        var Registry = Components ?? throw new InvalidOperationException(
            "Component registry is unavailable");
        var Replacement = WorldDocument.Load(Context.Path, Registry);
        _world = Replacement;
        _worldAssetId = Context.Asset.Id;
        _worldIsUntitled = false;
        _authoring = new WorldAuthoringSession(Replacement);
        _dirty = false;
        _selection = Replacement.Entities.Count == 0 ? EditorSelection.Scene :
            new EditorSelection(EditorSelectionKind.Entity, Replacement.Entities[0].Id);
        _mainWorldTab = null;
        _activePrefabPath = null;
        RefreshLists();
        RefreshInspector();
        RefreshWorldTitle();
        RefreshViewportModeUi();
        MarkExtensionUiDirty();
        SetStatus($"Loaded World · {Replacement.Name}");
    }

    private bool ConfirmCurrentWorldReplacement()
    {
        if (!IsDirty)
        {
            return true;
        }

        var Choice = NativeEngineApi.PromptUnsavedWorld(_windowHandle, WorldTabTitle());
        if (Choice == UnsavedWorldChoice.Cancel)
        {
            return false;
        }
        if (Choice == UnsavedWorldChoice.Save)
        {
            SaveWorld();
            return !IsDirty;
        }
        return true;
    }
}
