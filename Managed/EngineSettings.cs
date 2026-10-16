using System.Text.Json;

namespace SharpEngine.Editor;

/// <summary>Editor preferences stored outside the read-only public engine.</summary>
public sealed class EngineSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool InvertYAxis { get; set; }

    public static EngineSettings Load(string engineRoot, string? projectRoot)
    {
        var settings = new EngineSettings();
        var path = GetPath(engineRoot, projectRoot);
        if (!File.Exists(path)) return settings;
        try
        {
            var loaded = JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (loaded is not null) settings = loaded;
        }
        catch (JsonException)
        {
            // A malformed preference must not prevent opening a project.
        }
        settings.SchemaVersion = 1;
        return settings;
    }

    public void Save(string engineRoot, string? projectRoot)
    {
        var path = GetPath(engineRoot, projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
    }

    public static string GetPath(string engineRoot, string? projectRoot) =>
        Path.Combine(projectRoot ?? engineRoot, "Saved", "Config", "EngineSettings.json");
}
