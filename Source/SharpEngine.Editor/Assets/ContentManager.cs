using System.Text.Json;

namespace SharpEngine.Assets;

/// <summary>Which content store a browser should display.</summary>
public enum ContentSourceFilter : byte
{
    All = 0,
    Engine = 1,
    Project = 2,
}

/// <summary>How entries are arranged inside one content browser instance.</summary>
public enum ContentViewMode : byte
{
    Tile = 0,
    List = 1,
}

/// <summary>A file or directory shown by the content browser.</summary>
public sealed record ContentBrowserEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    AssetSource Source,
    string Type,
    string? AssetId,
    bool Exists,
    bool ReadOnly);

/// <summary>
/// Unreal-style Content folder browser. The registry supplies metadata for
/// known assets, while the filesystem keeps the browser useful for files that
/// have not been added to a manifest yet.
/// </summary>
public sealed class ContentManager
{
    private readonly AssetRegistry _registry;
    private string _search = string.Empty;
    private ContentSourceFilter _source;
    private string? _type;
    private string? _selectedId;
    private string? _selectedPath;
    private AssetSource? _selectedSource;
    private string _currentFolder = string.Empty;
    private ContentViewMode _viewMode;

    public ContentManager(AssetRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _source = registry.ProjectRoot is null
            ? ContentSourceFilter.Engine
            : ContentSourceFilter.Project;
    }

