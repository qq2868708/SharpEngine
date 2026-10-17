using System.Text.Json;

namespace SharpEngine.ProjectSelector;

internal sealed record SharpPluginDescriptor(
    string ManifestPath,
    string Root,
    string Id,
    string Version,
    IReadOnlyList<string> Modules);

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
        return new SharpPluginDescriptor(fullPath, pluginRoot,
            RequiredString(root, "id"), RequiredString(root, "version"), modules);
    }

    private static string RequiredString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
            throw new InvalidDataException($"Required Plugin property is missing: {property}");
        return element.GetString()!;
    }
}
