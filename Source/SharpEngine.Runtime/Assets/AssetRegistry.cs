using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpEngine.Assets;

/// <summary>Where an asset is owned. Ownership is distinct from editability.</summary>
public enum AssetSource : byte
{
    Engine = 0,
    Project = 1,
}

/// <summary>One declared plugin content owner discovered from a project manifest.</summary>
public sealed record AssetPluginDescriptor(
    string Id,
    string Version,
    AssetSource Source,
    string ManifestPath,
    string Root);

/// <summary>A renderer-neutral asset record exposed to editor panels and the viewport.</summary>
public sealed class AssetDescriptor
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = "Unknown";
    public string Name { get; init; } = string.Empty;
    /// <summary>Path relative to the owning Engine, Project or Plugin Content directory.</summary>
    public string RelativePath { get; init; } = string.Empty;
    public AssetSource Source { get; init; }
    public bool ReadOnly { get; init; }
    /// <summary>Plugin id when the asset is declared by a plugin manifest.</summary>
    public string? OwnerId { get; init; }
    public bool Exists { get; internal set; }
    public string? Importer { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Absolute file path when this descriptor points at a file.</summary>
    public string? ResolvePath(string ContentRoot)
    {
        if (string.IsNullOrWhiteSpace(RelativePath))
        {
            return null;
        }

        try
        {
            if (Path.IsPathRooted(RelativePath) || RelativePath.StartsWith('/') ||
                RelativePath.StartsWith('\\') || RelativePath.Any(character =>
                    char.IsControl(character) || "<>:\"|?*".Contains(character)))
            {
                throw new InvalidDataException(
                    $"Asset path must be a relative file path: '{RelativePath}'");
            }

            var Segments = RelativePath.Replace('\\', '/').Split('/');
            if (Segments.Any(Segment => Segment.Length == 0 || Segment is "." or ".."))
            {
                throw new InvalidDataException("Asset path must be canonical and remain under Content");
            }
            var fullRoot = Path.GetFullPath(ContentRoot);
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot,
                RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(fullRoot, fullPath);
            if (relative == "." || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                throw new InvalidDataException(
                    $"Asset path escapes Content or names a directory: '{RelativePath}'");
            }
            if (RelativePath.EndsWith('/') || RelativePath.EndsWith('\\') ||
                Directory.Exists(fullPath))
                throw new InvalidDataException(
                    $"Asset path names a directory: '{RelativePath}'");

            return fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Invalid asset path: '{RelativePath}'",
                exception);
        }
    }

    public override string ToString() =>
        $"{Id} ({Type}, {Source}, {RelativePath})";
}

/// <summary>Diagnostics emitted while loading one or more asset manifests.</summary>
public sealed record AssetRegistryDiagnostic(
    string Message,
    string? ManifestPath = null,
    bool IsError = false);

/// <summary>
/// A small manifest-backed registry. The engine owns the public manifest and a
/// project contributes a second manifest. IDs are namespaced as engine:// and
/// project:// so the two stores can safely be displayed together. Individual
/// asset types or records declare editability; source ownership alone is not a
/// file-system permission boundary.
/// </summary>
public sealed class AssetRegistry
{
    public const int ManifestSchemaVersion = 2;

    private readonly List<AssetDescriptor> _assets = new();
    private readonly Dictionary<string, AssetDescriptor> _byId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AssetRegistryDiagnostic> _diagnostics = new();
    private readonly List<AssetPluginDescriptor> _plugins = new();
    private readonly HashSet<string> _pluginIds =
        new(StringComparer.OrdinalIgnoreCase);

    private AssetRegistry(string engineRoot, string? projectRoot)
    {
        EngineRoot = Path.GetFullPath(engineRoot);
        ProjectRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? null
            : Path.GetFullPath(projectRoot);
    }

    public string EngineRoot { get; }
    public string? ProjectRoot { get; }
    public string? DefaultWorldId { get; private set; }
    public IReadOnlyList<AssetDescriptor> Assets => _assets;
    public IReadOnlyList<AssetPluginDescriptor> Plugins => _plugins;
    public IReadOnlyList<AssetRegistryDiagnostic> Diagnostics => _diagnostics;

    public IEnumerable<AssetDescriptor> EngineAssets =>
        _assets.Where(asset => asset.Source == AssetSource.Engine);

    public IEnumerable<AssetDescriptor> ProjectAssets =>
        _assets.Where(asset => asset.Source == AssetSource.Project);

    public IEnumerable<AssetDescriptor> PluginAssets =>
        _assets.Where(asset => asset.OwnerId is not null);

