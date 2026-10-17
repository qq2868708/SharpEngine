using System.Text.Json;
using System.Text.RegularExpressions;

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
    IReadOnlyList<string> Modules);

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
            modules);
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
}
