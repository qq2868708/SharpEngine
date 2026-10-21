using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpEngine.Plugins;

/// <summary>Source configuration shared by tooling, code loading and asset mounting.</summary>
public sealed record PluginConfiguration(
    IReadOnlyList<string> EngineDefaults,
    IReadOnlyList<string> ProjectEnabled,
    IReadOnlyList<string> ProjectDisabled,
    IReadOnlyList<string> Effective,
    string EngineDefaultsHash)
{
    public static PluginConfiguration Read(string EngineManifest, string? ProjectManifest)
    {
        using var Engine = JsonDocument.Parse(File.ReadAllText(EngineManifest));
        var Defaults = ReadArray(Engine.RootElement, "plugins");
        foreach (var Uri in Defaults)
        {
            if (!Uri.StartsWith("engine://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SEPLUGIN020: Engine defaults must use engine:// Plugin URIs");
            }
        }
        string[] Enabled = [];
        string[] Disabled = [];
        if (ProjectManifest is not null)
        {
            using var Project = JsonDocument.Parse(File.ReadAllText(ProjectManifest));
            Enabled = ReadArray(Project.RootElement, "plugins");
            Disabled = ReadArray(Project.RootElement, "disabledPlugins");
        }
        var Conflict = Enabled.Intersect(Disabled, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (Conflict is not null)
        {
            throw new InvalidDataException($"SEPLUGIN021: Plugin is both enabled and disabled: {Conflict}");
        }
        var Effective = Defaults.Concat(Enabled).Distinct(StringComparer.OrdinalIgnoreCase)
            .Except(Disabled, StringComparer.OrdinalIgnoreCase)
            .OrderBy(Uri => Uri, StringComparer.Ordinal).ToArray();
        var Fingerprint = JsonSerializer.Serialize(Defaults.OrderBy(Uri => Uri, StringComparer.Ordinal));
        return new PluginConfiguration(Defaults, Enabled, Disabled, Effective,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Fingerprint))));
    }

    public static string[] ReadArray(JsonElement Root, string Property)
    {
        if (Root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("SEPLUGIN022: Plugin configuration root must be an object");
        }
        var Exists = Root.TryGetProperty(Property, out var Values);
        if (!Exists)
        {
            return [];
        }
        if (Values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"SEPLUGIN022: '{Property}' must be an array of Plugin URIs");
        }
        var Result = new List<string>();
        var Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var Value in Values.EnumerateArray())
        {
            if (Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"SEPLUGIN022: '{Property}' entries must be strings");
            }
            var Uri = Value.GetString()!.Replace('\\', '/');
            _ = ResolveManifest(".", ".", Uri);
            if (!Seen.Add(Uri))
            {
                throw new InvalidDataException($"SEPLUGIN023: Duplicate Plugin URI in '{Property}': {Uri}");
            }
            Result.Add(Uri);
        }
        return Result.ToArray();
    }

    public static string ResolveManifest(string EngineRoot, string? ProjectRoot, string Uri)
    {
        var Separator = Uri.IndexOf("://", StringComparison.Ordinal);
        if (Separator <= 0)
        {
            throw new InvalidDataException($"SEPLUGIN024: Invalid Plugin URI: {Uri}");
        }
        var Scheme = Uri[..Separator];
        var Relative = Uri[(Separator + 3)..].Replace('\\', '/');
        var Root = Scheme.Equals("engine", StringComparison.OrdinalIgnoreCase) ? EngineRoot
            : Scheme.Equals("project", StringComparison.OrdinalIgnoreCase) ? ProjectRoot : null;
        if (Root is null || !Relative.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase) ||
            !Relative.EndsWith(".sharpplugin", StringComparison.OrdinalIgnoreCase) ||
            Relative.Split('/').Any(Part => Part.Length == 0 || Part is "." or ".." || Part.Contains(':')))
        {
            throw new InvalidDataException($"SEPLUGIN024: Invalid Plugin URI or unavailable root: {Uri}");
        }
        var Prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Root, "Plugins"))) +
            Path.DirectorySeparatorChar;
        var PathValue = Path.GetFullPath(Path.Combine(Root, Relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathValue.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SEPLUGIN024: Plugin URI escapes Plugins: {Uri}");
        }
        return PathValue;
    }
}