    public AssetRegistry Registry => _registry;
    public string SearchText => _search;
    public ContentSourceFilter SourceFilter => _source;
    public string? TypeFilter => _type;
    public string? SelectedId => _selectedId;
    public string? SelectedPath => _selectedPath;
    public AssetSource? SelectedSource => _selectedSource;
    public ContentViewMode ViewMode => _viewMode;
    /// <summary>Path relative to the active source's Content folder.</summary>
    public string CurrentFolder => _currentFolder;
    public string CurrentFolderDisplay => _currentFolder.Length == 0
        ? "Content"
        : _currentFolder.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase)
            ? _currentFolder
            : $"Content/{_currentFolder}";
    public string ContentRoot => GetContentRoot(_source == ContentSourceFilter.Engine
        ? AssetSource.Engine
        : AssetSource.Project);
    public AssetDescriptor? SelectedAsset =>
        _selectedId is null ? null : _registry.Find(_selectedId);

    public IReadOnlyList<string> Types => _registry.Assets
        .Select(asset => asset.Type)
        .Where(type => !string.IsNullOrWhiteSpace(type))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>Registered assets visible in the current Content folder.</summary>
    public IReadOnlyList<AssetDescriptor> VisibleAssets => Sources()
        .SelectMany(source => Enumerate(source)
            .Where(entry => !entry.IsDirectory && entry.AssetId is not null)
            .Select(entry => _registry.Find(entry.AssetId!)!))
        .ToArray();

    /// <summary>Entries visible in the current Content folder, including folders.</summary>
    public IReadOnlyList<ContentBrowserEntry> VisibleEntries => Sources()
        .SelectMany(Enumerate)
        .OrderByDescending(entry => entry.IsDirectory)
        .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void SetSearch(string? value) => _search = value?.Trim() ?? string.Empty;

    public void SetSource(ContentSourceFilter source)
    {
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));
        _source = source;
        _currentFolder = string.Empty;
        _selectedId = null;
        _selectedPath = null;
        _selectedSource = null;
        if (_source == ContentSourceFilter.Project && _registry.ProjectRoot is null)
            _source = ContentSourceFilter.Engine;
    }

    public void SetType(string? type)
    {
        _type = string.IsNullOrWhiteSpace(type) ||
            string.Equals(type, "All", StringComparison.OrdinalIgnoreCase)
            ? null
            : Types.FirstOrDefault(candidate =>
                string.Equals(candidate, type.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public void SetViewMode(ContentViewMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        _viewMode = mode;
    }

    public bool OpenFolder(string? relativePath)
    {
        var normalized = NormalizeFolder(relativePath);
        if (!Sources().Any(source => Directory.Exists(ResolveBrowsePath(source, normalized))))
            return false;
        _currentFolder = normalized;
        _selectedPath = null;
        _selectedId = null;
        _selectedSource = null;
        return true;
    }

    public bool GoUp()
    {
        if (_currentFolder.Length == 0) return false;
        var slash = _currentFolder.LastIndexOf('/');
        _currentFolder = slash < 0 ? string.Empty : _currentFolder[..slash];
        _selectedPath = null;
        _selectedId = null;
        _selectedSource = null;
        return true;
    }

    public void ResetFolder() => OpenFolder(string.Empty);

    public bool Select(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _selectedId = null;
            _selectedPath = null;
            _selectedSource = null;
            return true;
        }

        var asset = _registry.Find(id);
        if (asset is null) return false;
        _selectedId = asset.Id;
        _selectedPath = ToContentRelative(asset);
        _selectedSource = asset.Source;
        return true;
    }

    public bool SelectFile(AssetSource source, string relativePath)
    {
        var normalized = NormalizeFolder(relativePath);
        var fullPath = ResolveBrowsePath(source, normalized);
        if (!File.Exists(fullPath)) return false;
        _selectedId = FindDescriptor(source, fullPath)?.Id;
        _selectedPath = normalized;
        _selectedSource = source;
        return true;
    }

    public bool SelectFolder(AssetSource source, string relativePath)
    {
        var normalized = NormalizeFolder(relativePath);
        var fullPath = ResolveBrowsePath(source, normalized);
        if (!Directory.Exists(fullPath)) return false;
        _selectedId = null;
        _selectedPath = normalized;
        _selectedSource = source;
        return true;
    }

    public void ClearSelection()
    {
        _selectedId = null;
        _selectedPath = null;
        _selectedSource = null;
    }

    // Restoring layout is not a navigation command: retain saved references even
    // when files or registered types are temporarily unavailable.
    internal void RestoreBrowserState(string search, ContentSourceFilter source, string? type,
        string folder, string? selectedId, string? selectedPath, AssetSource? selectedSource,
        ContentViewMode viewMode)
    {
        SetSource(source);
        SetViewMode(viewMode);
        _search = search;
        _type = type;
        _currentFolder = NormalizeFolder(folder);
        _selectedId = selectedId;
        _selectedPath = selectedPath is null ? null : NormalizeFolder(selectedPath);
        _selectedSource = selectedSource;
    }

    public string ToJson(JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(new
        {
            search = SearchText,
            source = SourceFilter.ToString(),
            type = TypeFilter,
            contentRoot = ContentRoot,
            currentFolder = CurrentFolder,
            currentFolderExists = Sources().Any(source => Directory.Exists(
                ResolveBrowsePath(source, CurrentFolder))),
            selected = SelectedId,
            selectedPath = SelectedPath,
            selectedSource = SelectedSource?.ToString(),
            viewMode = ViewMode.ToString(),
            count = VisibleEntries.Count,
            types = Types,
            entries = VisibleEntries.Select(entry => new
            {
                name = entry.Name,
                path = entry.RelativePath,
                directory = entry.IsDirectory,
                source = entry.Source.ToString(),
                type = entry.Type,
                assetId = entry.AssetId,
                exists = entry.Exists,
                readOnly = entry.ReadOnly,
            }),
        }, options ?? new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

    private IEnumerable<AssetSource> Sources() => _source switch
    {
        ContentSourceFilter.Engine => new[] { AssetSource.Engine },
        ContentSourceFilter.Project => _registry.ProjectRoot is null
            ? new[] { AssetSource.Engine }
            : new[] { AssetSource.Project },
        _ => _registry.ProjectRoot is null
            ? new[] { AssetSource.Engine }
            : new[] { AssetSource.Project, AssetSource.Engine },
    };

    private IEnumerable<ContentBrowserEntry> Enumerate(AssetSource source)
    {
        if (_currentFolder.Length == 0)
        {
            foreach (var entry in EnumerateDirectory(source,
                         Path.Combine(GetSourceRoot(source) ?? _registry.EngineRoot,
                             "Content")))
                yield return entry;
            if (string.IsNullOrWhiteSpace(_search) &&
                _registry.Plugins.Any(plugin => plugin.Source == source))
                yield return new ContentBrowserEntry("Plugins", "Plugins", true,
                    source, "Folder", null, true, source == AssetSource.Engine);
            yield break;
        }

        if (_currentFolder.Equals("Plugins", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var plugin in _registry.Plugins.Where(plugin => plugin.Source == source)
                         .OrderBy(plugin => plugin.Root, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(plugin.Root);
                if (Matches($"Plugins/{name}", name, null))
                    yield return new ContentBrowserEntry(name, $"Plugins/{name}", true,
                        source, "Folder", null, true, source == AssetSource.Engine);
            }
            yield break;
        }

        if (_currentFolder.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase) &&
            _currentFolder.Split('/').Length == 2)
        {
            var pluginName = _currentFolder.Split('/')[1];
            if (_registry.Plugins.Any(plugin => plugin.Source == source &&
                    string.Equals(Path.GetFileName(plugin.Root), pluginName,
                        StringComparison.OrdinalIgnoreCase)))
                yield return new ContentBrowserEntry("Content",
                    $"Plugins/{pluginName}/Content", true, source, "Folder", null,
                    true, source == AssetSource.Engine);
            yield break;
        }

        var folderPath = ResolveBrowsePath(source, _currentFolder);
        foreach (var entry in EnumerateDirectory(source, folderPath))
            yield return entry;
    }

    private IEnumerable<ContentBrowserEntry> EnumerateDirectory(AssetSource source,
        string folderPath)
    {
        if (!Directory.Exists(folderPath)) yield break;
        IEnumerable<string> directories;
        IEnumerable<string> files;
        try
        {
            var searchRoot = string.IsNullOrWhiteSpace(_search)
                ? folderPath
                : SearchRoot(source);
            var option = string.IsNullOrWhiteSpace(_search)
                ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
            directories = Directory.EnumerateDirectories(searchRoot, "*", option);
            files = Directory.EnumerateFiles(searchRoot, "*", option);
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var directory in directories)
        {
            var relative = ToBrowserRelative(source, directory);
            if (Matches(relative, Path.GetFileName(directory), null))
                yield return new ContentBrowserEntry(Path.GetFileName(directory), relative,
                    true, source, "Folder", null, true, source == AssetSource.Engine);
        }

        foreach (var file in files)
        {
            var relative = ToBrowserRelative(source, file);
            var asset = FindDescriptor(source, file);
            var type = asset?.Type ?? InferType(file);
            if (Matches(relative, Path.GetFileName(file), type) &&
                (_type is null || string.Equals(type, _type, StringComparison.OrdinalIgnoreCase)))
                yield return new ContentBrowserEntry(Path.GetFileName(file), relative, false,
                    source, type, asset?.Id, true, asset?.ReadOnly ?? source == AssetSource.Engine);
        }
    }

    private bool Matches(string path, string name, string? type) =>
        string.IsNullOrWhiteSpace(_search) ||
        path.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
        name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
        (type?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false);

    private string GetContentRoot(AssetSource source)
    {
        var root = GetSourceRoot(source);
        return Path.Combine(root ?? _registry.EngineRoot, "Content");
    }

    private string? GetSourceRoot(AssetSource source) =>
        source == AssetSource.Project ? _registry.ProjectRoot : _registry.EngineRoot;

    private string ResolveBrowsePath(AssetSource source, string folder)
    {
        var root = GetSourceRoot(source) ?? _registry.EngineRoot;
        if (folder.Length == 0)
            return Path.Combine(root, "Content");
        if (!folder.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(GetContentRoot(source),
                folder.Replace('/', Path.DirectorySeparatorChar));

        var parts = folder.Split('/');
        if (parts.Length < 2 || !string.Equals(parts[0], "Plugins",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Content folder must remain under Content: '{folder}'");
        var plugin = _registry.Plugins.FirstOrDefault(value => value.Source == source &&
            string.Equals(Path.GetFileName(value.Root), parts[1],
                StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            return Path.Combine(root, "Plugins", parts[1]);
        if (parts.Length == 2)
            return plugin.Root;
        if (!string.Equals(parts[2], "Content", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Plugin browser paths must remain under Content: '{folder}'");
        var suffix = string.Join('/', parts.Skip(3));
        return Path.Combine(plugin.Root, "Content",
            suffix.Replace('/', Path.DirectorySeparatorChar));
    }

    private string SearchRoot(AssetSource source)
    {
        if (!_currentFolder.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase))
            return GetContentRoot(source);
        var parts = _currentFolder.Split('/');
        if (parts.Length >= 3)
            return ResolveBrowsePath(source,
                $"Plugins/{parts[1]}/Content");
        return ResolveBrowsePath(source, _currentFolder);
    }

    private AssetDescriptor? FindDescriptor(AssetSource source, string path) =>
        _registry.Assets.FirstOrDefault(asset => asset.Source == source &&
            string.Equals(_registry.ResolvePath(asset), path,
                StringComparison.OrdinalIgnoreCase));

    private string ToContentRelative(AssetDescriptor asset) =>
        ToBrowserRelative(asset.Source, _registry.ResolvePath(asset) ?? asset.RelativePath);

    private string ToBrowserRelative(AssetSource source, string path)
    {
        var root = GetSourceRoot(source) ?? _registry.EngineRoot;
        var contentRoot = GetContentRoot(source);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(contentRoot, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || Path.IsPathRooted(relative))
            relative = Path.GetRelativePath(root, fullPath);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string NormalizeFolder(string? path)
    {
        var value = (path ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (value.Length == 0) return string.Empty;
        if (Path.IsPathRooted(value) || value.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Content folder must remain under Content: '{path}'");
        return value;
    }

    private static string InferType(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".mesh.json", StringComparison.OrdinalIgnoreCase)) return "Mesh";
        if (name.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase)) return "Material";
        if (name.EndsWith(".sworld", StringComparison.OrdinalIgnoreCase)) return "World";
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".rml" => "RmlDocument",
            ".rcss" => "StyleSheet",
            ".xml" => "Xml",
            ".json" => "Json",
            ".png" or ".jpg" or ".jpeg" or ".tga" => "Texture",
            ".wav" or ".ogg" or ".mp3" => "Audio",
            _ => "File",
        };
    }
}