    public AssetDescriptor? Find(string? id) =>
        id is not null && _byId.TryGetValue(id, out var asset) ? asset : null;

    public string? ResolvePath(AssetDescriptor asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var Root = asset.Source == AssetSource.Engine ? EngineRoot : ProjectRoot;
        if (asset.OwnerId is not null)
        {
            Root = Plugins.FirstOrDefault(Plugin => Plugin.Source == asset.Source &&
                string.Equals(Plugin.Id, asset.OwnerId, StringComparison.OrdinalIgnoreCase))?.Root;
        }
        return Root is null ? null : asset.ResolvePath(Path.Combine(Root, "Content"));
    }

    public IEnumerable<AssetDescriptor> Search(string? query,
        AssetSource? source = null, string? type = null)
    {
        var normalized = query?.Trim();
        return _assets.Where(asset =>
            (!source.HasValue || asset.Source == source.Value) &&
            (string.IsNullOrWhiteSpace(type) ||
                string.Equals(asset.Type, type, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(normalized) ||
                asset.Id.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                asset.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                asset.RelativePath.Contains(normalized, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Stable AI-facing snapshot of both public and project asset stores.</summary>
    public string ToJson(JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = ManifestSchemaVersion,
            assets = Assets.Select(asset => new
            {
                id = asset.Id,
                type = RedactPaths(asset.Type),
                name = RedactPaths(asset.Name),
                source = asset.Source.ToString(),
                readOnly = asset.ReadOnly,
                plugin = asset.OwnerId,
                exists = asset.Exists,
                path = asset.RelativePath,
                importer = asset.Importer is null ? null : RedactPaths(asset.Importer),
                tags = asset.Tags.Select(RedactPaths),
                metadata = asset.Metadata.ToDictionary(Entry => Entry.Key, Entry => RedactPaths(Entry.Value)),
            }),
            plugins = Plugins.Select(plugin => new
            {
                id = plugin.Id,
                version = plugin.Version,
                source = plugin.Source.ToString(),
                manifest = LogicalPath(plugin.ManifestPath),
            }),
            diagnostics = Diagnostics.Select(Diagnostic => new
            {
                message = RedactPaths(Diagnostic.Message),
                manifestPath = Diagnostic.ManifestPath is null ? null : LogicalPath(Diagnostic.ManifestPath),
                isError = Diagnostic.IsError,
            }),
        }, options ?? new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

    private string LogicalPath(string FilePath)
    {
        try
        {
            if (ProjectRoot is not null)
            {
                var Relative = Path.GetRelativePath(ProjectRoot, FilePath).Replace('\\', '/');
                if (Relative != ".." && !Relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(Relative))
                {
                    return "project://" + Relative;
                }
            }
            var EngineRelative = Path.GetRelativePath(EngineRoot, FilePath).Replace('\\', '/');
            return EngineRelative != ".." && !EngineRelative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(EngineRelative)
                ? "engine://" + EngineRelative : "[external path]";
        }
        catch (Exception Error) when (Error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "[external path]";
        }
    }

    private string RedactPaths(string Message)
    {
        return System.Text.RegularExpressions.Regex.Replace(Message,
            "(?<![a-zA-Z0-9:/])(?:[a-zA-Z]:[\\\\/]|[\\\\/]{2}|/)[^'\"\r\n<>]*",
            Match => LogicalPath(Match.Value));
    }

    /// <summary>
    /// Load the public engine manifest and the optional project manifest. Missing
    /// project manifests are valid for the editor's empty-project state.
    /// </summary>
    public static AssetRegistry Load(string engineRoot, string? projectRoot = null,
        string? projectManifestPath = null)
    {
        var registry = new AssetRegistry(engineRoot, projectRoot);
        var engineManifest = Path.Combine(registry.EngineRoot, "engine.json");

        registry.LoadOwnerManifest(engineManifest, AssetSource.Engine,
            registry.EngineRoot);

        string? SelectedManifest = null;
        if (!string.IsNullOrWhiteSpace(registry.ProjectRoot))
        {
            var projectManifest = string.IsNullOrWhiteSpace(projectManifestPath)
                ? Directory.GetFiles(registry.ProjectRoot!, "*.sharpproject",
                    SearchOption.TopDirectoryOnly).SingleOrDefault()
                : Path.GetFullPath(projectManifestPath);

            if (projectManifest is not null && File.Exists(projectManifest))
            {
                SelectedManifest = projectManifest;
                registry.LoadOwnerManifest(projectManifest,
                    AssetSource.Project, registry.ProjectRoot!);
            }
        }

        registry.LoadConfiguredPlugins(engineManifest, SelectedManifest);
        registry.LoadDefaultWorld(engineManifest);
        return registry;
    }

    /// <summary>Load resolved engine and optional root project manifest paths.</summary>
    public static AssetRegistry LoadFromManifests(string engineManifestPath,
        string? projectManifestPath = null)
    {
        var engineRoot = GetManifestRoot(engineManifestPath);
        var projectRoot = projectManifestPath is null
            ? null
            : GetManifestRoot(projectManifestPath);
        var registry = new AssetRegistry(engineRoot, projectRoot);
        registry.LoadOwnerManifest(engineManifestPath, AssetSource.Engine, engineRoot);
        if (projectManifestPath is not null)
        {
            registry.LoadOwnerManifest(projectManifestPath,
                AssetSource.Project, projectRoot!);
        }

        registry.LoadConfiguredPlugins(engineManifestPath, projectManifestPath);
        registry.LoadDefaultWorld(engineManifestPath);
        return registry;
    }

    private void LoadConfiguredPlugins(string EngineManifest, string? ProjectManifest)
    {
        if (!File.Exists(EngineManifest))
        {
            return;
        }
        try
        {
            var Selection = SharpEngine.Plugins.PluginConfiguration.Read(EngineManifest, ProjectManifest);
            using var Plugins = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                plugins = Selection.Effective,
            }));
            LoadPlugins(Plugins.RootElement, ProjectManifest ?? EngineManifest);
        }
        catch (Exception Error) when (Error is JsonException or InvalidDataException or IOException)
        {
            _diagnostics.Add(new AssetRegistryDiagnostic(Error.Message, ProjectManifest ?? EngineManifest, true));
        }
    }

    private void LoadOwnerManifest(string manifestPath, AssetSource source,
        string sourceRoot)
    {
        if (!File.Exists(manifestPath))
        {
            _diagnostics.Add(new AssetRegistryDiagnostic(
                $"Source manifest not found: {manifestPath}", manifestPath, true));
            return;
        }

        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Source manifest root must be an object");
            var relativePath = "Content/Assets/assets.json";
            if (document.RootElement.TryGetProperty("assets", out var assets))
            {
                if (assets.ValueKind != JsonValueKind.Object ||
                    !assets.TryGetProperty("manifest", out var manifest) ||
                    manifest.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(manifest.GetString()))
                {
                    throw new InvalidDataException(
                        "Source assets.manifest must be a non-empty relative path");
                }

                relativePath = manifest.GetString()!;
            }

            LoadManifest(ResolveUnder(sourceRoot, relativePath, "Asset manifest"), source, sourceRoot);
        }
        catch (Exception exception) when (exception is JsonException or
            InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _diagnostics.Add(new AssetRegistryDiagnostic(
                $"Invalid source manifest: {exception.Message}", manifestPath, true));
        }
    }

    private void LoadDefaultWorld(string ManifestPath)
    {
        if (!File.Exists(ManifestPath))
        {
            return;
        }
        try
        {
            using var Document = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            if (Document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            var HasPresets = Document.RootElement.TryGetProperty("worldPresets", out var Presets);
            if (!HasPresets || Presets.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            var HasDefault = Presets.TryGetProperty("default", out var Default);
            if (!HasDefault || Default.ValueKind != JsonValueKind.String)
            {
                return;
            }
            var RawId = Default.GetString();
            var Valid = TryNormalizeId(RawId!, AssetSource.Engine, "", out var Id);
            if (RawId is null || !Valid)
            {
                throw new InvalidDataException("worldPresets.default must be a valid engine asset id");
            }
            var Asset = Find(Id);
            if (Asset is null || !string.Equals(Asset.Type, "World", StringComparison.OrdinalIgnoreCase) || !Asset.Exists)
            {
                throw new InvalidDataException($"worldPresets.default must reference an existing World asset: '{Id}'");
            }
            DefaultWorldId = Id;
        }
        catch (Exception Error) when (Error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _diagnostics.Add(new AssetRegistryDiagnostic($"Invalid source manifest: {Error.Message}", ManifestPath, true));
        }
    }

    private static string GetManifestRoot(string manifestPath)
    {
        return Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
    }

    private void LoadManifest(string manifestPath, AssetSource source,
        string sourceRoot, string idPrefix = "",
        string? ownerId = null)
    {
        if (!File.Exists(manifestPath))
        {
            _diagnostics.Add(new AssetRegistryDiagnostic(
                $"Asset manifest not found: {manifestPath}", manifestPath));
            return;
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            };
            var manifest = JsonSerializer.Deserialize<AssetManifestFile>(
                File.ReadAllText(manifestPath), options);
            if (manifest is null || manifest.SchemaVersion != ManifestSchemaVersion || manifest.Assets is null)
            {
                _diagnostics.Add(new AssetRegistryDiagnostic(
                    $"Asset manifest requires schemaVersion=2 and Content-relative paths; migrate older manifests explicitly: {manifestPath}",
                    manifestPath, true));
                return;
            }

            foreach (var item in manifest.Assets ?? new List<AssetManifestItem?>())
            {
                AddAsset(item, source, sourceRoot, manifestPath,
                    idPrefix, ownerId);
            }
        }
        catch (Exception exception) when (exception is JsonException or
            IOException or UnauthorizedAccessException)
        {
            _diagnostics.Add(new AssetRegistryDiagnostic(
                $"Invalid asset manifest: {exception.Message}", manifestPath, true));
        }
    }

    private void AddAsset(AssetManifestItem? item, AssetSource source,
        string sourceRoot, string manifestPath,
        string idPrefix, string? ownerId)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Id) ||
            string.IsNullOrWhiteSpace(item.Path))
        {
            _diagnostics.Add(new AssetRegistryDiagnostic(
                "Asset entries require both id and path", manifestPath, true));
            return;
        }

