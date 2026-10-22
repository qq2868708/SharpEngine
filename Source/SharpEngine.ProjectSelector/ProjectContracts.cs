using System.Text.Json;
using System.Text.RegularExpressions;
using SharpEngine.Plugins;

namespace SharpEngine.ProjectSelector;

internal sealed record EngineDescriptor(
    string Root,
    string ManifestPath,
    string Id,
    string Version,
    string SourcePath,
    string BinariesTemplate);

internal sealed record SharpProjectDescriptor(
    string ManifestPath,
    string Root,
    string ProjectId,
    string DisplayName,
    string EngineId,
    string EngineVersion,
    string EngineAssociation,
    string EngineManifest,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> Plugins,
    IReadOnlyList<string> Targets,
    string RenderBackends = "Both");

internal sealed record ResolvedSharpProject(
    SharpProjectDescriptor Project,
    EngineDescriptor Engine);

internal static class ProjectContracts
{
    internal static SharpProjectDescriptor ReadProject(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("SharpEngine project manifest was not found", fullPath);
        using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schema) ||
            schema.GetInt32() != 3)
            throw new InvalidDataException("SharpEngine project manifest schemaVersion must be 3");
        var projectRoot = Path.GetDirectoryName(fullPath)!;
        var engine = root.GetProperty("engine");
        var modules = root.GetProperty("modules").EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty).ToArray();
        if (modules.Length == 0 || modules.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("A SharpEngine project requires at least one Module");
        var sourceRoot = Path.GetFullPath(Path.Combine(projectRoot, "Source"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var module in modules)
        {
            if (Path.IsPathRooted(module))
                throw new InvalidDataException($"Module paths must be project-relative: {module}");
            var modulePath = Path.GetFullPath(Path.Combine(projectRoot, module));
            if (!modulePath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Module path must remain under Source: {module}");
            if (!File.Exists(modulePath))
                throw new FileNotFoundException("Project Module was not found", modulePath);
        }
        var plugins = OptionalArray(root, "plugins");
        _ = PluginConfiguration.ReadArray(root, "disabledPlugins");
        foreach (var plugin in plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin) ||
                (!plugin.StartsWith("project://", StringComparison.OrdinalIgnoreCase) &&
                 !plugin.StartsWith("engine://", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    $"Plugin references must use project:// or engine:// URIs: {plugin}");
            ValidatePluginUri(plugin);
        }
        var association = RequiredString(engine, "association");
        if (association.Length > 128 ||
            (!Guid.TryParse(association, out _) &&
             !Regex.IsMatch(association, "^[A-Za-z0-9][A-Za-z0-9._-]*$",
                 RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Engine association is invalid");
        return new SharpProjectDescriptor(
            fullPath,
            projectRoot,
            RequiredString(root, "projectId"),
            OptionalString(root, "displayName") ?? RequiredString(root, "projectId"),
            RequiredString(engine, "id"),
            RequiredString(engine, "version"),
            association,
            OptionalString(engine, "manifest") ?? "engine.json",
            modules,
            plugins,
            ProjectTargets.Read(root),
            RenderBuildOptions.Read(root));
    }

    internal static EngineDescriptor ReadEngine(string root, string manifestRelative = "engine.json")
    {
        var fullRoot = Path.GetFullPath(root);
        var manifestPath = ResolveUnderRoot(fullRoot, manifestRelative,
            "Engine manifest");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Engine manifest was not found", manifestPath);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = document.RootElement;
        if (manifest.ValueKind != JsonValueKind.Object ||
            !manifest.TryGetProperty("schemaVersion", out var schema) ||
            schema.GetInt32() != 2)
            throw new InvalidDataException("Engine manifest schemaVersion must be 2");
        var paths = manifest.GetProperty("paths");
        return new EngineDescriptor(
            fullRoot,
            manifestPath,
            RequiredString(manifest, "engineId"),
            RequiredString(manifest, "version"),
            RequiredString(paths, "source"),
            RequiredString(paths, "binaries"));
    }

    internal static void ValidateBinding(SharpProjectDescriptor project,
        EngineDescriptor engine)
    {
        if (!string.Equals(project.EngineId, engine.Id, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Engine id mismatch: project requires '{project.EngineId}', resolved '{engine.Id}'");
        if (!string.Equals(project.EngineVersion, engine.Version, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Engine version mismatch: project requires '{project.EngineVersion}', resolved '{engine.Version}'");
        var requestedManifest = Path.GetFullPath(Path.Combine(engine.Root,
            project.EngineManifest));
        if (!string.Equals(requestedManifest, engine.ManifestPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Project requested unexpected Engine manifest '{project.EngineManifest}'");
        var Selection = PluginConfiguration.Read(engine.ManifestPath, project.ManifestPath);
        foreach (var plugin in Selection.Effective)
        {
            var manifestPath = ResolvePluginManifest(project.Root, engine.Root, plugin);
            var descriptor = PluginContracts.Read(manifestPath);
            foreach (var module in descriptor.Modules)
            {
                var modulePath = ResolvePluginModule(descriptor, module);
                if (!File.Exists(modulePath))
                    throw new FileNotFoundException("Plugin Module was not found", modulePath);
            }
            if (descriptor.AssetManifest is not null)
            {
                var assetManifest = ResolvePluginAssetManifest(descriptor,
                    descriptor.AssetManifest);
                if (!File.Exists(assetManifest))
                    throw new FileNotFoundException(
                        "Plugin asset manifest was not found", assetManifest);
            }
        }
    }

    internal static string ResolveBinaries(EngineDescriptor engine, string platform,
        string configuration, string target)
    {
        var relative = engine.BinariesTemplate
            .Replace("{platform}", platform, StringComparison.Ordinal)
            .Replace("{configuration}", configuration, StringComparison.Ordinal)
            .Replace("{target}", target, StringComparison.Ordinal);
        return ResolveUnderRoot(engine.Root, relative, "Engine binaries");
    }

    private static string ResolveUnderRoot(string root, string relative, string label)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('{') || relative.Contains('}'))
            throw new InvalidDataException($"{label} path is invalid: {relative}");
        var rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(rootPrefix, relative));
        if (!result.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} path escapes its root: {relative}");
        return result;
    }

    private static string RequiredString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
            throw new InvalidDataException($"Required string property is missing: {property}");
        return element.GetString()!;
    }

    private static string? OptionalString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string[] OptionalArray(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element)) return [];
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Project property '{property}' must be an array");
        return element.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
    }

    internal static string ResolvePluginManifest(string projectRoot, string engineRoot, string uri)
    {
        var (root, relative) = SplitPluginUri(projectRoot, engineRoot, uri);
        var pluginsRoot = Path.GetFullPath(Path.Combine(root, "Plugins"));
        var fullPath = ResolveUnderRoot(pluginsRoot, relative, "Plugin manifest");
        if (!fullPath.EndsWith(".sharpplugin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Plugin manifest must use .sharpplugin: {uri}");
        return fullPath;
    }

    internal static string ResolvePluginModule(SharpPluginDescriptor plugin, string relative)
    {
        if (Path.IsPathRooted(relative) || string.IsNullOrWhiteSpace(relative))
            throw new InvalidDataException($"Plugin Module paths must be relative: {relative}");
        var root = Path.GetFullPath(plugin.Root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Plugin Module path escapes its plugin root: {relative}");
        return result;
    }

    internal static string ResolvePluginAssetManifest(SharpPluginDescriptor plugin,
        string relative) => ResolvePluginModule(plugin, relative);

    private static (string Root, string Relative) SplitPluginUri(string projectRoot,
        string engineRoot, string uri)
    {
        var separator = uri.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0) throw new InvalidDataException($"Invalid Plugin URI: {uri}");
        var scheme = uri[..separator];
        var relative = uri[(separator + 3)..].Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative) ||
            !relative.StartsWith("Plugins" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Plugin URI must remain under Plugins: {uri}");
        return scheme.Equals("project", StringComparison.OrdinalIgnoreCase)
            ? (projectRoot, relative[("Plugins" + Path.DirectorySeparatorChar).Length..])
            : scheme.Equals("engine", StringComparison.OrdinalIgnoreCase)
                ? (engineRoot, relative[("Plugins" + Path.DirectorySeparatorChar).Length..])
                : throw new InvalidDataException($"Unknown Plugin URI scheme: {scheme}");
    }

    private static void ValidatePluginUri(string uri)
    {
        _ = SplitPluginUri(".", ".", uri);
    }
}
