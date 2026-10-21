using System.Text.Json.Nodes;
using SharpEngine.Interop;

internal static class McpBootstrapValidation
{
    internal static void Run(Action<bool, string> Check)
    {
        string[] Roles = ["game", "project-browser"];
        foreach (var Role in Roles)
        {
            var Input = Console.In;
            var Output = Console.Out;
            using var Captured = new StringWriter();
            try
            {
                Console.SetIn(new StringReader("""
                    {"jsonrpc":"2.0","id":1,"method":"initialize"}
                    {"jsonrpc":"2.0","id":2,"method":"tools/list"}
                    {"jsonrpc":"2.0","id":3,"method":"resources/list"}
                    """));
                Console.SetOut(Captured);
                Check(UiMcpSessionHost.RunBridge(Role) == 0, "MCP bootstrap must end cleanly at EOF");
            }
            finally
            {
                Console.SetIn(Input);
                Console.SetOut(Output);
            }
            var Responses = Captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(Line => JsonNode.Parse(Line)!).ToArray();
            Check(Responses[0]["result"]!["serverInfo"]!["name"]!.GetValue<string>() == "sharpengine-" + Role,
                "Shared UI transport must preserve the role's MCP identity");
            var Tools = Responses[1]["result"]!["tools"]!.AsArray();
            Check(Tools.Count == (Role == "game" ? 2 : 3), "Bootstrap must preserve Game discovery and add explicit browser launch");
            Check(Responses[2]["result"]!["resources"]!.AsArray().Count == 0,
                "No implicit target may expose UI resources before explicit session selection");
        }
    }
}
