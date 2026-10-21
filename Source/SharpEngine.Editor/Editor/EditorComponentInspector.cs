using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Assets;
using SharpEngine.Hosting;
using SharpEngine.World;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime : IComponentRuntime
{
    private RuntimeWorld? _runtimeWorld;
    private WorldDocument? _playAuthoringWorld;
    private string? _playSelection;
    private string _componentMarkup = "";
    private string _componentSearch = "";
    private bool _addComponentOpen, _inspectorEditing;
    private string? _inspectorGesture;
    private ulong _inspectorGestureRevision;
    private readonly HashSet<string> _collapsedFields = [];
    private string? _cameraTransactionGroup;
    private readonly HashSet<string> _collapsedComponents = [];
    public ComponentRegistry? Components => _world?.Registry;
    public RuntimeWorld? RuntimeWorld => _runtimeWorld;
    private ulong ComponentRevision => _runtimeWorld?.Revision ?? _authoring?.DocumentRevision ?? 0;

    public void RegisterComponentModule(Assembly assembly, string moduleId, string generationId)
    {
        if (Components is null)
        {
            throw new InvalidOperationException("World is not loaded");
        }

        Components.RegisterAssembly(assembly, moduleId, generationId);
        foreach (var Type in assembly.GetTypes().Where(Type =>
                     !Type.IsAbstract && typeof(DataAsset).IsAssignableFrom(Type)))
        {
            DataAssetTypes.Register(Type);
        }
    }
    public void CompleteComponentRegistration()
    {
        if (Components is not null) Components.AssetExists = (id, type) => _assets?.Find(id) is { Exists: true } asset &&
            EditorAssetReferenceTypes.Matches(asset.Type, type);
        Components?.ValidateRegistrations();
        _world?.Data.Validate(Components!);
        RefreshComponentInspector(force: true);
    }
    private void BeginComponentPlay()
    {
        ActivateMainWorld();
        if (_world is null) return;
        _pressedKeys.Clear();
        _viewportKeyFocus = false;
        _gizmoDragging = false;
        _orbiting = false;
        _panning = false;
        _rightPointerPending = false;
        _rightPointerDragging = false;
        _playAuthoringWorld = _world;
        _playSelection = SelectedEntityId;
        Instance = new SharpEngine.Subsystems.RuntimeInstance(_assets, SubsystemLifetime, _inputRuntime, "main");
        try
        {
            _runtimeWorld = Instance.CreateWorld(_world.Data, _world.Registry);
        }
        catch
        {
            Instance.Dispose();
            Instance = null;
            throw;
        }
        _world = new WorldDocument(_runtimeWorld.Frame, _world.Registry, "runtime:" + _world.SourcePath);
        _inspectorEditing = false;
        RefreshLists(); RefreshComponentInspector(force: true);
    }
    private void EndComponentPlay()
    {
        ReleasePlayInputCapture();
        _runtimeWorld?.Dispose(); _runtimeWorld = null;
        Instance?.Dispose();
        Instance = null;
        if (_playAuthoringWorld is not null) _world = _playAuthoringWorld;
        _playAuthoringWorld = null;
        var restore = SelectedEntityId is { } selected && _world?.Entities.Any(e => e.Id == selected) == true ? selected :
            _playSelection is { } previous && _world?.Entities.Any(e => e.Id == previous) == true ? previous : null;
        _selection = restore is null ? EditorSelection.Scene : new(EditorSelectionKind.Entity, restore);
        _inspectorEditing = false; _componentMarkup = "";
        RefreshLists();
    }
    private void UpdateComponentRuntime(float DeltaSeconds)
    {
        if (_runtimeWorld is null || _world is null) return;
        var PreviousIds = string.Join('|', _world.Entities.Select(e => e.Id));
        Instance!.Tick(DeltaSeconds);
        _world.ApplyRuntimeFrame(_runtimeWorld.Frame);
        if (PreviousIds != string.Join('|', _world.Entities.Select(e => e.Id))) RefreshHierarchy();
    }
    private void CommitCameraNavigation(WorldEntity camera, WorldVector3 position, WorldVector3 rotation)
    {
        if (_runtimeWorld is { } runtime)
        {
            var transform = camera.Components.Single(c => c.TypeId == "engine.transform");
            _ = runtime.Patch(transform.ComponentId, new() { ["Position"] = new JsonArray(position.X, position.Y, position.Z),
                ["Rotation"] = new JsonArray(rotation.X, rotation.Y, rotation.Z) }, runtime.Revision).Value;
            _world!.ApplyRuntimeFrame(runtime.Frame);
        }
        else if (_authoring is not null)
        {
            var result = _authoring.SetTransform(new(camera.Id, new(position, rotation, camera.Scale),
                _authoring.DocumentRevision, _cameraTransactionGroup));
            if (!result.Success) SetStatus(result.Message);
        }
        RefreshWorldTitle(); RefreshInspector();
    }
    public string DescribeComponentsJson() => new JsonObject
    {
        ["schemaVersion"] = 1, ["context"] = _runtimeWorld is null ? "authoring" : "runtime", ["revision"] = ComponentRevision,
        ["types"] = new JsonArray((Components?.Types ?? []).OrderBy(t => t.TypeId, StringComparer.Ordinal).Select(t => (JsonNode)new JsonObject
        { ["typeId"] = t.TypeId, ["schemaVersion"] = t.SchemaVersion, ["name"] = t.DisplayName, ["moduleId"] = t.ModuleId,
            ["moduleGenerationId"] = t.GenerationId, ["menuPath"] = t.MenuPath, ["single"] = t.Single,
            ["schema"] = t.Schema(Components!), ["defaults"] = t.CreateDefaultState(Components!) }).ToArray()),
        ["document"] = _world?.Data.ToJson(),
        ["diagnostics"] = JsonSerializer.SerializeToNode(_runtimeWorld?.Diagnostics ?? []),
        ["navigation"] = _runtimeWorld?.Navigation.ToJson(),
        ["animation"] = _runtimeWorld is null ? null : new SharpEngine.Animation.AnimationSnapshot(_runtimeWorld.Frame).ToJson(),
        ["subsystems"] = DescribeSubsystems(),
    }.ToJsonString();

    /// <summary>Shared UI/MCP authoring and runtime mutation surface.</summary>
    public JsonObject EditComponent(JsonObject request)
    {
        var before = ComponentRevision;
        try
        {
            if (_world is null || _authoring is null) throw new InvalidOperationException("World is unavailable");
            var context = request["context"]!.GetValue<string>();
            if (context != (_runtimeWorld is null ? "authoring" : "runtime")) throw new InvalidOperationException("CONTEXT_MISMATCH");
            if (request["expectedRevision"]!.GetValue<ulong>() != before) throw new InvalidOperationException("REVISION_CONFLICT");
            var operation = request["operation"]!.GetValue<string>();
            var entityId = request["entityId"]?.GetValue<string>() ?? SelectedEntityId ?? "";
            var componentId = request["componentId"]?.GetValue<string>() ?? "";
            var initialIds = _world.Data.Entities.Select(e => e.EntityId).Concat(_world.Data.Entities.SelectMany(e => e.Components).Select(c => c.ComponentId)).ToHashSet();
            WorldEditResult? result = null;
            if (_runtimeWorld is { } runtime)
            {
                switch (operation)
                {
                    case "add": _ = runtime.AddComponent(entityId, request["typeId"]!.GetValue<string>(), request["state"] as JsonObject).Value; break;
                    case "remove": _ = runtime.RemoveComponent(entityId, componentId).Value; break;
                    case "patch": _ = runtime.Patch(componentId, request["state"]!.AsObject(), before).Value; break;
                    case "reset": _ = runtime.Reset(componentId, before).Value; break;
                    case "enabled": runtime.FindComponent(componentId)!.Enabled = request["value"]!.GetValue<bool>(); break;
                    case "active": runtime.FindEntity(entityId)!.ActiveSelf = request["value"]!.GetValue<bool>(); break;
                    case "create-entity": _ = runtime.CreateEntity(request["name"]?.GetValue<string>() ?? "Entity", request["parentId"]?.GetValue<string>()).Value; break;
                    case "destroy-entity": _ = runtime.DestroyEntity(entityId).Value; break;
                    default: throw new InvalidOperationException("Unsupported Runtime operation");
                }
                _world.ApplyRuntimeFrame(runtime.Frame);
            }
            else result = _authoring.Edit("component." + operation, before, data =>
            {
                var entity = data.Entities.FirstOrDefault(e => e.EntityId == entityId);
                var component = entity?.Components.FirstOrDefault(c => c.ComponentId == componentId);
                switch (operation)
                {
                    case "add": data.AddComponent(Components!, entityId, request["typeId"]!.GetValue<string>(), request["state"] as JsonObject); break;
                    case "remove": data.RemoveComponent(Components!, entityId, componentId); break;
                    case "patch":
                        if (component is null || Components!.Find(component.TypeId) is null) throw new InvalidOperationException("Unknown component");
                        foreach (var (field, value) in request["state"]!.AsObject()) component.State[field] = value?.DeepClone();
                        break;
                    case "reset": component!.State = Components!.Find(component.TypeId)!.CreateDefaultState(Components); break;
                    case "enabled": component!.Enabled = request["value"]!.GetValue<bool>(); break;
                    case "active": entity!.ActiveSelf = request["value"]!.GetValue<bool>(); break;
                    case "create-entity": data.CreateEntity(Components!, request["name"]?.GetValue<string>() ?? "Entity", request["parentId"]?.GetValue<string>()); break;
                    case "destroy-entity": data.DestroyEntity(Components!, entityId); break;
                    case "reorder":
                        var target = request["index"]!.GetValue<int>();
                        if (target < 0 || target >= entity!.Components.Count) throw new InvalidOperationException("Invalid component index");
                        entity.Components.Remove(component!); entity.Components.Insert(target, component!); break;
                    default: throw new InvalidOperationException("Unknown component operation");
                }
            }, [entityId], request["transactionGroupId"]?.GetValue<string>());
            if (result is { Success: false }) throw new InvalidOperationException(result.Message);
            var finalIds = _world.Data.Entities.Select(e => e.EntityId).Concat(_world.Data.Entities.SelectMany(e => e.Components).Select(c => c.ComponentId)).ToHashSet();
            if (operation == "create-entity") _selection = new(EditorSelectionKind.Entity, _world.Entities.Last().Id);
            if (SelectedEntityId is { } selection && _world.Entities.All(e => e.Id != selection)) _selection = EditorSelection.Scene;
            _inspectorEditing = _inspectorGesture is not null;
            _inspectorGestureRevision = ComponentRevision;
            RefreshHierarchy(); RefreshWorldTitle();
            if (!_inspectorEditing) { RefreshInspector(); RefreshComponentInspector(force: true); }
            RefreshPrefabEditor();
            return new() { ["success"] = true, ["context"] = context, ["beforeRevision"] = before, ["afterRevision"] = ComponentRevision,
                ["transactionId"] = result?.Transaction?.TransactionId.ToString(), ["dirty"] = _runtimeWorld is null && IsDirty,
                ["createdIds"] = new JsonArray(finalIds.Except(initialIds).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["deletedIds"] = new JsonArray(initialIds.Except(finalIds).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
        }
        catch (Exception error)
        {
            SetText("componentError", error.Message);
            return new() { ["success"] = false, ["beforeRevision"] = before, ["afterRevision"] = ComponentRevision,
                ["diagnostics"] = new JsonArray(new JsonObject { ["code"] = error is ComponentValidationException validation ? validation.Code : "COMPONENT_EDIT_REJECTED",
                    ["severity"] = "error", ["message"] = error.Message, ["fieldPath"] = error is ComponentValidationException field ? field.FieldPath : "",
                    ["revision"] = ComponentRevision, ["retryable"] = error.Message.Contains("REVISION_CONFLICT") }) };
        }
    }
    private JsonObject ComponentRequest(string operation)
    {
        var result = new JsonObject { ["operation"] = operation, ["context"] = _runtimeWorld is null ? "authoring" : "runtime",
            ["expectedRevision"] = ComponentRevision, ["entityId"] = SelectedEntityId };
        if (operation == "create-entity" && _activePrefabPath is not null)
            result["parentId"] = SelectedEntityId ?? _world!.Data.Entities.Single(e => e.ParentId is null).EntityId;
        return result;
    }
    private bool HandleComponentAction(string action)
    {
        if (!action.StartsWith("editor.component-", StringComparison.Ordinal)) return false;
        var parts = action["editor.component-".Length..].Split(':', 4);
        try
        {
            switch (parts[0])
            {
                case "gesture": _inspectorGesture = Guid.NewGuid().ToString("N"); _inspectorGestureRevision = ComponentRevision; _inspectorEditing = true; return true;
                case "gesture-end": _inspectorGesture = null; _inspectorEditing = false; RefreshComponentInspector(force: true); return true;
                case "fold":
                    if (!_collapsedFields.Add(parts[1])) _collapsedFields.Remove(parts[1]);
                    RefreshComponentInspector(force: true); return true;
                case "drop":
                {
                    var request = ComponentRequest("reorder"); request["componentId"] = parts[1];
                    request["index"] = SelectedEntity!.Record.Components.FindIndex(c => c.ComponentId == parts[2]);
                    _ = EditComponent(request); return true;
                }
                case "focus": _inspectorEditing = true; return true;
                case "blur": _inspectorEditing = false; RefreshComponentInspector(); return true;
                case "cancel": _inspectorEditing = false; RefreshComponentInspector(force: true); return true;
                case "menu": _addComponentOpen = !_addComponentOpen; RefreshComponentInspector(force: true); return true;
                case "search": _componentSearch = string.Join(':', parts.Skip(1)); RefreshAddComponentMenu(); return true;
                case "collapse": if (!_collapsedComponents.Add(parts[1])) _collapsedComponents.Remove(parts[1]); RefreshComponentInspector(force: true); return true;
                case "field":
                {
                    // componentId, encoded path, value; revision captured when the field was rendered.
                    var payload = Uri.UnescapeDataString(parts[2]).Split('|', 4);
                    var owner = _world!.Data.Entities.Single(e => e.Components.Any(c => c.ComponentId == parts[1]));
                    var component = owner.Components.Single(c => c.ComponentId == parts[1]);
                    var segments = payload[2].Split('/');
                    var state = (JsonObject)component.State.DeepClone();
                    JsonNode cursor = state;
                    for (int i = 0; i < segments.Length - 1; i++) cursor = cursor is JsonArray array ? array[int.Parse(segments[i])]! : cursor[segments[i]]!;
                    var text = parts.Length > 3 ? parts[3] : "";
                    JsonNode? value = payload[1] switch
                    { "string" => JsonValue.Create(text), "reference" => text.Length == 0 ? null : JsonValue.Create(text), "enum" => JsonValue.Create(text), _ => JsonNode.Parse(text) };
                    if (cursor is JsonArray vector) vector[int.Parse(segments[^1])] = value; else cursor[segments[^1]] = value;
                    var request = ComponentRequest("patch"); request["componentId"] = parts[1]; request["state"] = state;
                    request["entityId"] = owner.EntityId;
                    request["expectedRevision"] = ulong.Parse(payload[0], CultureInfo.InvariantCulture);
                    if (_inspectorGesture is not null)
                    {
                        request["expectedRevision"] = _runtimeWorld is null ? _inspectorGestureRevision : ComponentRevision;
                        request["transactionGroupId"] = _inspectorGesture;
                    }
                    else if (_runtimeWorld is not null && payload.Length == 4)
                    {
                        JsonNode? current = component.State;
                        foreach (var segment in segments) current = current is JsonArray currentArray ? currentArray[int.Parse(segment)] : current?[segment];
                        if (!JsonNode.DeepEquals(current, JsonNode.Parse(payload[3]))) throw new InvalidOperationException("REVISION_CONFLICT: this field changed while editing");
                        request["expectedRevision"] = ComponentRevision;
                    }
                    _inspectorEditing = _inspectorGesture is not null;
                    if (EditComponent(request)["success"]?.GetValue<bool>() != true) _inspectorEditing = true;
                    return true;
                }
                case "list":
                {
                    var detail = Uri.UnescapeDataString(parts[2]).Split('|', 2);
                    var c = _world!.Data.Entities.SelectMany(e => e.Components).Single(c => c.ComponentId == parts[1]);
                    var state = (JsonObject)c.State.DeepClone();
                    var path = detail[0].Split('/');
                    JsonNode cursor = state;
                    foreach (var segment in path.SkipLast(1)) cursor = cursor is JsonArray parent ? parent[int.Parse(segment)]! : cursor[segment]!;
                    var current = cursor is JsonArray parentArray ? parentArray[int.Parse(path[^1])] : cursor[path[^1]];
                    var list = current as JsonArray ?? new JsonArray();
                    if (parts[3] == "add") list.Add(JsonNode.Parse(detail[1]));
                    else if (parts[3].StartsWith("up,")) { var i = int.Parse(parts[3][3..]); var item = list[i]; list.RemoveAt(i); list.Insert(i - 1, item); }
                    else list.RemoveAt(int.Parse(parts[3], CultureInfo.InvariantCulture));
                    if (current is null)
                    { if (cursor is JsonArray targetArray) targetArray[int.Parse(path[^1])] = list; else cursor[path[^1]] = list; }
                    var request = ComponentRequest("patch"); request["componentId"] = parts[1]; request["state"] = state; _ = EditComponent(request); return true;
                }
            }
            var command = ComponentRequest(parts[0]);
            if (parts[0] == "add") command["typeId"] = Uri.UnescapeDataString(parts[1]);
            else if (parts.Length > 1) command["componentId"] = parts[1];
            if (parts[0] is "enabled" or "active") command["value"] = parts[^1] == "true";
            if (parts[0] == "reorder") command["index"] = int.Parse(parts[2], CultureInfo.InvariantCulture);
            _ = EditComponent(command); return true;
        }
        catch (Exception error) { _inspectorEditing = true; SetText("componentError", error.Message); return true; }
    }
    private void RefreshComponentInspector(bool force = false)
    {
        if (_inspectorEditing && !force)
        {
            return;
        }
        var Entity = SelectedEntity;
        var Markup = new StringBuilder();
        if (Entity is not null && Components is not null)
        {
            Markup.Append("<div class=\"inspector-entity-state\"><span class=\"inspector-context\">")
                .Append(_runtimeWorld is null ? "Authoring" : "Runtime — changes discarded on Stop")
                .Append("</span><label class=\"inspector-active\"><span>Active</span><input type=\"checkbox\" title=\"Entity active\" ")
                .Append(Entity.ActiveSelf ? "checked=\"checked\" " : "")
                .Append("data-action=\"editor.component-active:").Append(!Entity.ActiveSelf ? "true" : "false").Append("\" /></label></div>");
            foreach (var Component in Entity.Components)
            {
                var Type = Components.Find(Component.TypeId);
                if (Type is null)
                {
                    continue;
                }
                var Id = Escape(Component.ComponentId);
                var Collapsed = _collapsedComponents.Contains(Component.ComponentId);
                var Icon = Component.TypeId switch
                {
                    "engine.transform" => "move",
                    "engine.mesh-renderer" => "mesh",
                    "engine.camera" => "camera",
                    "engine.light" => "light",
                    _ => "data",
                };
                Markup.Append("<div class=\"component\" data-component-id=\"").Append(Id).Append("\" draggable=\"true\"><div class=\"component-title component-header\">")
                    .Append("<button title=\"").Append(Escape(Type.DisplayName)).Append("\" data-action=\"editor.component-collapse:").Append(Id).Append("\">")
                    .Append(InspectorIcon(Collapsed ? "chevron-right" : "chevron-down", "component-chevron"))
                    .Append(InspectorIcon(Icon, "component-type-icon"))
                    .Append("<span class=\"component-name\">").Append(Escape(InspectorComponentTitle(Type.DisplayName))).Append("</span></button>");
                if (Component.TypeId != "engine.transform")
                {
                    Markup.Append("<input type=\"checkbox\" title=\"Enable component\" ").Append(Component.Enabled ? "checked=\"checked\" " : "")
                        .Append("data-action=\"editor.component-enabled:").Append(Id).Append(':').Append(!Component.Enabled ? "true" : "false").Append("\" />");
                }
                Markup.Append("</div>");
                if (!Collapsed)
                {
                    foreach (var Field in Type.Fields.Where(Field => !Field.Hidden))
                    {
                        if (Field.Header is not null)
                        {
                            Markup.Append("<div class=\"component-title\">").Append(Escape(Field.Header)).Append("</div>");
                        }
                        AppendComponentField(Markup, Component, Field, Field.Name, Component.State[Field.Name], Field.ValueType);
                    }
                    Markup.Append("<div class=\"component-actions\"><button data-action=\"editor.component-reset:").Append(Id).Append("\">")
                        .Append(InspectorIcon("reset")).Append("<span>Reset</span></button><div class=\"grow\" />");
                    if (Component.TypeId != "engine.transform")
                    {
                        string? Reason = null;
                        try
                        {
                            var Staging = _world!.Data.Clone();
                            Staging.RemoveComponent(Components, Entity.Id, Component.ComponentId);
                        }
                        catch (Exception Error)
                        {
                            Reason = Error.Message;
                        }
                        Markup.Append("<button class=\"component-icon-button component-danger\" ")
                            .Append(Reason is null ? "" : "disabled=\"disabled\" ")
                            .Append("title=\"").Append(Escape(Reason ?? "Remove component")).Append("\" data-action=\"editor.component-remove:").Append(Id).Append("\">")
                            .Append(InspectorActionIcon("trash", Reason ?? "Remove component")).Append("</button>");
                    }
                    if (_runtimeWorld is null)
                    {
                        var Index = Entity.Record.Components.IndexOf(Component);
                        if (Index > 0)
                        {
                            Markup.Append("<button class=\"component-icon-button\" title=\"Move component up\" data-action=\"editor.component-reorder:").Append(Id).Append(':').Append(Index - 1).Append("\">")
                                .Append(InspectorActionIcon("up", "Move component up")).Append("</button>");
                        }
                        if (Index + 1 < Entity.Components.Count)
                        {
                            Markup.Append("<button class=\"component-icon-button\" title=\"Move component down\" data-action=\"editor.component-reorder:").Append(Id).Append(':').Append(Index + 1).Append("\">")
                                .Append(InspectorActionIcon("down", "Move component down")).Append("</button>");
                        }
                    }
                    Markup.Append("</div>");
                }
                Markup.Append("</div>");
            }
            Markup.Append("<button id=\"add-component\" class=\"add-component\" data-action=\"editor.component-menu\">")
                .Append(InspectorIcon("add")).Append("<span>Add Component</span></button>");
            if (_addComponentOpen)
            {
                Markup.Append("<input id=\"component-search\" type=\"text\" placeholder=\"Search components\" value=\"").Append(Escape(_componentSearch)).Append("\" data-change-action=\"editor.component-search:\" /><div id=\"componentMenuResults\">").Append(AddComponentMarkup()).Append("</div>");
            }
            Markup.Append("<div class=\"component-actions component-entity-actions\"><button class=\"component-prefab\" data-action=\"editor.prefab-export\">")
                .Append(InspectorIcon("export")).Append("<span>Save as Prefab</span></button><button class=\"component-icon-button component-danger\" title=\"Delete entity\" data-action=\"editor.component-destroy-entity\">")
                .Append(InspectorActionIcon("trash", "Delete entity")).Append("</button></div>");
        }
        Markup.Append("<div id=\"componentError\" class=\"component-error\"></div>");
        var Html = Markup.ToString();
        if (force || Html != _componentMarkup)
        {
            _nativeApi.SetUiMarkup("componentInspector", Html);
            _componentMarkup = Html;
        }
    }
    private static string InspectorIcon(string Name, string Class = "") => $"<img class=\"icon {Class}\" src=\"{EditorResourceUris.IconSource(Name)}\" />";
    private static string InspectorActionIcon(string Name, string Tooltip) => InspectorIcon(Name) + "<span class=\"control-tooltip tooltip-right\">" + Escape(Tooltip) + "</span>";
    private static string InspectorComponentTitle(string Name)
    {
        var Title = Name.EndsWith("Component", StringComparison.Ordinal) && Name.Length > 9 ? Name[..^9] : Name;
        return System.Text.RegularExpressions.Regex.Replace(Title, "(?<=[a-z0-9])(?=[A-Z])", " ");
    }
    private void RefreshAddComponentMenu() => _nativeApi.SetUiMarkup("componentMenuResults", AddComponentMarkup());
    private string AddComponentMarkup()
    {
        if (Components is null || SelectedEntity is not { } entity) return "";
        var html = new StringBuilder();
        foreach (var type in Components.Types.OrderBy(t => t.MenuPath, StringComparer.Ordinal))
        {
            if (type.TypeId == "engine.transform" || !(type.MenuPath + type.DisplayName + type.ModuleId + type.RuntimeType.FullName).Contains(_componentSearch, StringComparison.OrdinalIgnoreCase)) continue;
            string? reason = null; string dependencies = "";
            try { dependencies = string.Join(", ", Components.Addition(type.TypeId, entity.Components.Select(c => c.TypeId)).Where(d => d.TypeId != type.TypeId).Select(d => d.DisplayName)); }
            catch (Exception error) { reason = error.Message; }
            html.Append("<button class=\"component-menu-item\" title=\"").Append(Escape(reason ?? (dependencies.Length == 0 ? type.ModuleId : "Also adds: " + dependencies))).Append("\" ")
                .Append(reason is null ? "" : "disabled=\"disabled\" ").Append("data-action=\"editor.component-add:").Append(Uri.EscapeDataString(type.TypeId)).Append("\">").Append(Escape(type.MenuPath));
            if (dependencies.Length != 0) html.Append(" (+ ").Append(Escape(dependencies)).Append(')');
            html.Append("</button>");
        }
        return html.ToString();
    }
    private void AppendComponentField(StringBuilder html, ComponentRecord component, ComponentField field, string path, JsonNode? value, Type type)
    {
        var name = path.Contains('/') ? path.Split('/').Last() : field.DisplayName ?? field.Name;
        var schema = ComponentValue.Schema(type, Components!);
        var vector = schema["x-sharpengine-control"] is not null;
        var foldId = component.ComponentId + "-" + Uri.EscapeDataString(path);
        if (value is null && !type.IsValueType && type != typeof(string) && !ComponentValue.IsReference(type) &&
            !type.IsArray && !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
        {
            var initial = Components!.Data(type).Defaults(Components);
            var action = "editor.component-field:" + component.ComponentId + ":" + Uri.EscapeDataString(ComponentRevision + "|json|" + path + "|null") + ":" + initial.ToJsonString();
            html.Append("<div class=\"component-field\"><label>").Append(Escape(name)).Append(" (null)</label><button data-action=\"").Append(Escape(action)).Append("\">Create</button></div>");
            return;
        }
        if (value is JsonObject obj)
        {
            html.Append("<div class=\"component-nested\"><button data-action=\"editor.component-fold:").Append(foldId).Append("\">").Append(Escape(name)).Append("</button>");
            if (!_collapsedFields.Contains(foldId)) foreach (var child in Components!.Data(type).Fields.Where(f => !f.Hidden)) AppendComponentField(html, component, child, path + "/" + child.Name, obj[child.Name], child.ValueType);
            html.Append("</div>"); return;
        }
        if (value is JsonArray || value is null && (type.IsArray || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
        {
            var array = value as JsonArray ?? new JsonArray();
            html.Append("<div class=\"component-nested\"><label>").Append(Escape(name)).Append("</label>");
            var element = vector ? typeof(float) : type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            if (vector)
            {
                html.Append(type == typeof(Color) ? "<div class=\"component-vector component-color\">" : "<div class=\"component-vector\">");
            }
            for (int i = 0; i < array.Count; i++)
            {
                AppendComponentField(html, component, field with { DisplayName = vector ? (type == typeof(Color) ? "RGBA" : "XYZW")[i].ToString() : i.ToString(), Minimum = null, Maximum = null }, path + "/" + i, array[i], element);
                if (!vector)
                {
                    var action = "editor.component-list:" + component.ComponentId + ":" + Uri.EscapeDataString(path + "|" + DefaultElement(element)) + ":";
                    if (i > 0) html.Append("<button data-action=\"").Append(action).Append("up,").Append(i).Append("\">↑</button>");
                    html.Append("<button data-action=\"").Append(action).Append(i).Append("\">Remove item</button>");
                }
            }
            if (!vector) html.Append("<button data-action=\"editor.component-list:").Append(component.ComponentId).Append(':').Append(Uri.EscapeDataString(path + "|" + DefaultElement(element))).Append(":add\">+ Item</button>");
            if (vector) html.Append("</div>");
            if (type == typeof(Color))
            {
                html.Append("<div class=\"component-palette\">");
                foreach (var color in new[] { new Color(1,1,1), new Color(0,0,0), new Color(1,0,0), new Color(0,1,0), new Color(0,0,1), new Color(1,1,0), new Color(0,1,1), new Color(1,0,1) })
                {
                    var colorJson = ComponentValue.Encode(color, typeof(Color), Components!)!.ToJsonString();
                    var colorAction = "editor.component-field:" + component.ComponentId + ":" + Uri.EscapeDataString(ComponentRevision + "|json|" + path + "|" + value!.ToJsonString()) + ":" + colorJson;
                    html.Append("<button title=\"Choose color\" style=\"background-color:rgb(").Append((int)(color.R*255)).Append(',').Append((int)(color.G*255)).Append(',').Append((int)(color.B*255)).Append(");\" data-action=\"").Append(Escape(colorAction)).Append("\"> </button>");
                }
                html.Append("</div>");
                AppendInspectorColorPicker(html, component.ComponentId, path, array);
            }
            html.Append("</div>"); return;
        }
        var isReference = schema["x-sharpengine-reference"] is not null;
        var kind = isReference ? "reference" : type.IsEnum ? "enum" : type == typeof(string) ? "string" : "json";
        var actionPrefix = "editor.component-field:" + component.ComponentId + ":" + Uri.EscapeDataString(ComponentRevision + "|" + kind + "|" + path + "|" + (value?.ToJsonString() ?? "null")) + ":";
        var controlId = "component-field-" + component.ComponentId + "-" + Uri.EscapeDataString(path);
        html.Append("<div class=\"field component-field\" title=\"").Append(Escape(field.Tooltip ?? "")).Append("\"><label>").Append(Escape(field.DisplayName ?? name)).Append("</label>");
        if (type.IsEnum || isReference)
        {
            var options = new List<(string Id, string Label)>();
            if (type.IsEnum) options.AddRange(Enum.GetNames(type).Select(n => (n, n)));
            else
            {
                options.Add(("", "None"));
                if (type == typeof(SharpEntity)) options.AddRange(_world!.Entities.Select(e => (e.Id, e.Name)));
                else if (typeof(SharpComponent).IsAssignableFrom(type)) options.AddRange(_world!.Data.Entities.SelectMany(e => e.Components.Where(c => Components!.Find(c.TypeId) is { } d && type.IsAssignableFrom(d.RuntimeType)).Select(c => (c.ComponentId, e.Name + " / " + Components!.Find(c.TypeId)!.DisplayName + " / " + c.ComponentId[..Math.Min(8, c.ComponentId.Length)]))));
                else
                {
                    options.AddRange((_assets?.Assets ?? []).Where(Asset => Asset.Type.Equals(
                        ComponentValue.AssetTypeName(type.GetGenericArguments()[0]), StringComparison.OrdinalIgnoreCase))
                        .Select(Asset => (Asset.Id, Asset.Name)));
                }
            }
            html.Append("<select id=\"").Append(controlId).Append("\" data-change-action=\"").Append(actionPrefix).Append("\">");
            foreach (var option in options) html.Append("<option value=\"").Append(Escape(option.Id)).Append("\" ").Append((value?.GetValue<string>() ?? "") == option.Id ? "selected=\"selected\"" : "").Append('>').Append(Escape(option.Label)).Append("</option>");
            html.Append("</select>");
        }
        else if (type == typeof(bool)) html.Append("<input id=\"").Append(controlId).Append("\" type=\"checkbox\" ").Append(value?.GetValue<bool>() == true ? "checked=\"checked\" " : "").Append("data-action=\"").Append(actionPrefix).Append(value?.GetValue<bool>() == true ? "false" : "true").Append("\" />");
        else
        {
            var text = InspectorFieldText(value, type);
            if (type == typeof(string) && field.Lines > 0)
                html.Append("<textarea id=\"").Append(controlId).Append("\" rows=\"").Append(field.Lines).Append("\" data-component-input=\"true\" data-original-value=\"").Append(Escape(text)).Append("\" data-commit-action=\"").Append(actionPrefix).Append("\">").Append(Escape(text)).Append("</textarea>");
            else html.Append("<input id=\"").Append(controlId).Append("\" type=\"text\" value=\"").Append(Escape(text)).Append("\" data-component-input=\"true\" data-original-value=\"").Append(Escape(text)).Append("\" data-commit-action=\"").Append(actionPrefix).Append("\" />");
            if (field.Minimum is { } min && field.Maximum is { } max)
                html.Append("<input id=\"").Append(controlId).Append("-slider\" type=\"range\" min=\"").Append(min.ToString(CultureInfo.InvariantCulture)).Append("\" max=\"").Append(max.ToString(CultureInfo.InvariantCulture)).Append("\" step=\"").Append(type == typeof(float) || type == typeof(double) ? "0.01" : "1").Append("\" value=\"").Append(Escape(text)).Append("\" data-component-slider=\"true\" data-change-action=\"").Append(actionPrefix).Append("\" />");
        }
        html.Append("</div>");
    }
    private string DefaultElement(Type type) => ComponentValue.Encode(type.IsValueType ? Activator.CreateInstance(type) : type == typeof(string) ? "" : null, type, Components!)?.ToJsonString() ?? "null";
    private static string InspectorFieldText(JsonNode? Value, Type ValueType)
    {
        if (ValueType == typeof(string))
        {
            return Value?.GetValue<string>() ?? "";
        }
        var Text = Value?.ToJsonString() ?? "null";
        if (Value is not null && (ValueType == typeof(float) || ValueType == typeof(double)))
        {
            var Number = double.Parse(Text, CultureInfo.InvariantCulture);
            return Number.ToString("G6", CultureInfo.InvariantCulture);
        }
        return Text;
    }
}
