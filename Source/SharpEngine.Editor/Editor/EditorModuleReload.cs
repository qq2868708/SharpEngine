using SharpEngine.Settings;
using System.Reflection;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.World;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    public bool ProjectCodeAvailable { get; private set; } = true;
    internal bool CanApplyProjectCode => PlayState == EditorPlayState.Stopped &&
        !_inspectorEditing && _inspectorGesture is null && !_gizmoDragging;
    internal Action? RequestProjectBuild { get; set; }
    private string _projectBuildState = "Idle", _projectBuildMessage = "";
    private ulong _projectCodeRevision;
    public string DescribeProjectCodeJson() => System.Text.Json.JsonSerializer.Serialize(new
    {
        schemaVersion = 1, stateRevision = _projectCodeRevision, state = _projectBuildState,
        message = _projectBuildMessage, available = ProjectCodeAvailable,
        pendingUntilStop = _projectBuildState == "Ready" && PlayState != EditorPlayState.Stopped,
        pendingUntilInputCommit = _projectBuildState == "Ready" && PlayState == EditorPlayState.Stopped && !CanApplyProjectCode,
    });
    internal void SetProjectBuildStatus(string state, string message)
    {
        _projectBuildState = state; _projectBuildMessage = message; _projectCodeRevision++;
        SetStatus(message);
    }

    internal ModuleReloadPreparation PrepareModuleReload(IEnumerable<(Assembly Assembly, string Id, string Type, string Hash)> Modules)
    {
        if (PlayState != EditorPlayState.Stopped) throw new InvalidOperationException("Stop Play before applying project code");
        var Candidates = Modules.ToArray();
        SubsystemLifetime.Registry.ValidateModules(Candidates.Select(Module => (Module.Assembly, Module.Id, Module.Type)));
        var registry = ComponentRegistry.CreateBuiltin();
        registry.AssetExists = (id, type) => _assets?.Find(id) is { Exists: true } asset &&
            EditorAssetReferenceTypes.Matches(asset.Type, type);
        var settings = new DevelopmentSettingsRegistry(_projectRoot, _engineRoot, install: false);
        try
        {
            settings.RegisterAssembly(typeof(EditorRuntime).Assembly, SettingsScope.Engine);
            foreach (var Module in Candidates)
            {
                if (Module.Type == "Runtime") registry.RegisterAssembly(Module.Assembly, Module.Id, Module.Hash);
                settings.RegisterAssembly(Module.Assembly, SettingsScope.Project);
            }
            var DiscoveredDataAssetTypes = DataAssetTypeRegistry.Discover(
                Candidates.Where(Module => Module.Type == "Runtime")
                    .Select(Module => Module.Assembly).ToArray());
            registry.ValidateRegistrations();
            if (_developmentSettings is not null) settings.CopyStateFrom(_developmentSettings);
            var sessions = _prefabTabs.Values.Select(tab => tab.Session)
                .Concat(new[] { _authoring, _mainWorldTab?.Session }.OfType<WorldAuthoringSession>())
                .Distinct().ToArray();
            foreach (var session in sessions) session.ValidateRegistry(registry);
            return new ModuleReloadPreparation(this, registry, settings, sessions,
                DiscoveredDataAssetTypes);
        }
        catch { settings.Dispose(); throw; }
    }

    internal void SetProjectCodeAvailable(bool available, string message)
    {
        ProjectCodeAvailable = available;
        _projectCodeRevision++;
        MarkExtensionUiDirty();
        RefreshComponentInspector(force: true);
        RefreshSettingsUi();
        SetStatus(message);
    }

    // Preflight is read-only. Committing descriptors does not replace documents,
    // revisions, selections, savepoints, or Undo/Redo history.
    internal sealed class ModuleReloadPreparation(EditorRuntime editor, ComponentRegistry registry,
        DevelopmentSettingsRegistry settings, WorldAuthoringSession[] sessions,
        DataAssetTypeRegistry DataAssetTypes) : IDisposable
    {
        private bool _accepted;
        private bool _disposed;
        private readonly Action[] _restore = sessions.Select(session => session.CaptureReloadRestorePoint()).ToArray();
        private readonly DevelopmentSettingsRegistry? _previousSettings = editor._developmentSettings;
        private readonly Action? _restoreSettings = editor._developmentSettings?.CaptureReloadRestorePoint();
        private readonly WorldDocument? _previousWorld = editor._world;
        private readonly WorldAuthoringSession? _previousAuthoring = editor._authoring;
        private readonly EditorSelection _previousSelection = editor._selection;
        private readonly bool _previousDirty = editor._dirty;
        private readonly SceneDocumentTab? _previousMainTab = editor._mainWorldTab;
        private readonly Dictionary<string, SceneDocumentTab> _previousPrefabTabs = new(editor._prefabTabs, StringComparer.OrdinalIgnoreCase);
        private readonly string? _previousPrefabPath = editor._activePrefabPath;
        private readonly bool _previousUntitled = editor._worldIsUntitled;
        private readonly string? _previousAssetId = editor._worldAssetId;
        private readonly DataAssetTypeRegistry PreviousDataAssetTypes = editor.DataAssetTypes;
        public void Commit()
        {
            foreach (var session in sessions) session.Document.Registry = registry;
            editor.DataAssetTypes = DataAssetTypes;
            editor._developmentSettings = settings;
            settings.Activate();
        }
        public void Accept()
        {
            _accepted = true;
            _previousSettings?.Dispose();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_accepted) return;
            foreach (var restore in _restore) restore();
            _restoreSettings?.Invoke();
            editor.DataAssetTypes = PreviousDataAssetTypes;
            editor._developmentSettings = _previousSettings;
            editor._world = _previousWorld; editor._authoring = _previousAuthoring;
            editor._selection = _previousSelection; editor._dirty = _previousDirty;
            editor._mainWorldTab = _previousMainTab; editor._activePrefabPath = _previousPrefabPath;
            editor._worldIsUntitled = _previousUntitled; editor._worldAssetId = _previousAssetId;
            editor._prefabTabs.Clear();
            foreach (var pair in _previousPrefabTabs) editor._prefabTabs.Add(pair.Key, pair.Value);
            settings.Dispose();
        }
    }
}
