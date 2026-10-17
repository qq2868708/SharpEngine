using System.Text.Json;

namespace SharpEngine.ProjectSelector;

internal sealed record SharpPluginDescriptor(
    string ManifestPath,
    string Root,
    string Id,
    string Version,
    IReadOnlyList<string> Modules,
    string? AssetManifest);

internal static class PluginContracts
{
    internal static SharpPluginDescriptor Read(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("SharpEngine Plugin manifest was not found", fullPath);
        using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
            throw new InvalidDataException("SharpEngine Plugin manifest schemaVersion must be 1");
        var modules = root.TryGetProperty("modules", out var moduleArray)
            ? moduleArray.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray()
            : throw new InvalidDataException("SharpEngine Plugin manifest requires modules");
        if (modules.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Plugin Module paths cannot be empty");
        var pluginRoot = Path.GetDirectoryName(fullPath)!;
        string? assetManifest = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            if (assets.ValueKind != JsonValueKind.Object ||
                !assets.TryGetProperty("manifest", out var manifest) ||
                manifest.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(manifest.GetString()))
                throw new InvalidDataException(
                    "Plugin assets.manifest must be a non-empty relative path");
            assetManifest = manifest.GetString()!;
            if (Path.IsPathRooted(assetManifest) ||
                assetManifest.Replace('\\', '/').Split('/').Any(
                    segment => segment.Length == 0 || segment is "." or ".."))
                throw new InvalidDataException(
                    $"Plugin assets.manifest escapes its plugin root: {assetManifest}");
            if (!assetManifest.Replace('\\', '/').StartsWith("Content/",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Plugin assets.manifest must remain under the plugin Content directory");
        }
        var id = RequiredString(root, "id");
        if (!IsValidId(id))
            throw new InvalidDataException($"Plugin id is invalid: {id}");
        return new SharpPluginDescriptor(fullPath, pluginRoot,
            id, RequiredString(root, "version"), modules,
            assetManifest);
    }

    private static string RequiredString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
            throw new InvalidDataException($"Required Plugin property is missing: {property}");
        return element.GetString()!;
    }

    private static bool IsValidId(string value) =>
        value.Length > 0 && char.IsLetterOrDigit(value[0]) &&
        value.All(character => char.IsLetterOrDigit(character) ||
            character is '.' or '_' or '-');
}
