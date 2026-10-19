using SharpEngine.Settings;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private const string EngineSettingsHostId = "settings.engine";
    private const string ProjectSettingsHostId = "settings.project";
    private DevelopmentSettingsRegistry? _developmentSettings;
    private readonly HashSet<string> _openSettingsHosts = new(StringComparer.Ordinal);
    private readonly Dictionary<SettingsScope, string> _activeSettingsPages = new();
    private long _nextSettingsDiskCheck;

    public string DescribeSettingsJson() => _developmentSettings?.Describe()
        .ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }) ??
        "{\"schemaVersion\":1,\"pages\":[]}";

    internal void RegisterProjectSettingsAssembly(Assembly assembly)
    {
        var registry = _developmentSettings ??
            throw new InvalidOperationException("Development Settings are not initialized");
        registry.RegisterAssembly(assembly, SettingsScope.Project);
        EnsureActiveSettingsPage(SettingsScope.Project);
        RefreshSettingsWindow(SettingsScope.Project);
    }

    private void InitializeDevelopmentSettings()
    {
        _developmentSettings = new DevelopmentSettingsRegistry(_projectRoot, _engineRoot);
        try
        {
            _developmentSettings.RegisterAssembly(typeof(EditorRuntime).Assembly,
                SettingsScope.Engine);
            ImportLegacyNavigationSettings();
        }
        catch
        {
            _developmentSettings.Dispose();
            _developmentSettings = null;
            throw;
        }
        EnsureActiveSettingsPage(SettingsScope.Engine);
    }

    private void PollDevelopmentSettings()
    {
        var now = Environment.TickCount64;
        if (now < _nextSettingsDiskCheck) return;
        _nextSettingsDiskCheck = now + 1000;
        if (_developmentSettings?.ReloadChangedFiles() != true) return;
        RefreshSettingsUi();
    }

    private void ImportLegacyNavigationSettings()
    {
        var page = _developmentSettings!.Find(SettingsScope.Engine, "sharpengine.editor-navigation")!;
        if (page.Path is not null && File.Exists(page.Path)) return;
        var legacyPath = Path.Combine(_projectRoot ?? _engineRoot, "Saved", "Config", "EngineSettings.json");
        if (!File.Exists(legacyPath)) return;
        using var legacy = JsonDocument.Parse(File.ReadAllText(legacyPath));
        var data = legacy.RootElement;
        if (data.GetProperty("schemaVersion").GetInt32() != 1 ||
            data.EnumerateObject().Any(property => property.Name is not ("schemaVersion" or "invertYAxis")))
            throw new InvalidDataException($"Unsupported legacy settings: {legacyPath}");
        if (!_developmentSettings.TrySet(SettingsScope.Engine, page.Attribute.Id, "invertYAxis",
                data.GetProperty("invertYAxis").GetBoolean() ? "true" : "false", out var error))
            throw new InvalidDataException(error);
        // Leave the original preference file untouched. Explicit Save creates the new document.
    }

    private bool ConfirmDevelopmentSettingsClose()
    {
        foreach (var page in _developmentSettings?.Descriptors.Where(page => page.IsDirty) ?? [])
        {
            var choice = NativeEngineApi.PromptUnsavedWorld(_windowHandle,
                $"{ScopeTitle(page.Attribute.Scope)} / {page.DisplayName}");
            if (choice == UnsavedWorldChoice.Cancel) return false;
            if (choice == UnsavedWorldChoice.Save &&
                !_developmentSettings!.Save(page.Attribute.Scope, page.Attribute.Id, out var error))
            {
                OpenSettingsWindow(page.Attribute.Scope);
                _nativeApi.SetHostMarkup(ScopeHost(page.Attribute.Scope), "settings-feedback",
                    $"<span>{EscapeSettings(error)}</span>");
                SetStatus($"Settings could not be saved: {error}");
                return false;
            }
        }
        return true;
    }

    private void ShutdownDevelopmentSettings()
    {
        foreach (var hostId in _openSettingsHosts.ToArray())
            _nativeApi.DestroyDockWindow(hostId);
        _openSettingsHosts.Clear();
        _activeSettingsPages.Clear();
        _developmentSettings?.Dispose();
        _developmentSettings = null;
    }

    private bool HandleDevelopmentSettingsAction(string hostId, string action)
    {
        if (action == "window.closed" && TryScopeFromHost(hostId, out var closedScope))
        {
            _nativeApi.DestroyDockWindow(hostId);
            _openSettingsHosts.Remove(hostId);
            SetStatus($"{ScopeTitle(closedScope)} closed");
            return true;
        }
        if (action == "editor.engine-settings")
        {
            OpenSettingsWindow(SettingsScope.Engine);
            return true;
        }
        if (action == "editor.project-settings")
        {
            OpenSettingsWindow(SettingsScope.Project);
            return true;
        }
        const string pagePrefix = "editor.settings-page:";
        if (action.StartsWith(pagePrefix, StringComparison.Ordinal))
        {
            var parts = action[pagePrefix.Length..].Split(':', 2);
            if (parts.Length == 2 && TryScope(parts[0], out var scope) &&
                _developmentSettings?.Find(scope, parts[1]) is not null)
            {
                _activeSettingsPages[scope] = parts[1];
                RefreshSettingsWindow(scope);
            }
            return true;
        }
        const string changePrefix = "editor.settings-change:";
        if (action.StartsWith(changePrefix, StringComparison.Ordinal))
        {
            var parts = action[changePrefix.Length..].Split(':', 4);
            if (parts.Length == 4 && TryScope(parts[0], out var scope))
                ApplySettingsEdit(scope, parts[1], parts[2], parts[3]);
            return true;
        }
        foreach (var operation in new[] { "save", "revert", "undo", "redo" })
        {
            var prefix = $"editor.settings-{operation}:";
            if (!action.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var parts = action[prefix.Length..].Split(':', 2);
            if (parts.Length == 2 && TryScope(parts[0], out var scope))
                ApplySettingsOperation(scope, parts[1], operation);
            return true;
        }
        return false;
    }

    private void OpenSettingsWindow(SettingsScope scope)
    {
        EnsureActiveSettingsPage(scope);
        var hostId = ScopeHost(scope);
        if (_nativeApi.CreateDockWindow(hostId, ScopeTitle(scope)) == 0)
        {
            SetStatus($"{ScopeTitle(scope)} requires the document UI host");
            return;
        }
        _openSettingsHosts.Add(hostId);
        _nativeApi.SetHostMarkup(hostId, "dockWindowTitle", EscapeSettings(ScopeTitle(scope)));
        RefreshSettingsWindow(scope, force: true);
        SetStatus($"Opened {ScopeTitle(scope)}");
    }

    private void RefreshSettingsWindow(SettingsScope scope, bool force = false)
    {
        var hostId = ScopeHost(scope);
        if (!_openSettingsHosts.Contains(hostId) && !force) return;
        _nativeApi.SetHostMarkup(hostId, "workspace", BuildSettingsMarkup(scope));
    }

    internal string BuildSettingsMarkup(SettingsScope scope)
    {
        var pages = _developmentSettings?.Descriptors
            .Where(value => value.Attribute.Scope == scope).ToArray() ?? [];
        var markup = new StringBuilder("<div class=\"settings-window\">");
        markup.Append("<div class=\"settings-sidebar\"><div class=\"settings-title\">")
            .Append(EscapeSettings(ScopeTitle(scope))).Append("</div>");
        string? category = null;
        foreach (var page in pages)
        {
            if (!string.Equals(category, page.Category, StringComparison.Ordinal))
            {
                category = page.Category;
                markup.Append("<div class=\"settings-category\">")
                    .Append(EscapeSettings(category)).Append("</div>");
            }
            var active = _activeSettingsPages.TryGetValue(scope, out var activeId) &&
                activeId == page.Attribute.Id;
            markup.Append("<button class=\"settings-page")
                .Append(active ? " active" : string.Empty)
                .Append("\" action=\"editor.settings-page:")
                .Append(ScopeToken(scope)).Append(':')
                .Append(EscapeSettings(page.Attribute.Id)).Append("\"><span>")
                .Append(EscapeSettings(page.DisplayName)).Append("</span>");
            if (page.IsDirty) markup.Append("<span class=\"settings-dirty\">*</span>");
            markup.Append("</button>");
        }
        markup.Append("</div><div class=\"settings-content\">");
        var selected = pages.FirstOrDefault(page =>
            _activeSettingsPages.TryGetValue(scope, out var id) &&
            id == page.Attribute.Id);
        if (selected is null)
        {
            markup.Append("<div class=\"settings-empty\">No ")
                .Append(scope == SettingsScope.Engine ? "engine" : "project")
                .Append(" settings are registered.</div>");
        }
        else
        {
            AppendSettingsPage(markup, selected);
        }
        markup.Append("</div></div>");
        return markup.ToString();
    }

    private void AppendSettingsPage(StringBuilder markup,
        DevelopmentSettingsDescriptor descriptor)
    {
        markup.Append("<div id=\"settings-header\" class=\"settings-header\">")
            .Append(BuildSettingsHeader(descriptor))
            .Append("</div><div class=\"settings-fields\">");
        AppendSettingsFields(markup, descriptor, descriptor.Current,
            descriptor.Fields, string.Empty, 0);
        markup.Append("</div><div id=\"settings-feedback\" class=\"settings-footer\"><span>")
            .Append(EscapeSettings(descriptor.Diagnostic ??
                (descriptor.IsDirty ? "Unsaved changes" : "Saved"))).Append("</span></div>");
    }

    private static string BuildSettingsHeader(DevelopmentSettingsDescriptor descriptor)
    {
        var markup = new StringBuilder();
        var scope = descriptor.Attribute.Scope;
        var prefix = $"{ScopeToken(scope)}:{descriptor.Attribute.Id}";
        markup.Append("<div><div class=\"settings-heading\">")
            .Append(EscapeSettings(descriptor.DisplayName))
            .Append("</div><div class=\"settings-id\">")
            .Append(EscapeSettings(descriptor.Attribute.Id)).Append(" @ ")
            .Append(descriptor.Attribute.SchemaVersion).Append("</div></div>")
            .Append("<div class=\"grow\" />")
            .Append(SettingsButton("Undo", "undo", prefix, descriptor.Undo.Count == 0))
            .Append(SettingsButton("Redo", "redo", prefix, descriptor.Redo.Count == 0))
            .Append(SettingsButton("Revert", "revert", prefix, !descriptor.IsDirty))
            .Append(SettingsButton("Save", "save", prefix,
                !descriptor.IsDirty || descriptor.Path is null, primary: true));
        return markup.ToString();
    }

    private void AppendSettingsFields(StringBuilder markup,
        DevelopmentSettingsDescriptor descriptor, object owner,
        IReadOnlyList<DevelopmentSettingField> fields, string parentPath, int depth)
    {
        foreach (var field in fields)
        {
            var path = parentPath.Length == 0 ? field.Attribute.Id :
                parentPath + "/" + field.Attribute.Id;
            var value = field.Property.GetValue(owner);
            var type = Nullable.GetUnderlyingType(field.Property.PropertyType) ??
                field.Property.PropertyType;
            var label = string.IsNullOrWhiteSpace(field.Attribute.DisplayName)
                ? field.Property.Name : field.Attribute.DisplayName!;
            if (IsSettingsObject(type) && value is not null)
            {
                markup.Append("<div class=\"settings-group\" style=\"margin-left:")
                    .Append(depth * 10).Append("px;\"><div class=\"settings-group-title\">")
                    .Append(EscapeSettings(label)).Append("</div>");
                AppendSettingsFields(markup, descriptor, value,
                    GetSettingFields(type), path, depth + 1);
                markup.Append("</div>");
                continue;
            }
            markup.Append("<div class=\"settings-field\"><div class=\"settings-field-copy\">")
                .Append("<label>").Append(EscapeSettings(label)).Append("</label>");
            if (!string.IsNullOrWhiteSpace(field.Attribute.Description))
                markup.Append("<span>").Append(EscapeSettings(field.Attribute.Description!))
                    .Append("</span>");
            markup.Append("</div><div class=\"settings-control\">");
            var action = $"editor.settings-change:{ScopeToken(descriptor.Attribute.Scope)}:" +
                $"{descriptor.Attribute.Id}:{path}:";
            if (type == typeof(bool) && Nullable.GetUnderlyingType(field.Property.PropertyType) is null)
            {
                var enabled = value is true;
                markup.Append("<button id=\"setting-").Append(EscapeAttribute(path))
                    .Append("\" class=\"settings-toggle")
                    .Append(enabled ? " enabled" : string.Empty).Append("\" action=\"")
                    .Append(action).Append(enabled ? "false" : "true").Append("\">")
                    .Append(enabled ? "On" : "Off").Append("</button>");
            }
            else if (type.IsEnum && Nullable.GetUnderlyingType(field.Property.PropertyType) is null)
            {
                var names = Enum.GetNames(type);
                var current = value?.ToString() ?? names[0];
                var index = Array.IndexOf(names, current);
                var next = names[(index + 1 + names.Length) % names.Length];
                markup.Append("<button id=\"setting-").Append(EscapeAttribute(path))
                    .Append("\" class=\"settings-enum\" action=\"")
                    .Append(action).Append(EscapeSettings(next)).Append("\">")
                    .Append(EscapeSettings(current)).Append("</button>");
            }
            else
            {
                markup.Append("<input id=\"setting-").Append(EscapeAttribute(path))
                    .Append("\" value=\"")
                    .Append(EscapeAttribute(_developmentSettings!.EditorValue(
                        field.Property.PropertyType, value)))
                    .Append("\" data-change-action=\"").Append(action).Append("\" />");
            }
            markup.Append("</div></div>");
        }
    }

    private void ApplySettingsEdit(SettingsScope scope, string id,
        string path, string value)
    {
        var error = "Development Settings are not initialized";
        if (_developmentSettings is null ||
            !_developmentSettings.TrySet(scope, id, path, value, out error))
        {
            SetStatus($"Settings change rejected: {error}");
            _nativeApi.SetHostMarkup(ScopeHost(scope), "settings-feedback", $"<span>{EscapeSettings(error)}</span>");
            return;
        }
        // Keep the input element alive while RmlUi is processing its Change event.
        // Replacing the whole document here loses focus and the remaining typed text.
        var descriptor = _developmentSettings.Find(scope, id)!;
        _nativeApi.SetHostMarkup(ScopeHost(scope), "settings-header", BuildSettingsHeader(descriptor));
        _nativeApi.SetHostMarkup(ScopeHost(scope), "settings-feedback",
            descriptor.IsDirty ? "<span>Unsaved changes</span>" : "<span>Saved</span>");
        var fieldType = descriptor.Type;
        foreach (var segment in path.Split('/'))
            fieldType = GetSettingFields(fieldType).First(field => field.Attribute.Id == segment)
                .Property.PropertyType;
        if (fieldType == typeof(bool) || fieldType.IsEnum)
        {
            RefreshSettingsWindow(scope);
        }
        MarkExtensionUiDirty();
        SetStatus($"{ScopeTitle(scope)} changed · {id}/{path}");
    }

    private void ApplySettingsOperation(SettingsScope scope, string id,
        string operation)
    {
        if (_developmentSettings is null) return;
        var saveError = string.Empty;
        bool success;
        try
        {
            success = operation switch
            {
                "save" => _developmentSettings.Save(scope, id, out saveError),
                "revert" => _developmentSettings.Revert(scope, id),
                "undo" => _developmentSettings.Undo(scope, id),
                "redo" => _developmentSettings.Redo(scope, id),
                _ => false,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or InvalidOperationException or FormatException)
        {
            _nativeApi.SetHostMarkup(ScopeHost(scope), "settings-feedback",
                $"<span>{EscapeSettings(exception.Message)}</span>");
            SetStatus($"Settings operation failed: {exception.Message}");
            return;
        }
        if (operation == "save" && !success)
        {
            SetStatus($"Settings could not be saved: {saveError}");
            _nativeApi.SetHostMarkup(ScopeHost(scope), "settings-feedback", $"<span>{EscapeSettings(saveError)}</span>");
            return;
        }
        RefreshSettingsWindow(scope);
        MarkExtensionUiDirty();
        SetStatus(success ? $"{ScopeTitle(scope)} {operation} complete" :
            $"Nothing to {operation} in {ScopeTitle(scope)}");
    }

    private void EnsureActiveSettingsPage(SettingsScope scope)
    {
        if (_developmentSettings is null) return;
        if (_activeSettingsPages.TryGetValue(scope, out var id) &&
            _developmentSettings.Find(scope, id) is not null) return;
        var first = _developmentSettings.Descriptors.FirstOrDefault(value =>
            value.Attribute.Scope == scope);
        if (first is not null) _activeSettingsPages[scope] = first.Attribute.Id;
        else _activeSettingsPages.Remove(scope);
    }

    private static IReadOnlyList<DevelopmentSettingField> GetSettingFields(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => new
            {
                Property = property,
                Attribute = property.GetCustomAttribute<SettingAttribute>(inherit: true),
            })
            .Where(value => value.Attribute is not null)
            .Select(value => new DevelopmentSettingField(value.Property, value.Attribute!))
            .OrderBy(value => value.Attribute.Order)
            .ThenBy(value => value.Attribute.Id, StringComparer.Ordinal).ToArray();

    private static bool IsSettingsObject(Type type) =>
        !type.IsPrimitive && !type.IsEnum && type != typeof(string) &&
        type != typeof(decimal) && type != typeof(Guid) &&
        type != typeof(DateTime) && type != typeof(DateTimeOffset) &&
        type != typeof(TimeSpan) && type != typeof(Uri) &&
        !type.IsArray && !typeof(System.Collections.IList).IsAssignableFrom(type) &&
        !typeof(System.Collections.IDictionary).IsAssignableFrom(type);

    private static string SettingsButton(string label, string operation,
        string prefix, bool disabled, bool primary = false) =>
        $"<button id=\"settings-{operation}\" class=\"settings-action{(primary ? " primary" : string.Empty)}" +
        $"{(disabled ? " disabled\" disabled=\"true" : string.Empty)}\" " +
        $"action=\"editor.settings-{operation}:{prefix}\">{label}</button>";

    private static string ScopeHost(SettingsScope scope) => scope == SettingsScope.Engine
        ? EngineSettingsHostId : ProjectSettingsHostId;
    private static string ScopeTitle(SettingsScope scope) => scope == SettingsScope.Engine
        ? "Engine Settings" : "Project Settings";
    private static string ScopeToken(SettingsScope scope) => scope == SettingsScope.Engine
        ? "engine" : "project";
    private static bool TryScopeFromHost(string hostId, out SettingsScope scope)
    {
        if (hostId == EngineSettingsHostId) { scope = SettingsScope.Engine; return true; }
        if (hostId == ProjectSettingsHostId) { scope = SettingsScope.Project; return true; }
        scope = default;
        return false;
    }
    private static bool TryScope(string value, out SettingsScope scope)
    {
        if (value == "engine") { scope = SettingsScope.Engine; return true; }
        if (value == "project") { scope = SettingsScope.Project; return true; }
        scope = default;
        return false;
    }
    private static string EscapeSettings(string value) => WebUtility.HtmlEncode(value);
    private static string EscapeAttribute(string value) => WebUtility.HtmlEncode(value);
}
