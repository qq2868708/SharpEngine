using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.World;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private sealed class SceneDocumentTab(WorldDocument world, WorldAuthoringSession session,
        EditorSelection selection, bool untitled, string? assetId, bool dirty)
    {
        public WorldDocument World = world;
        public WorldAuthoringSession Session = session;
        public EditorSelection Selection = selection;
        public bool Untitled = untitled;
        public string? AssetId = assetId;
        public bool Dirty = dirty;
    }
    private SceneDocumentTab? _mainWorldTab;
    private readonly Dictionary<string, SceneDocumentTab> _prefabTabs = new(StringComparer.OrdinalIgnoreCase);
    private string? _activePrefabPath;
    private bool _prefabWindowOpen;
    private SceneDocumentTab CaptureDocumentTab() => new(_world!, _authoring!, _selection, _worldIsUntitled, _worldAssetId, _dirty);
    private void RememberDocumentTab()
    {
        if (_activePrefabPath is null) _mainWorldTab = CaptureDocumentTab();
        else _prefabTabs[_activePrefabPath] = CaptureDocumentTab();
    }
    private void RestoreDocumentTab(SceneDocumentTab tab)
    {
        _world = tab.World; _authoring = tab.Session; _selection = tab.Selection;
        _worldIsUntitled = tab.Untitled; _worldAssetId = tab.AssetId; _dirty = tab.Dirty;
        _inspectorEditing = false; _componentMarkup = "";
        RefreshLists(); RefreshInspector(); RefreshWorldTitle(); RefreshPrefabEditor();
    }
    private void ActivateMainWorld()
    {
        if (_activePrefabPath is null || _mainWorldTab is null) return;
        RememberDocumentTab(); _activePrefabPath = null; RestoreDocumentTab(_mainWorldTab);
    }
    public void OpenPrefab(string path)
    {
        if (PlayState != EditorPlayState.Stopped) throw new InvalidOperationException("Stop Play before editing a Prefab");
        path = Path.GetFullPath(path);
        if (!_prefabTabs.TryGetValue(path, out var tab))
        {
            var world = WorldDocument.Load(path, Components);
            if (!world.Data.IsPrefab) throw new InvalidDataException("Expected Prefab document");
            tab = new(world, new WorldAuthoringSession(world), new(EditorSelectionKind.Entity, world.Entities[0].Id), false, null, false);
            _prefabTabs.Add(path, tab);
        }
        RememberDocumentTab(); _activePrefabPath = path;
        if (!_prefabWindowOpen) _prefabWindowOpen = _nativeApi.CreateDockWindow("prefab-editor", "Prefab Editor") != 0;
        RestoreDocumentTab(tab);
    }
    private bool SaveActivePrefab()
    {
        if (_activePrefabPath is null) return false;
        try
        {
            _authoring!.Save(_activePrefabPath); _dirty = false;
            RememberDocumentTab(); RefreshWorldTitle(); RefreshPrefabEditor();
            SetStatus("Prefab saved: " + _activePrefabPath); return true;
        }
        catch (Exception error) { SetStatus("Prefab save failed: " + error.Message); return false; }
    }
    private bool HandlePrefabAction(string action)
    {
        if (!action.StartsWith("editor.prefab-", StringComparison.Ordinal)) return false;
        try
        {
            if (action == "editor.prefab-world") { ActivateMainWorld(); return true; }
            if (action == "editor.prefab-save") { SaveActivePrefab(); return true; }
            if (action.StartsWith("editor.prefab-open:")) { OpenPrefab(Uri.UnescapeDataString(action[19..])); return true; }
            if (action.StartsWith("editor.prefab-select:")) { SelectEntity(action[21..]); RefreshPrefabEditor(); return true; }
            if (action.StartsWith("editor.prefab-instantiate:"))
            {
                var parts = action[26..].Split('|', 2);
                var asset = _assets!.Find(Uri.UnescapeDataString(parts[0])) ?? throw new InvalidOperationException("Prefab asset missing");
                var path = _assets.ResolvePath(asset)!;
                var prefab = _prefabTabs.TryGetValue(path, out var tab) ? tab.World.Data.Clone() : WorldDocument.Load(path, Components).Data;
                var parent = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
                if (_runtimeWorld is { } runtime) { var created = runtime.Instantiate(prefab, parent).Value; _world!.Replace(runtime.Snapshot()); _selection = new(EditorSelectionKind.Entity, created.EntityId); }
                else
                {
                    string? root = null;
                    var result = _authoring!.Edit("prefab.instantiate", ComponentRevision, data =>
                    { var records = data.Instantiate(prefab, Components!, parent); root = records.Single(e => e.ParentId == parent).EntityId; }, []);
                    if (!result.Success) throw new InvalidOperationException(result.Message);
                    _selection = new(EditorSelectionKind.Entity, root);
                }
                RefreshLists(); RefreshInspector(); RefreshWorldTitle(); RefreshPrefabEditor(); return true;
            }
            if (action is "editor.prefab-new" or "editor.prefab-export")
            {
                if (PlayState != EditorPlayState.Stopped) throw new InvalidOperationException("Stop Play before saving a Prefab asset");
                SceneData data;
                if (action == "editor.prefab-export") data = _world!.Data.ExportPrefab(Components!, SelectedEntityId ?? throw new InvalidOperationException("Select an entity"));
                else { data = new SceneData { Name = "New Prefab", IsPrefab = true }; data.CreateEntity(Components!, "Root"); }
                var directory = Path.Combine(_projectRoot ?? _engineRoot, "Content", "Prefabs");
                Directory.CreateDirectory(directory);
                var name = string.Concat(data.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
                var path = Path.Combine(directory, name + ".sprefab");
                for (int suffix = 1; File.Exists(path); suffix++) path = Path.Combine(directory, name + suffix + ".sprefab");
                new WorldDocument(data, Components!, path).Save(path);
                try { RegisterPrefabAsset(path, data.Name); }
                catch { File.Delete(path); throw; }
                OpenPrefab(path); return true;
            }
        }
        catch (Exception error) { SetStatus("Prefab: " + error.Message); SetText("componentError", error.Message); }
        return true;
    }
    private void RegisterPrefabAsset(string path, string name)
    {
        var root = _projectRoot ?? _engineRoot;
        var ownerManifest = JsonNode.Parse(File.ReadAllText(_projectManifest ?? Path.Combine(_engineRoot, "engine.json")))!;
        var manifestPath = Path.Combine(root, ownerManifest["assets"]?["manifest"]?.GetValue<string>() ?? "Content/Assets/assets.json");
        var json = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var id = "prefabs/" + Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        var assets = json["assets"]!.AsArray();
        if (assets.Any(a => a!["id"]!.GetValue<string>() == id)) throw new InvalidOperationException("Prefab asset ID already exists");
        assets.Add(new JsonObject { ["id"] = id, ["type"] = "Prefab", ["name"] = name,
            ["path"] = relative, ["importer"] = "SharpEngine.PrefabJson", ["tags"] = new JsonArray("prefab") });
        var staged = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(staged, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); File.Move(staged, manifestPath, true); }
        finally { if (File.Exists(staged)) File.Delete(staged); }
        _assets = AssetRegistry.Load(_engineRoot, _projectRoot, _projectManifest);
        var contentStates = _contentManagers?.Capture() ?? EditorLayoutDocument.CreateDefaultContentManagers();
        var activeContent = _contentManagers?.ActivePaneId ?? EditorPaneIds.Content;
        _contentManagers = new ContentManagerCoordinator(_assets, contentStates, activeContent);
        RefreshAssets();
    }
    private void RefreshPrefabEditor()
    {
        if (!_prefabWindowOpen) return;
        var markup = new StringBuilder("<div class=\"prefab-editor\"><div class=\"prefab-tabs\"><button data-action=\"editor.prefab-world\">World</button>");
        foreach (var (path, tab) in _prefabTabs)
            markup.Append("<button data-action=\"editor.prefab-open:").Append(Uri.EscapeDataString(path)).Append("\">").Append(Escape(tab.World.Name)).Append(tab.Session.IsDirty ? " *" : "").Append("</button>");
        markup.Append("</div><div class=\"pane-toolbar\"><button data-action=\"editor.prefab-new\">New Prefab</button><button data-action=\"editor.prefab-save\">Save</button><button data-action=\"editor.component-create-entity\">+ Entity</button><button data-action=\"editor.command:core.undo\">Undo</button><button data-action=\"editor.command:core.redo\">Redo</button></div>");
        if (_activePrefabPath is not null && _world is not null)
            foreach (var entity in _world.Entities)
                markup.Append("<button class=\"prefab-entity\" data-action=\"editor.prefab-select:").Append(entity.Id).Append("\">").Append(Escape(entity.Name)).Append("</button>");
        markup.Append("<div class=\"muted\">Select an entity to edit its components in Inspector.</div></div>");
        _nativeApi.SetHostMarkup("prefab-editor", "workspace", markup.ToString());
    }
}
