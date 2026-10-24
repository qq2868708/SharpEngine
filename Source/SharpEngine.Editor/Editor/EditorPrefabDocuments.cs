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
        if (PlayState != EditorPlayState.Stopped)
        {
            throw new InvalidOperationException("Stop Play before editing a Prefab");
        }

        var FullPath = Path.GetFullPath(path);
        var Asset = _assets?.Assets.FirstOrDefault(Candidate =>
            Candidate.Type.Equals("Prefab", StringComparison.OrdinalIgnoreCase) &&
            _assets.ResolvePath(Candidate) is { } CandidatePath &&
            Path.GetFullPath(CandidatePath).Equals(FullPath,
                StringComparison.OrdinalIgnoreCase));
        if (Asset is null)
        {
            throw new InvalidOperationException(
                $"Prefab is not registered as an asset: {FullPath}");
        }

        var Result = OpenAsset(Asset.Id);
        if (!Result.Success)
        {
            throw new InvalidOperationException(Result.Message);
        }
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
                var Runtime = _runtimeWorld;
                if (Runtime is not null)
                {
                    var Created = Runtime.Instantiate(prefab, parent).Value;
                    _world!.ApplyRuntimeFrame(Runtime.Frame);
                    _selection = new(EditorSelectionKind.Entity, Created.EntityId);
                }
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
                string AssetId;
                try { AssetId = RegisterPrefabAsset(path, data.Name); }
                catch { File.Delete(path); throw; }
                var Opened = OpenAsset(AssetId);
                if (!Opened.Success)
                {
                    throw new InvalidOperationException(Opened.Message);
                }
                return true;
            }
        }
        catch (Exception error) { SetStatus("Prefab: " + error.Message); SetText("componentError", error.Message); }
        return true;
    }
    private string RegisterPrefabAsset(string PathValue, string Name)
    {
        var Root = _projectRoot ?? _engineRoot;
        var OwnerManifest = JsonNode.Parse(File.ReadAllText(_projectManifest ?? Path.Combine(_engineRoot, "engine.json")))!;
        var ManifestPath = Path.Combine(Root, OwnerManifest["assets"]?["manifest"]?.GetValue<string>() ?? "Content/Assets/assets.json");
        var Json = JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();
        if (Json["schemaVersion"]?.GetValue<int>() != AssetRegistry.ManifestSchemaVersion)
        {
            throw new InvalidDataException("Asset manifest requires schemaVersion=2 and Content-relative paths");
        }
        var ContentRoot = Path.Combine(Root, "Content");
        var Relative = Path.GetRelativePath(ContentRoot, PathValue).Replace('\\', '/');
        var Descriptor = new AssetDescriptor
        {
            RelativePath = Relative,
        };
        Descriptor.ResolvePath(ContentRoot);
        var Id = "prefabs/" + Path.GetFileNameWithoutExtension(PathValue).ToLowerInvariant();
        var Assets = Json["assets"]!.AsArray();
        if (Assets.Any(Asset => Asset!["id"]!.GetValue<string>() == Id))
        {
            throw new InvalidOperationException("Prefab asset ID already exists");
        }
        Assets.Add(new JsonObject
        {
            ["id"] = Id,
            ["type"] = "Prefab",
            ["name"] = Name,
            ["path"] = Relative,
            ["importer"] = "SharpEngine.PrefabJson",
            ["tags"] = new JsonArray("prefab"),
        });
        var Staged = ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(Staged, Json.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            File.Move(Staged, ManifestPath, true);
        }
        finally
        {
            if (File.Exists(Staged))
            {
                File.Delete(Staged);
            }
        }
        _assets = AssetRegistry.Load(_engineRoot, _projectRoot, _projectManifest);
        var ContentStates = _contentManagers?.Capture() ?? EditorLayoutDocument.CreateDefaultContentManagers();
        var ActiveContent = _contentManagers?.ActivePaneId ?? EditorPaneIds.Content;
        SetContentManagers(new ContentManagerCoordinator(_assets, ContentStates, ActiveContent));
        RefreshAssets();
        return (_projectRoot is null ? "engine://" : "project://") + Id;
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
