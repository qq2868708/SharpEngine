using SharpEngine.Editor;

internal static class McpConfigurationValidation
{
    internal static void Run(string DirectoryPath, Action<bool, string> Require)
    {
        var ConfigurationPath = Path.Combine(DirectoryPath, "Mcp", "Mcp.json");
        var Default = new McpConfiguration();
        Default.Save(ConfigurationPath);
        Require(McpConfiguration.Parse(File.ReadAllText(ConfigurationPath)).Port == McpConfiguration.DefaultPort,
            "A fresh MCP configuration must persist and reload the explicit default port");
        var Custom = new McpConfiguration
        {
            Port = 23456,
        };
        Custom.Save(ConfigurationPath);
        var Saved = File.ReadAllText(ConfigurationPath);
        Require(McpConfiguration.Parse(Saved) == Custom, "MCP configuration round-trip must preserve the port and version");
        Custom.Save(ConfigurationPath);
        Require(Saved == File.ReadAllText(ConfigurationPath), "MCP configuration serialization must be deterministic");
        foreach (var Text in new[]
        {
            "null",
            "{}",
            "{\"schemaVersion\":1}",
            "{\"port\":23456}",
            "{\"schemaVersion\":2,\"port\":23456}",
            "{\"schemaVersion\":1,\"port\":0}",
            "{\"schemaVersion\":1,\"port\":65536}",
            "{\"schemaVersion\":1,\"port\":\"23456\"}",
            "{\"schemaVersion\":1,\"port\":23456,\"unknown\":true}",
        })
        {
            var Rejected = false;
            try
            {
                McpConfiguration.Parse(Text);
            }
            catch (Exception Error) when (Error is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
            {
                Rejected = true;
            }
            Require(Rejected, "Invalid MCP configuration must be rejected without defaulting: " + Text);
            Require(File.ReadAllText(ConfigurationPath) == Saved,
                "Rejected MCP configuration must preserve the previous savepoint");
        }
        Require(!Directory.EnumerateFiles(Path.GetDirectoryName(ConfigurationPath)!, "*.tmp").Any(),
            "Atomic MCP saves must not leave temporary files");
    }
}
