using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpEngine.Editor;

internal sealed record McpConfiguration
{
    internal const int DefaultPort = 17700;

    [JsonRequired]
    public int SchemaVersion
    {
        get;
        init;
    } = 1;

    [JsonRequired]
    public int Port
    {
        get;
        init;
    } = DefaultPort;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static McpConfiguration Parse(string Text)
    {
        var Configuration = JsonSerializer.Deserialize<McpConfiguration>(Text, JsonOptions) ??
            throw new InvalidDataException("MCP configuration must be a JSON object");
        if (Configuration.SchemaVersion != 1)
        {
            throw new InvalidDataException("MCP configuration schemaVersion must be 1");
        }
        ValidatePort(Configuration.Port);
        return Configuration;
    }

    internal static void ValidatePort(int Port)
    {
        if (Port < 1 || Port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "MCP port must be between 1 and 65535");
        }
    }

    internal string ToJson() { return JsonSerializer.Serialize(this, JsonOptions) + "\n"; }

    internal void Save(string ConfigurationPath)
    {
        ValidatePort(Port);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
        var TemporaryPath = ConfigurationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(TemporaryPath, ToJson(), new UTF8Encoding(false));
            File.Move(TemporaryPath, ConfigurationPath, true);
        }
        finally
        {
            if (File.Exists(TemporaryPath))
            {
                File.Delete(TemporaryPath);
            }
        }
    }
}
