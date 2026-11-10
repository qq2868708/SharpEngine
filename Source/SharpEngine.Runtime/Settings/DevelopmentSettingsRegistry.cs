using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Components;

namespace SharpEngine.Settings;

internal sealed record DevelopmentSettingField(PropertyInfo Property,
    SettingAttribute Attribute);

internal sealed class DevelopmentSettingsDescriptor
{
    public required Type Type { get; init; }
    public required DevelopmentSettingsAttribute Attribute { get; init; }
    public required IReadOnlyList<DevelopmentSettingField> Fields { get; init; }
    public required DevelopmentSettings Defaults { get; init; }
    private DevelopmentSettings _current = null!;
    public required DevelopmentSettings Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
    public required DevelopmentSettings Saved { get; set; }
    public string? Path { get; init; }
    public long Revision { get; set; }
    public bool IsDirty { get; set; }
    public string? SavedFileText { get; set; }
    public string? Diagnostic { get; set; }
    public Stack<DevelopmentSettings> Undo { get; } = new();
    public Stack<DevelopmentSettings> Redo { get; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(Attribute.DisplayName)
        ? Type.Name.EndsWith("Settings", StringComparison.Ordinal)
            ? Type.Name[..^"Settings".Length] : Type.Name
        : Attribute.DisplayName!;
    public string Category => string.IsNullOrWhiteSpace(Attribute.Category)
        ? "General" : Attribute.Category!;
}

internal sealed class DevelopmentSettingsRegistry : IDevelopmentSettingsProvider,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default)
    {
        WriteIndented = true,
    };

    private readonly string? _projectRoot;
    private readonly string? _engineRoot;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, DevelopmentSettingsDescriptor> _byType = new();
    private readonly Dictionary<string, DevelopmentSettingsDescriptor> _byId =
        new(StringComparer.Ordinal);
    private readonly HashSet<Assembly> _scanned = new();
    private bool _disposed;

    public DevelopmentSettingsRegistry(string? projectRoot, string? engineRoot = null, bool install = true)
    {
        _engineRoot = engineRoot;
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? null : Path.GetFullPath(projectRoot);
        if (install) DevelopmentSettings.InstallProvider(this);
    }

    internal void Activate() => DevelopmentSettings.InstallProvider(this);

    internal Action CaptureReloadRestorePoint()
    {
        var snapshots = Descriptors.Select(page => (Page: page,
            Current: SettingsValueCodec.Clone(page.Current, page.Fields),
            Saved: SettingsValueCodec.Clone(page.Saved, page.Fields),
            Undo: page.Undo.ToArray(), Redo: page.Redo.ToArray(),
            page.Revision, page.IsDirty, page.SavedFileText, page.Diagnostic)).ToArray();
        return () =>
        {
            foreach (var snapshot in snapshots)
            {
                var page = snapshot.Page;
                page.Current = snapshot.Current; page.Saved = snapshot.Saved;
                page.Undo.Clear(); foreach (var state in snapshot.Undo.Reverse()) page.Undo.Push(state);
                page.Redo.Clear(); foreach (var state in snapshot.Redo.Reverse()) page.Redo.Push(state);
                page.Revision = snapshot.Revision; page.IsDirty = snapshot.IsDirty;
                page.SavedFileText = snapshot.SavedFileText; page.Diagnostic = snapshot.Diagnostic;
            }
        };
    }

    internal void CopyStateFrom(DevelopmentSettingsRegistry previous)
    {
        foreach (var old in previous.Descriptors)
        {
            if (!_byId.TryGetValue(old.Attribute.Id, out var next))
                throw new InvalidDataException($"Reload would remove settings '{old.Attribute.Id}'");
            if (old.Attribute.SchemaVersion != next.Attribute.SchemaVersion ||
                old.Attribute.Scope != next.Attribute.Scope)
                throw new InvalidDataException($"Settings schema changed: {old.Attribute.Id}");
            DevelopmentSettings Convert(DevelopmentSettings value)
            {
                var converted = (DevelopmentSettings)SettingsValueCodec.ReadObject(next.Type,
                    SettingsValueCodec.WriteObject(value, old.Fields), next.Defaults);
                SettingsValueCodec.ValidateValues(converted, next.Fields, next.Attribute.Id);
                return converted;
            }
            next.Current = Convert(old.Current);
            next.Saved = Convert(old.Saved);
            SettingsValueCodec.ValidateValues(next.Current, next.Fields, next.Attribute.Id);
            foreach (var value in old.Undo.Reverse()) next.Undo.Push(Convert(value));
            foreach (var value in old.Redo.Reverse()) next.Redo.Push(Convert(value));
            next.Revision = old.Revision;
            next.IsDirty = old.IsDirty;
            next.SavedFileText = old.SavedFileText;
            next.Diagnostic = old.Diagnostic;
        }
    }

    public IReadOnlyList<DevelopmentSettingsDescriptor> Descriptors => _byId.Values
        .OrderBy(value => value.Attribute.Scope)
        .ThenBy(value => value.Attribute.Order)
        .ThenBy(value => value.Category, StringComparer.Ordinal)
        .ThenBy(value => value.DisplayName, StringComparer.Ordinal)
        .ThenBy(value => value.Attribute.Id, StringComparer.Ordinal)
        .ToArray();

    public DevelopmentSettings Get(Type settingsType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _byType.TryGetValue(settingsType, out var descriptor)
            ? SettingsValueCodec.Clone(descriptor.Current, descriptor.Fields)
            : throw new KeyNotFoundException(
                $"Development Settings type '{settingsType.FullName}' is not registered");
    }

    public void RegisterAssembly(Assembly assembly, SettingsScope expectedScope)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(assembly);
        RequireOwnerThread();
        if (_scanned.Contains(assembly)) return;
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception)
        {
            throw new InvalidDataException(
                $"Could not inspect Development Settings in '{assembly.FullName}'",
                exception.LoaderExceptions.FirstOrDefault() ?? exception);
        }
        var existing = _byId.Keys.ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var type in types.OrderBy(value => value.FullName, StringComparer.Ordinal))
            {
                var attribute = type.GetCustomAttribute<DevelopmentSettingsAttribute>();
                if (attribute is null) continue;
                if (attribute.Scope != expectedScope)
                    throw new InvalidDataException($"Settings '{attribute.Id}' scope must be {expectedScope}");
                Register(type, attribute);
            }
            _scanned.Add(assembly);
        }
        catch
        {
            foreach (var id in _byId.Keys.Where(id => !existing.Contains(id)).ToArray())
            {
                _byType.TryRemove(_byId[id].Type, out _);
                _byId.Remove(id);
            }
            throw;
        }
    }

    public DevelopmentSettingsDescriptor? Find(SettingsScope scope, string id) =>
        _byId.TryGetValue(id, out var descriptor) &&
        descriptor.Attribute.Scope == scope ? descriptor : null;

    public bool TrySet(SettingsScope scope, string id, string fieldPath,
        string serializedValue, out string error)
    {
        RequireOwnerThread();
        error = string.Empty;
        var descriptor = Find(scope, id);
        if (descriptor is null)
        {
            error = $"Settings '{id}' is not registered in {scope}";
            return false;
        }
        try
        {
            var values = SettingsValueCodec.WriteObject(descriptor.Current,
                descriptor.Fields);
            var segments = fieldPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) throw new InvalidDataException("A setting field path is required");
            var (property, target) = SettingsValueCodec.ResolvePath(
                descriptor.Type, values, segments);
            target[segments[^1]] = SettingsValueCodec.ParseEditorValue(
                serializedValue, property.PropertyType);
            var next = (DevelopmentSettings)SettingsValueCodec.ReadObject(
                descriptor.Type, values, descriptor.Defaults);
            SettingsValueCodec.ValidateValues(next, descriptor.Fields,
                descriptor.Attribute.Id);
            Commit(descriptor, next, recordUndo: true);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidDataException or JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }

    public bool Save(SettingsScope scope, string id, out string error)
    {
        RequireOwnerThread();
        error = string.Empty;
        var descriptor = Find(scope, id);
        if (descriptor is null)
        {
            error = $"Settings '{id}' is not registered in {scope}";
            return false;
        }
        if (descriptor.Path is null)
        {
            error = "Open a project before saving settings overrides";
            return false;
        }
        try
        {
            var diskText = File.Exists(descriptor.Path) ? File.ReadAllText(descriptor.Path) : null;
            if (diskText != descriptor.SavedFileText)
                throw new InvalidDataException("Settings changed on disk. Revert before saving to reload the external version.");
            var document = SettingsValueCodec.WriteDocument(descriptor);
            var savedText = document.ToJsonString(JsonOptions) + "\n";
            var directory = Path.GetDirectoryName(descriptor.Path)!;
            Directory.CreateDirectory(directory);
            var temporary = descriptor.Path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, savedText);
                _ = SettingsValueCodec.ReadDocument(descriptor.Type, descriptor.Attribute,
                    descriptor.Fields, File.ReadAllText(temporary), descriptor.Defaults,
                    temporary);
                File.Move(temporary, descriptor.Path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            descriptor.Saved = SettingsValueCodec.Clone(descriptor.Current,
                descriptor.Fields);
            descriptor.IsDirty = false;
            descriptor.SavedFileText = savedText;
            descriptor.Diagnostic = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            error = exception.Message;
            return false;
        }
    }

    public bool Revert(SettingsScope scope, string id)
    {
        RequireOwnerThread();
        var descriptor = Find(scope, id);
        if (descriptor is null) return false;
        if (descriptor.Path is not null)
        {
            var text = File.Exists(descriptor.Path) ? File.ReadAllText(descriptor.Path) : null;
            if (text != descriptor.SavedFileText)
            {
                var loaded = text is null ? SettingsValueCodec.Clone(descriptor.Defaults, descriptor.Fields) :
                    (DevelopmentSettings)SettingsValueCodec.ReadDocument(descriptor.Type, descriptor.Attribute,
                        descriptor.Fields, text, descriptor.Defaults, descriptor.Path);
                Commit(descriptor, loaded, recordUndo: true);
                descriptor.Saved = SettingsValueCodec.Clone(loaded, descriptor.Fields);
                descriptor.SavedFileText = text;
                descriptor.Diagnostic = null;
                descriptor.IsDirty = false;
                return true;
            }
        }
        if (!descriptor.IsDirty) return false;
        Commit(descriptor, SettingsValueCodec.Clone(descriptor.Saved,
            descriptor.Fields), recordUndo: true);
        descriptor.IsDirty = false;
        return true;
    }

    public bool ReloadChangedFiles()
    {
        RequireOwnerThread();
        var changed = false;
        foreach (var page in Descriptors.Where(page => page.Path is not null))
        {
            var previousDiagnostic = page.Diagnostic;
            try
            {
                var text = File.Exists(page.Path) ? File.ReadAllText(page.Path!) : null;
                if (text == page.SavedFileText)
                {
                    page.Diagnostic = null;
                    changed |= previousDiagnostic is not null;
                    continue;
                }
                if (page.IsDirty)
                    throw new InvalidDataException("Settings changed on disk while local edits are unsaved. Revert to load the external version.");
                Revert(page.Attribute.Scope, page.Attribute.Id);
                changed = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or InvalidOperationException or FormatException)
            {
                page.Diagnostic = exception.Message;
            }
            changed |= previousDiagnostic != page.Diagnostic;
        }
        return changed;
    }

    public bool Undo(SettingsScope scope, string id)
    {
        RequireOwnerThread();
        var descriptor = Find(scope, id);
        if (descriptor is null || descriptor.Undo.Count == 0) return false;
        descriptor.Redo.Push(SettingsValueCodec.Clone(descriptor.Current,
            descriptor.Fields));
        descriptor.Current = descriptor.Undo.Pop();
        descriptor.Revision++;
        descriptor.IsDirty = !SettingsValueCodec.SemanticallyEqual(
            descriptor.Current, descriptor.Saved, descriptor.Fields);
        return true;
    }

    public bool Redo(SettingsScope scope, string id)
    {
        RequireOwnerThread();
        var descriptor = Find(scope, id);
        if (descriptor is null || descriptor.Redo.Count == 0) return false;
        descriptor.Undo.Push(SettingsValueCodec.Clone(descriptor.Current,
            descriptor.Fields));
        descriptor.Current = descriptor.Redo.Pop();
        descriptor.Revision++;
        descriptor.IsDirty = !SettingsValueCodec.SemanticallyEqual(
            descriptor.Current, descriptor.Saved, descriptor.Fields);
        return true;
    }

    public JsonObject Describe()
    {
        var pages = new JsonArray();
        foreach (var descriptor in Descriptors)
        {
            var fields = new JsonArray();
            foreach (var field in descriptor.Fields)
                fields.Add(SettingsValueCodec.DescribeField(field));
            pages.Add(new JsonObject
            {
                ["id"] = descriptor.Attribute.Id,
                ["scope"] = descriptor.Attribute.Scope.ToString().ToLowerInvariant(),
                ["schemaVersion"] = descriptor.Attribute.SchemaVersion,
                ["displayName"] = descriptor.DisplayName,
                ["category"] = descriptor.Category,
                ["revision"] = descriptor.Revision,
                ["dirty"] = descriptor.IsDirty,
                ["diagnostic"] = descriptor.Diagnostic,
                ["path"] = descriptor.Path,
                ["fields"] = fields,
                ["schema"] = SettingsValueCodec.Schema(descriptor.Type, Root: true),
                ["values"] = SettingsValueCodec.WriteObject(descriptor.Current,
                    descriptor.Fields),
            });
        }
        return new JsonObject { ["schemaVersion"] = 1, ["pages"] = pages };
    }

    public string EditorValue(Type type, object? value) =>
        SettingsValueCodec.EditorValue(type, value);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DevelopmentSettings.RemoveProvider(this);
        _byType.Clear();
        _byId.Clear();
        _scanned.Clear();
    }

    private void Register(Type type, DevelopmentSettingsAttribute attribute)
    {
        ValidateId(attribute.Id, "settings");
        if (!type.IsClass || type.IsAbstract || !typeof(DevelopmentSettings).IsAssignableFrom(type))
            throw new InvalidDataException(
                $"Settings '{attribute.Id}' must be a concrete DevelopmentSettings class");
        if (type.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidDataException(
                $"Settings '{attribute.Id}' requires a public parameterless constructor");
        if (_byId.ContainsKey(attribute.Id))
            throw new InvalidDataException($"Duplicate Development Settings ID '{attribute.Id}'");
        if (_byType.ContainsKey(type))
            throw new InvalidDataException($"Settings type '{type.FullName}' is already registered");
        var fields = SettingsValueCodec.GetFields(type);
        if (fields.Count == 0)
            throw new InvalidDataException($"Settings '{attribute.Id}' has no [Setting] properties");
        SettingsValueCodec.ValidateSchema(type, attribute.Id);
        var defaults = (DevelopmentSettings)Activator.CreateInstance(type)!;
        SettingsValueCodec.ValidateValues(defaults, fields, attribute.Id);
        if (attribute.Scope == SettingsScope.Engine && _engineRoot is not null)
        {
            var defaultsPath = Path.Combine(_engineRoot, "Config", "EngineSettings", attribute.Id + ".json");
            if (File.Exists(defaultsPath))
                defaults = (DevelopmentSettings)SettingsValueCodec.ReadDocument(type, attribute,
                    fields, File.ReadAllText(defaultsPath), defaults, defaultsPath);
        }
        var path = GetPath(attribute.Scope, attribute.Id);
        var fileText = path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
        var current = fileText is not null
            ? (DevelopmentSettings)SettingsValueCodec.ReadDocument(type, attribute,
                fields, fileText, defaults, path!)
            : SettingsValueCodec.Clone(defaults, fields);
        var descriptor = new DevelopmentSettingsDescriptor
        {
            Type = type,
            Attribute = attribute,
            Fields = fields,
            Defaults = defaults,
            Current = current,
            Saved = SettingsValueCodec.Clone(current, fields),
            Path = path,
            SavedFileText = fileText,
        };
        if (!_byType.TryAdd(type, descriptor))
            throw new InvalidDataException($"Settings type '{type.FullName}' is already registered");
        _byId.Add(attribute.Id, descriptor);
    }

    private void Commit(DevelopmentSettingsDescriptor descriptor,
        DevelopmentSettings next, bool recordUndo)
    {
        if (SettingsValueCodec.SemanticallyEqual(descriptor.Current, next,
                descriptor.Fields)) return;
        if (recordUndo)
        {
            descriptor.Undo.Push(SettingsValueCodec.Clone(descriptor.Current,
                descriptor.Fields));
            descriptor.Redo.Clear();
        }
        descriptor.Current = next;
        descriptor.Revision++;
        descriptor.IsDirty = !SettingsValueCodec.SemanticallyEqual(
            descriptor.Current, descriptor.Saved, descriptor.Fields);
    }

    private void RequireOwnerThread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Settings mutations must run on the owning editor thread");
    }

    private string? GetPath(SettingsScope scope, string id) => _projectRoot is null
        ? null
        : Path.Combine(_projectRoot, "Config",
            scope == SettingsScope.Engine ? "EngineSettings" : "ProjectSettings",
            id + ".json");

    private static void ValidateId(string id, string kind, bool allowUppercase = false)
    {
        if (id.Length > 128 || id[0] is '.' or '-' || id[^1] is '.' or '-' ||
            id.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' ||
                allowUppercase && character is >= 'A' and <= 'Z')))
            throw new InvalidDataException(
                $"{kind} ID '{id}' contains unsupported characters");
    }

    private static class SettingsValueCodec
    {
        public static IReadOnlyList<DevelopmentSettingField> GetFields(Type type)
        {
            var fields = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => (Property: property,
                    Attribute: property.GetCustomAttribute<SettingAttribute>(inherit: true)))
                .Where(value => value.Attribute is not null)
                .Select(value => new DevelopmentSettingField(value.Property, value.Attribute!))
                .OrderBy(value => value.Attribute.Order)
                .ThenBy(value => value.Attribute.Id, StringComparer.Ordinal)
                .ToArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                ValidateId(field.Attribute.Id, "setting field", allowUppercase: true);
                if (!ids.Add(field.Attribute.Id))
                    throw new InvalidDataException(
                        $"Duplicate setting field ID '{field.Attribute.Id}' on '{type.FullName}'");
                if (field.Property.GetMethod?.IsPublic != true ||
                    field.Property.SetMethod?.IsPublic != true ||
                    field.Property.GetIndexParameters().Length != 0)
                    throw new InvalidDataException(
                        $"Setting field '{type.FullName}.{field.Property.Name}' requires public get/init or get/set accessors");
            }
            return fields;
        }

        public static void ValidateSchema(Type type, string settingsId) =>
            ValidateType(type, new HashSet<Type>(), settingsId);

        private static void ValidateType(Type type, HashSet<Type> stack, string path)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (IsScalar(type) || type.IsEnum || IsNumericVector(type))
            {
                return;
            }
            if (type == typeof(object) || type.IsInterface || type.IsAbstract ||
                type.IsPointer || type.IsByRef || type.ContainsGenericParameters ||
                typeof(Delegate).IsAssignableFrom(type))
                throw new InvalidDataException(
                    $"Setting schema '{path}' contains unsupported type '{type.FullName}'");
            if (!stack.Add(type))
                throw new InvalidDataException(
                    $"Setting schema '{path}' contains a recursive type cycle at '{type.FullName}'");
            try
            {
                if (type.IsArray)
                {
                    if (type.GetArrayRank() != 1)
                        throw new InvalidDataException($"Setting '{path}' requires a one-dimensional array");
                    ValidateType(type.GetElementType()!, stack, path + "[]");
                    return;
                }
                if (TryCollectionElement(type, out var element))
                {
                    if (type.GetConstructor(Type.EmptyTypes) is null)
                        throw new InvalidDataException(
                            $"Setting collection '{path}' requires a public parameterless constructor");
                    ValidateType(element!, stack, path + "[]");
                    return;
                }
                if (TryDictionaryValue(type, out var valueType))
                {
                    if (type.GetConstructor(Type.EmptyTypes) is null)
                        throw new InvalidDataException(
                            $"Setting dictionary '{path}' requires a public parameterless constructor");
                    ValidateType(valueType!, stack, path + "{}");
                    return;
                }
                if (type.GetConstructor(Type.EmptyTypes) is null && !type.IsValueType)
                    throw new InvalidDataException(
                        $"Setting object '{path}' requires a public parameterless constructor");
                var fields = GetFields(type);
                if (fields.Count == 0)
                    throw new InvalidDataException(
                        $"Setting object '{path}' has no [Setting] properties");
                foreach (var field in fields)
                    ValidateType(field.Property.PropertyType, stack,
                        path + "." + field.Attribute.Id);
            }
            finally { stack.Remove(type); }
        }

        public static JsonObject WriteDocument(DevelopmentSettingsDescriptor descriptor) => new()
        {
            ["schemaVersion"] = descriptor.Attribute.SchemaVersion,
            ["settingsId"] = descriptor.Attribute.Id,
            ["values"] = WriteObject(descriptor.Current, descriptor.Fields),
        };

        public static object ReadDocument(Type type,
            DevelopmentSettingsAttribute attribute,
            IReadOnlyList<DevelopmentSettingField> fields, string json,
            DevelopmentSettings defaults, string source)
        {
            var root = JsonNode.Parse(json) as JsonObject ??
                throw new InvalidDataException($"Settings '{source}' must contain a JSON object");
            RequireOnly(root, source, "schemaVersion", "settingsId", "values");
            if (root["schemaVersion"]?.GetValue<int>() != attribute.SchemaVersion)
                throw new InvalidDataException(
                    $"Settings '{source}' schemaVersion must be {attribute.SchemaVersion}");
            if (!string.Equals(root["settingsId"]?.GetValue<string>(), attribute.Id,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Settings '{source}' settingsId must be '{attribute.Id}'");
            var values = root["values"] as JsonObject ??
                throw new InvalidDataException($"Settings '{source}' requires object 'values'");
            var result = ReadObject(type, values, defaults);
            ValidateValues(result, fields, attribute.Id);
            return result;
        }

        public static JsonObject WriteObject(object value,
            IReadOnlyList<DevelopmentSettingField>? knownFields = null)
        {
            var result = new JsonObject();
            foreach (var field in knownFields ?? GetFields(value.GetType()))
                result[field.Attribute.Id] = WriteValue(
                    field.Property.PropertyType, field.Property.GetValue(value));
            return result;
        }

        public static object ReadObject(Type type, JsonObject values,
            object? baseValue = null)
        {
            var fields = GetFields(type);
            RequireOnly(values, type.FullName ?? type.Name,
                fields.Select(field => field.Attribute.Id).ToArray());
            var result = Activator.CreateInstance(type) ??
                throw new InvalidDataException($"Could not create setting object '{type.FullName}'");
            foreach (var field in fields)
            {
                var fallback = baseValue is null ? field.Property.GetValue(result) :
                    field.Property.GetValue(baseValue);
                if (!values.TryGetPropertyValue(field.Attribute.Id, out var node))
                    throw new InvalidDataException(
                        $"Setting object '{type.FullName}' is missing required field '{field.Attribute.Id}'");
                field.Property.SetValue(result, ReadValue(field.Property.PropertyType,
                    node, fallback));
            }
            return result;
        }

        public static DevelopmentSettings Clone(DevelopmentSettings value,
            IReadOnlyList<DevelopmentSettingField> fields) =>
            (DevelopmentSettings)ReadObject(value.GetType(), WriteObject(value, fields), value);

        public static bool SemanticallyEqual(DevelopmentSettings left,
            DevelopmentSettings right, IReadOnlyList<DevelopmentSettingField> fields) =>
            JsonNode.DeepEquals(WriteObject(left, fields), WriteObject(right, fields));

        public static (PropertyInfo Property, JsonObject Target) ResolvePath(
            Type rootType, JsonObject values, IReadOnlyList<string> segments)
        {
            var currentType = rootType;
            var current = values;
            for (var index = 0; index < segments.Count; index++)
            {
                var field = GetFields(currentType).FirstOrDefault(value =>
                    value.Attribute.Id == segments[index]) ??
                    throw new InvalidDataException(
                        $"Unknown setting field '{string.Join('/', segments.Take(index + 1))}'");
                if (index == segments.Count - 1) return (field.Property, current);
                currentType = Nullable.GetUnderlyingType(field.Property.PropertyType) ??
                    field.Property.PropertyType;
                current = current[segments[index]] as JsonObject ??
                    throw new InvalidDataException(
                        $"Setting field '{segments[index]}' is not an object");
            }
            throw new InvalidDataException("A setting field path is required");
        }

        public static JsonNode? ParseEditorValue(string value, Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying is not null && string.Equals(value, "null",
                    StringComparison.OrdinalIgnoreCase)) return null;
            type = underlying ?? type;
            if (type == typeof(string)) return JsonValue.Create(value);
            if (type.IsEnum)
            {
                if (!Enum.TryParse(type, value, ignoreCase: false, out var parsed) ||
                    !Enum.IsDefined(type, parsed!))
                    throw new InvalidDataException(
                        $"'{value}' is not a valid {type.Name} value");
                return JsonValue.Create(value);
            }
            try { return JsonNode.Parse(value); }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"'{value}' is not valid JSON for {type.Name}", exception);
            }
        }

        public static string EditorValue(Type type, object? value)
        {
            if (value is null) return "null";
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string)) return (string)value;
            if (type.IsEnum) return value.ToString()!;
            if (type == typeof(bool)) return (bool)value ? "true" : "false";
            return WriteValue(type, value)?.ToJsonString() ?? "null";
        }

        public static void ValidateValues(object value,
            IReadOnlyList<DevelopmentSettingField> fields, string path)
        {
            foreach (var field in fields)
            {
                var fieldValue = field.Property.GetValue(value);
                var fieldPath = path + "." + field.Attribute.Id;
                if (fieldValue is IConvertible convertible &&
                    !double.IsNaN(field.Attribute.Minimum) &&
                    Convert.ToDouble(convertible, CultureInfo.InvariantCulture) <
                        field.Attribute.Minimum)
                    throw new InvalidDataException(
                        $"Setting '{fieldPath}' must be at least {field.Attribute.Minimum}");
                if (fieldValue is IConvertible maximumConvertible &&
                    !double.IsNaN(field.Attribute.Maximum) &&
                    Convert.ToDouble(maximumConvertible, CultureInfo.InvariantCulture) >
                        field.Attribute.Maximum)
                    throw new InvalidDataException(
                        $"Setting '{fieldPath}' must be at most {field.Attribute.Maximum}");
                ValidateValue(field.Property.PropertyType, fieldValue, fieldPath);
            }
        }

        private static void ValidateValue(Type Type, object? Value, string Path)
        {
            if (Value is null)
            {
                return;
            }
            Type = Nullable.GetUnderlyingType(Type) ?? Type;
            if (Value is float Single && !float.IsFinite(Single) ||
                Value is double Number && !double.IsFinite(Number))
            {
                throw new InvalidDataException($"Setting '{Path}' must be finite");
            }
            if (IsNumericVector(Type))
            {
                var Components = GetNumericComponents(Type, Value);
                if (Components.Any(Component => !float.IsFinite(Component)))
                {
                    throw new InvalidDataException(
                        $"Setting '{Path}' must contain finite components");
                }
                return;
            }
            if (Type.IsEnum && !Enum.IsDefined(Type, Value))
            {
                throw new InvalidDataException($"Setting '{Path}' has an undefined enum value");
            }
            if (IsScalar(Type) || Type.IsEnum)
            {
                return;
            }
            Type? Element = null;
            if (Type.IsArray || TryCollectionElement(Type, out Element))
            {
                Element ??= Type.GetElementType()!;
                var Index = 0;
                foreach (var Item in (IEnumerable)Value)
                {
                    ValidateValue(Element, Item, $"{Path}/{Index}");
                    Index++;
                }
                return;
            }
            if (TryDictionaryValue(Type, out var DictionaryValue))
            {
                foreach (DictionaryEntry Entry in (IDictionary)Value)
                {
                    ValidateValue(DictionaryValue!, Entry.Value, $"{Path}/{Entry.Key}");
                }
                return;
            }
            if (Value.GetType() != Type)
            {
                throw new InvalidDataException(
                    $"Setting '{Path}' cannot contain undeclared polymorphic types");
            }
            ValidateValues(Value, GetFields(Type), Path);
        }

        public static JsonObject DescribeField(DevelopmentSettingField field)
        {
            var type = Nullable.GetUnderlyingType(field.Property.PropertyType) ??
                field.Property.PropertyType;
            var result = new JsonObject
            {
                ["id"] = field.Attribute.Id,
                ["displayName"] = string.IsNullOrWhiteSpace(field.Attribute.DisplayName)
                    ? field.Property.Name : field.Attribute.DisplayName,
                ["description"] = field.Attribute.Description,
                ["type"] = TypeName(type),
            };
            if (!double.IsNaN(field.Attribute.Minimum))
                result["minimum"] = field.Attribute.Minimum;
            if (!double.IsNaN(field.Attribute.Maximum))
                result["maximum"] = field.Attribute.Maximum;
            if (type.IsEnum)
                result["enum"] = new JsonArray(Enum.GetNames(type)
                    .Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
            return result;
        }

        private static JsonNode? WriteValue(Type DeclaredType, object? Value)
        {
            if (Value is null)
            {
                return null;
            }
            var Type = Nullable.GetUnderlyingType(DeclaredType) ?? DeclaredType;
            if (Type.IsEnum)
            {
                return JsonValue.Create(Value.ToString());
            }
            if (IsNumericVector(Type))
            {
                return new JsonArray(GetNumericComponents(Type, Value)
                    .Select(Component => (JsonNode?)JsonValue.Create(Component)).ToArray());
            }
            if (IsScalar(Type))
            {
                return JsonSerializer.SerializeToNode(Value, Type, JsonOptions);
            }
            Type? ElementType = null;
            if (Type.IsArray || TryCollectionElement(Type, out ElementType))
            {
                ElementType ??= Type.GetElementType()!;
                var Array = new JsonArray();
                foreach (var Item in (IEnumerable)Value)
                {
                    Array.Add(WriteValue(ElementType, Item));
                }
                return Array;
            }
            if (TryDictionaryValue(Type, out var ValueType))
            {
                var Result = new JsonObject();
                var Dictionary = (IDictionary)Value;
                foreach (var Key in Dictionary.Keys.Cast<string>()
                             .OrderBy(Key => Key, StringComparer.Ordinal))
                {
                    Result[Key] = WriteValue(ValueType!, Dictionary[Key]);
                }
                return Result;
            }
            return WriteObject(Value);
        }

        private static object? ReadValue(Type DeclaredType, JsonNode? Node,
            object? Fallback)
        {
            var NullableType = Nullable.GetUnderlyingType(DeclaredType);
            if (Node is null)
            {
                if (!DeclaredType.IsValueType || NullableType is not null)
                {
                    return null;
                }
                throw new InvalidDataException(
                    $"Non-nullable setting type '{DeclaredType.Name}' cannot read null");
            }
            var Type = NullableType ?? DeclaredType;
            if (Type.IsEnum)
            {
                var Name = Node.GetValue<string>();
                if (!Enum.TryParse(Type, Name, ignoreCase: false, out var EnumValue) ||
                    !Enum.IsDefined(Type, EnumValue!))
                {
                    throw new InvalidDataException(
                        $"Unknown {Type.Name} setting value '{Name}'");
                }
                return EnumValue;
            }
            if (IsNumericVector(Type))
            {
                return ReadNumericVector(Type, Node);
            }
            if (IsScalar(Type))
            {
                return Node.Deserialize(Type, JsonOptions) ??
                    throw new InvalidDataException(
                        $"Could not deserialize setting value as '{Type.Name}'");
            }
            if (Type.IsArray)
            {
                var Source = Node as JsonArray ??
                    throw new InvalidDataException($"Setting value for '{Type.Name}' must be an array");
                var ElementType = Type.GetElementType()!;
                var Result = Array.CreateInstance(ElementType, Source.Count);
                for (var Index = 0; Index < Source.Count; Index++)
                {
                    Result.SetValue(ReadValue(ElementType, Source[Index], null), Index);
                }
                return Result;
            }
            if (TryCollectionElement(Type, out var CollectionElement))
            {
                var Source = Node as JsonArray ??
                    throw new InvalidDataException($"Setting value for '{Type.Name}' must be an array");
                var Result = (IList)Activator.CreateInstance(Type)!;
                foreach (var Item in Source)
                {
                    Result.Add(ReadValue(CollectionElement!, Item, null));
                }
                return Result;
            }
            if (TryDictionaryValue(Type, out var DictionaryValue))
            {
                var Source = Node as JsonObject ??
                    throw new InvalidDataException($"Setting value for '{Type.Name}' must be an object");
                var Result = (IDictionary)Activator.CreateInstance(Type)!;
                foreach (var Entry in Source.OrderBy(Entry => Entry.Key,
                             StringComparer.Ordinal))
                {
                    Result.Add(Entry.Key, ReadValue(DictionaryValue!, Entry.Value, null));
                }
                return Result;
            }
            return ReadObject(Type, Node as JsonObject ??
                throw new InvalidDataException($"Setting value for '{Type.Name}' must be an object"),
                Fallback);
        }

        private static bool IsScalar(Type type) =>
            type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr) ||
            type == typeof(string) || type == typeof(decimal) ||
            type == typeof(Guid) || type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
            type == typeof(Uri);

        public static JsonObject Schema(Type DeclaredType, bool Root = false)
        {
            var Type = Nullable.GetUnderlyingType(DeclaredType) ?? DeclaredType;
            JsonObject ResultSchema;
            if (Type.IsEnum)
            {
                ResultSchema = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(Enum.GetNames(Type)
                        .Select(Name => (JsonNode?)JsonValue.Create(Name)).ToArray()),
                };
            }
            else if (IsScalar(Type))
            {
                var ScalarType = Type == typeof(bool) ? "boolean" :
                    Type == typeof(float) || Type == typeof(double) || Type == typeof(decimal)
                        ? "number" :
                    Type.IsPrimitive && Type != typeof(char) ? "integer" : "string";
                ResultSchema = new JsonObject
                {
                    ["type"] = ScalarType,
                };
            }
            else if (Type.IsArray)
            {
                ResultSchema = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = Schema(Type.GetElementType()!),
                };
            }
            else if (TryCollectionElement(Type, out var Element))
            {
                ResultSchema = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = Schema(Element!),
                };
            }
            else if (IsNumericVector(Type))
            {
                var Dimension = NumericDimension(Type);
                ResultSchema = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "number",
                    },
                    ["minItems"] = Dimension,
                    ["maxItems"] = Dimension,
                };
            }
            else if (TryDictionaryValue(Type, out var DictionaryValue))
            {
                ResultSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = Schema(DictionaryValue!),
                };
            }
            else
            {
                var Properties = new JsonObject();
                var Required = new JsonArray();
                foreach (var Field in GetFields(Type))
                {
                    var FieldSchema = Schema(Field.Property.PropertyType);
                    if (!double.IsNaN(Field.Attribute.Minimum))
                    {
                        FieldSchema["minimum"] = Field.Attribute.Minimum;
                    }
                    if (!double.IsNaN(Field.Attribute.Maximum))
                    {
                        FieldSchema["maximum"] = Field.Attribute.Maximum;
                    }
                    Properties[Field.Attribute.Id] = FieldSchema;
                    Required.Add(Field.Attribute.Id);
                }
                ResultSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = Properties,
                    ["required"] = Required,
                    ["additionalProperties"] = false,
                };
            }
            if (!Root && (!DeclaredType.IsValueType ||
                Nullable.GetUnderlyingType(DeclaredType) is not null))
            {
                ResultSchema = new JsonObject
                {
                    ["anyOf"] = new JsonArray(ResultSchema,
                        new JsonObject { ["type"] = "null" }),
                };
            }
            if (Root)
            {
                ResultSchema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            }
            return ResultSchema;
        }

        private static bool IsStructured(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return !IsScalar(type) && !type.IsEnum;
        }

        private static bool TryCollectionElement(Type type, out Type? element)
        {
            element = null;
            if (!typeof(IList).IsAssignableFrom(type) || !type.IsGenericType ||
                type.GetGenericArguments().Length != 1) return false;
            element = type.GetGenericArguments()[0];
            return true;
        }

        private static bool TryDictionaryValue(Type type, out Type? value)
        {
            value = null;
            if (!typeof(IDictionary).IsAssignableFrom(type) || !type.IsGenericType)
                return false;
            var arguments = type.GetGenericArguments();
            if (arguments.Length != 2 || arguments[0] != typeof(string)) return false;
            value = arguments[1];
            return true;
        }

        private static string TypeName(Type Type)
        {
            if (Type.IsEnum)
            {
                return "enum";
            }
            if (Type == typeof(Vector2))
            {
                return "vector2";
            }
            if (Type == typeof(Vector3))
            {
                return "vector3";
            }
            if (Type == typeof(Vector4))
            {
                return "vector4";
            }
            if (Type == typeof(Quaternion))
            {
                return "quaternion";
            }
            if (Type == typeof(Color))
            {
                return "color";
            }
            if (Type == typeof(bool))
            {
                return "boolean";
            }
            if (Type == typeof(string) || Type == typeof(Uri))
            {
                return "string";
            }
            if (Type.IsPrimitive || Type == typeof(decimal))
            {
                return "number";
            }
            if (Type.IsArray || TryCollectionElement(Type, out _))
            {
                return "array";
            }
            return TryDictionaryValue(Type, out _) ? "map" : "object";
        }

        private static bool IsNumericVector(Type Type)
        {
            return Type == typeof(Vector2) || Type == typeof(Vector3) ||
                Type == typeof(Vector4) || Type == typeof(Quaternion) ||
                Type == typeof(Color);
        }

        private static int NumericDimension(Type Type)
        {
            if (Type == typeof(Vector2))
            {
                return 2;
            }
            if (Type == typeof(Vector3))
            {
                return 3;
            }
            return 4;
        }

        private static float[] GetNumericComponents(Type Type, object? Value)
        {
            return Value switch
            {
                Vector2 Vector => [Vector.X, Vector.Y],
                Vector3 Vector => [Vector.X, Vector.Y, Vector.Z],
                Vector4 Vector => [Vector.X, Vector.Y, Vector.Z, Vector.W],
                Quaternion Rotation => [Rotation.X, Rotation.Y, Rotation.Z, Rotation.W],
                Color Color => [Color.R, Color.G, Color.B, Color.A],
                _ => throw new InvalidDataException(
                    $"Setting value for '{Type.Name}' has an incompatible runtime type"),
            };
        }

        private static object ReadNumericVector(Type Type, JsonNode? Node)
        {
            var Dimension = NumericDimension(Type);
            float[] Components;
            if (Node is JsonArray Array)
            {
                if (Array.Count != Dimension)
                {
                    throw new InvalidDataException(
                        $"Setting value for '{Type.Name}' must contain {Dimension} components");
                }
                Components = Array.Select(Value => Value!.Deserialize<float>()).ToArray();
            }
            else if (Node is JsonObject Object)
            {
                var Names = Type == typeof(Color)
                    ? new[] { "r", "g", "b", "a" }
                    : new[] { "x", "y", "z", "w" };
                if (Object.Count == 0)
                {
                    Components = new float[Dimension];
                }
                else
                {
                    Components = new float[Dimension];
                    for (var Index = 0; Index < Dimension; Index++)
                    {
                        var Name = Names[Index];
                        if (!Object.TryGetPropertyValue(Name, out var Value) &&
                            !Object.TryGetPropertyValue(Name.ToUpperInvariant(), out Value))
                        {
                            throw new InvalidDataException(
                                $"Setting value for '{Type.Name}' is missing '{Name}'");
                        }
                        Components[Index] = Value!.Deserialize<float>();
                    }
                }
            }
            else
            {
                throw new InvalidDataException(
                    $"Setting value for '{Type.Name}' must be a component array");
            }
            if (Components.Any(Component => !float.IsFinite(Component)))
            {
                throw new InvalidDataException(
                    $"Setting value for '{Type.Name}' must contain finite components");
            }
            if (Type == typeof(Vector2))
            {
                return new Vector2(Components[0], Components[1]);
            }
            if (Type == typeof(Vector3))
            {
                return new Vector3(Components[0], Components[1], Components[2]);
            }
            if (Type == typeof(Vector4))
            {
                return new Vector4(Components[0], Components[1], Components[2], Components[3]);
            }
            if (Type == typeof(Quaternion))
            {
                return new Quaternion(Components[0], Components[1], Components[2], Components[3]);
            }
            return new Color(Components[0], Components[1], Components[2], Components[3]);
        }

        private static void RequireOnly(JsonObject value, string source,
            params string[] allowed)
        {
            var names = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var property in value)
                if (!names.Contains(property.Key))
                    throw new InvalidDataException(
                        $"Settings '{source}' contains unknown field '{property.Key}'");
        }
    }
}
