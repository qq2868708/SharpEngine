using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Modules;

namespace SharpEngine.Editor;

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

    public DevelopmentSettingsRegistry(string? projectRoot, string? engineRoot = null)
    {
        _engineRoot = engineRoot;
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? null : Path.GetFullPath(projectRoot);
        DevelopmentSettings.InstallProvider(this);
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
                ["schema"] = SettingsValueCodec.Schema(descriptor.Type, root: true),
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
            if (IsScalar(type) || type.IsEnum) return;
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

        private static void ValidateValue(Type type, object? value, string path)
        {
            if (value is null) return;
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (value is float single && !float.IsFinite(single) ||
                value is double number && !double.IsFinite(number))
                throw new InvalidDataException($"Setting '{path}' must be finite");
            if (type.IsEnum && !Enum.IsDefined(type, value))
                throw new InvalidDataException($"Setting '{path}' has an undefined enum value");
            if (IsScalar(type) || type.IsEnum) return;
            Type? element = null;
            if (type.IsArray || TryCollectionElement(type, out element))
            {
                element ??= type.GetElementType()!;
                var index = 0;
                foreach (var item in (IEnumerable)value) ValidateValue(element, item, $"{path}/{index++}");
            }
            else if (TryDictionaryValue(type, out var dictionaryValue))
            {
                foreach (DictionaryEntry entry in (IDictionary)value)
                    ValidateValue(dictionaryValue!, entry.Value, $"{path}/{entry.Key}");
            }
            else
            {
                if (value.GetType() != type)
                    throw new InvalidDataException($"Setting '{path}' cannot contain undeclared polymorphic types");
                ValidateValues(value, GetFields(type), path);
            }
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

        private static JsonNode? WriteValue(Type declaredType, object? value)
        {
            if (value is null) return null;
            var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            if (type.IsEnum) return JsonValue.Create(value.ToString());
            if (IsScalar(type)) return JsonSerializer.SerializeToNode(value, type, JsonOptions);
            Type? elementType = null;
            if (type.IsArray || TryCollectionElement(type, out elementType))
            {
                elementType ??= type.GetElementType()!;
                var array = new JsonArray();
                foreach (var item in (IEnumerable)value)
                    array.Add(WriteValue(elementType, item));
                return array;
            }
            if (TryDictionaryValue(type, out var valueType))
            {
                var result = new JsonObject();
                var dictionary = (IDictionary)value;
                foreach (var key in dictionary.Keys.Cast<string>().OrderBy(key => key, StringComparer.Ordinal))
                    result[key] = WriteValue(valueType!, dictionary[key]);
                return result;
            }
            return WriteObject(value);
        }

        private static object? ReadValue(Type declaredType, JsonNode? node,
            object? fallback)
        {
            var nullable = Nullable.GetUnderlyingType(declaredType);
            if (node is null)
            {
                if (!declaredType.IsValueType || nullable is not null) return null;
                throw new InvalidDataException(
                    $"Non-nullable setting type '{declaredType.Name}' cannot read null");
            }
            var type = nullable ?? declaredType;
            if (type.IsEnum)
            {
                var name = node.GetValue<string>();
                if (!Enum.TryParse(type, name, ignoreCase: false, out var value) ||
                    !Enum.IsDefined(type, value!))
                    throw new InvalidDataException(
                        $"Unknown {type.Name} setting value '{name}'");
                return value;
            }
            if (IsScalar(type))
                return node.Deserialize(type, JsonOptions) ??
                    throw new InvalidDataException(
                        $"Could not deserialize setting value as '{type.Name}'");
            if (type.IsArray)
            {
                var source = node as JsonArray ??
                    throw new InvalidDataException($"Setting value for '{type.Name}' must be an array");
                var elementType = type.GetElementType()!;
                var result = Array.CreateInstance(elementType, source.Count);
                for (var index = 0; index < source.Count; index++)
                    result.SetValue(ReadValue(elementType, source[index], null), index);
                return result;
            }
            if (TryCollectionElement(type, out var collectionElement))
            {
                var source = node as JsonArray ??
                    throw new InvalidDataException($"Setting value for '{type.Name}' must be an array");
                var result = (IList)Activator.CreateInstance(type)!;
                foreach (var item in source)
                    result.Add(ReadValue(collectionElement!, item, null));
                return result;
            }
            if (TryDictionaryValue(type, out var dictionaryValue))
            {
                var source = node as JsonObject ??
                    throw new InvalidDataException($"Setting value for '{type.Name}' must be an object");
                var result = (IDictionary)Activator.CreateInstance(type)!;
                foreach (var entry in source.OrderBy(entry => entry.Key,
                             StringComparer.Ordinal))
                    result.Add(entry.Key, ReadValue(dictionaryValue!, entry.Value, null));
                return result;
            }
            return ReadObject(type, node as JsonObject ??
                throw new InvalidDataException($"Setting value for '{type.Name}' must be an object"),
                fallback);
        }

        private static bool IsScalar(Type type) =>
            type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr) ||
            type == typeof(string) || type == typeof(decimal) ||
            type == typeof(Guid) || type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
            type == typeof(Uri);

        public static JsonObject Schema(Type declaredType, bool root = false)
        {
            var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            JsonObject schema;
            if (type.IsEnum)
                schema = new() { ["type"] = "string", ["enum"] = new JsonArray(
                    Enum.GetNames(type).Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()) };
            else if (IsScalar(type))
                schema = new() { ["type"] = type == typeof(bool) ? "boolean" :
                    type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? "number" :
                    type.IsPrimitive && type != typeof(char) ? "integer" : "string" };
            else if (type.IsArray)
                schema = new() { ["type"] = "array", ["items"] = Schema(type.GetElementType()!) };
            else if (TryCollectionElement(type, out var element))
                schema = new() { ["type"] = "array", ["items"] = Schema(element!) };
            else if (TryDictionaryValue(type, out var dictionaryValue))
                schema = new() { ["type"] = "object", ["additionalProperties"] = Schema(dictionaryValue!) };
            else
            {
                var properties = new JsonObject();
                var required = new JsonArray();
                foreach (var field in GetFields(type))
                {
                    var fieldSchema = Schema(field.Property.PropertyType);
                    if (!double.IsNaN(field.Attribute.Minimum)) fieldSchema["minimum"] = field.Attribute.Minimum;
                    if (!double.IsNaN(field.Attribute.Maximum)) fieldSchema["maximum"] = field.Attribute.Maximum;
                    properties[field.Attribute.Id] = fieldSchema;
                    required.Add(field.Attribute.Id);
                }
                schema = new() { ["type"] = "object", ["properties"] = properties,
                    ["required"] = required, ["additionalProperties"] = false };
            }
            if (!root && (!declaredType.IsValueType || Nullable.GetUnderlyingType(declaredType) is not null))
                schema = new() { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
            if (root) schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            return schema;
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

        private static string TypeName(Type type) => type.IsEnum ? "enum" :
            type == typeof(bool) ? "boolean" :
            type == typeof(string) || type == typeof(Uri) ? "string" :
            type.IsPrimitive || type == typeof(decimal) ? "number" :
            type.IsArray || TryCollectionElement(type, out _) ? "array" : "object";

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