        if (!TryNormalizeId(item.Id, source, idPrefix, out var id))
        {
            _diagnostics.Add(new AssetRegistryDiagnostic(
                $"Invalid asset id or source namespace: '{item.Id}' (source={source})",
                manifestPath, true));
            return;
        }
        if (_byId.ContainsKey(id))
        {
            _diagnostics.Add(new AssetRegistryDiagnostic(
                $"Duplicate asset id ignored: {id}", manifestPath, true));
            return;
        }

        var relativePath = item.Path.Replace('\\', '/');
        var descriptor = new AssetDescriptor
        {
            Id = id,
            Type = string.IsNullOrWhiteSpace(item.Type) ? "Unknown" : item.Type,
            Name = string.IsNullOrWhiteSpace(item.Name)
                ? Path.GetFileNameWithoutExtension(relativePath)
                : item.Name,
            RelativePath = relativePath,
            Source = source,
            OwnerId = ownerId,
            ReadOnly = item.ReadOnly || (source == AssetSource.Engine &&
                !string.Equals(item.Type, "World", StringComparison.OrdinalIgnoreCase)),
            Importer = item.Importer,
            Tags = (item.Tags ?? new List<string>()).Where(
                tag => !string.IsNullOrWhiteSpace(tag)).ToArray(),
            Metadata = new ReadOnlyDictionary<string, string>(
                item.Metadata ?? new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)),
        };

        try
        {
            var PathValue = descriptor.ResolvePath(Path.Combine(sourceRoot, "Content"));
            descriptor.Exists = PathValue is not null && File.Exists(PathValue);
            if (!descriptor.Exists)
            {
                _diagnostics.Add(new AssetRegistryDiagnostic(
                    $"Asset file not found: {id} ({relativePath})", manifestPath));
            }
        }
        catch (InvalidDataException exception)
        {
            _diagnostics.Add(new AssetRegistryDiagnostic(
                exception.Message, manifestPath, true));
            return;
        }

        _assets.Add(descriptor);
        _byId.Add(descriptor.Id, descriptor);
    }

    private void LoadPlugins(JsonElement owner, string ownerManifestPath)
    {
        if (!owner.TryGetProperty("plugins", out var plugins)) return;
        if (plugins.ValueKind != JsonValueKind.Array)
        {
            _diagnostics.Add(new AssetRegistryDiagnostic(
                "Source manifest plugins must be an array", ownerManifestPath, true));
            return;
        }

        foreach (var pluginValue in plugins.EnumerateArray())
        {
            if (pluginValue.ValueKind != JsonValueKind.String)
            {
                _diagnostics.Add(new AssetRegistryDiagnostic(
                    "Plugin references must be strings", ownerManifestPath, true));
                continue;
            }

            var uri = pluginValue.GetString() ?? string.Empty;
            try
            {
                var (source, root, manifestPath) = ResolvePluginManifest(uri);
                using var pluginDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var plugin = pluginDocument.RootElement;
                if (plugin.ValueKind != JsonValueKind.Object ||
                    !plugin.TryGetProperty("schemaVersion", out var schema) ||
                    schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1)
                    throw new InvalidDataException(
                        $"Plugin manifest schemaVersion must be 1: {manifestPath}");
                var id = RequiredString(plugin, "id");
                if (!IsValidPluginId(id))
                    throw new InvalidDataException($"Plugin id is invalid: {id}");
                var version = RequiredString(plugin, "version");
                var pluginRoot = Path.GetDirectoryName(manifestPath)!;
                if (!_pluginIds.Add(id))
                    throw new InvalidDataException($"Duplicate Plugin id '{id}'");

                _plugins.Add(new AssetPluginDescriptor(id, version, source,
                    manifestPath, pluginRoot));

                if (!plugin.TryGetProperty("assets", out var assets)) continue;
                if (assets.ValueKind != JsonValueKind.Object ||
                    !assets.TryGetProperty("manifest", out var assetManifest) ||
                    assetManifest.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(assetManifest.GetString()))
                    throw new InvalidDataException(
                        $"Plugin assets.manifest must be a non-empty relative path: {manifestPath}");

                var assetManifestRelative = assetManifest.GetString()!;
                if (!assetManifestRelative.Replace('\\', '/').StartsWith("Content/",
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Plugin assets.manifest must remain under Content: {manifestPath}");

                var assetManifestPath = ResolveUnder(pluginRoot,
                    assetManifestRelative, "Plugin asset manifest");
                var idPrefix = $"plugins/{id}/";
                LoadManifest(assetManifestPath, source, pluginRoot, idPrefix, id);
            }
            catch (Exception exception) when (exception is JsonException or
                InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _diagnostics.Add(new AssetRegistryDiagnostic(
                    $"Invalid Plugin manifest '{uri}': {exception.Message}",
                    ownerManifestPath, true));
            }
        }
    }

    private (AssetSource Source, string Root, string ManifestPath) ResolvePluginManifest(
        string uri)
    {
        var separator = uri.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0) throw new InvalidDataException($"Invalid Plugin URI: {uri}");
        var scheme = uri[..separator];
        var relative = uri[(separator + 3)..].Replace('\\', '/');
        if (!relative.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Plugin URI must remain under Plugins: {uri}");

        var source = scheme.Equals("engine", StringComparison.OrdinalIgnoreCase)
            ? AssetSource.Engine
            : scheme.Equals("project", StringComparison.OrdinalIgnoreCase)
                ? AssetSource.Project
                : throw new InvalidDataException($"Unknown Plugin URI scheme: {scheme}");
        var root = source == AssetSource.Engine ? EngineRoot : ProjectRoot;
        if (root is null) throw new InvalidDataException($"Plugin root is unavailable: {uri}");
        var pluginRelative = relative["Plugins/".Length..];
        if (pluginRelative.Length == 0 || pluginRelative.Split('/').Any(
                segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException($"Plugin URI contains an invalid path: {uri}");
        var pluginsRoot = Path.Combine(root, "Plugins");
        var manifestPath = ResolveUnder(pluginsRoot, pluginRelative, "Plugin manifest");
        if (!manifestPath.EndsWith(".sharpplugin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Plugin manifest must use .sharpplugin: {uri}");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Plugin manifest was not found", manifestPath);
        return (source, root, manifestPath);
    }

    private static string ResolveUnder(string root, string relative, string label)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Replace('\\', '/').Split('/').Any(
                segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException($"{label} path is invalid: {relative}");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(fullRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} path escapes its root: {relative}");
        return result;
    }

    private static string RequiredString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
            throw new InvalidDataException($"Plugin property '{property}' is required");
        return element.GetString()!;
    }

    private static bool IsValidPluginId(string value) =>
        value.Length > 0 && char.IsLetterOrDigit(value[0]) &&
        value.All(character => char.IsLetterOrDigit(character) ||
            character is '.' or '_' or '-');

    private static bool TryNormalizeId(string id, AssetSource source,
        string idPrefix, out string normalized)
    {
        var value = id.Trim().Replace('\\', '/');
        var prefix = source == AssetSource.Engine ? "engine://" : "project://";
        normalized = string.Empty;
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            value = value[prefix.Length..];
        }

        if (value.Length == 0 || value.Split('/').Any(segment =>
            segment.Length == 0 || segment is "." or "..") ||
            value.Any(character => !char.IsLetterOrDigit(character) &&
                character is not '/' and not '_' and not '-' and not '.'))
        {
            return false;
        }

        if (idPrefix.Length > 0)
            value = idPrefix + value;
        normalized = prefix + value;
        return true;
    }

    private sealed class AssetManifestFile
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }
        [JsonPropertyName("assets")]
        public List<AssetManifestItem?>? Assets { get; set; }
    }

    private sealed class AssetManifestItem
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = "Unknown";
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool ReadOnly { get; set; }
        public string? Importer { get; set; }
        public List<string>? Tags { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }
}
