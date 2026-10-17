using System.Diagnostics;
using System.Text.Json.Nodes;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var exe = Path.Combine(root, "Binaries/Win64/Debug/Editor/Engine.exe");
var sessionId = Guid.NewGuid().ToString("N");
using var editor = Start(exe, "--mcp-isolated", "--mcp-session-id", sessionId);
var editorErrors = editor.StandardError.ReadToEndAsync();
var editorOutput = editor.StandardOutput.ReadToEndAsync();
Process? bridge = null;
var assertions = 0;
var requestId = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++;
}
try
{
    var registration = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SharpEngine/McpSessions", sessionId + ".json");
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (!File.Exists(registration) && DateTime.UtcNow < deadline && !editor.HasExited) Thread.Sleep(50);
    Check(File.Exists(registration), "Editor must register a live session");
    bridge = Start(exe, "--mcp-stdio");
    _ = bridge.StandardError.ReadToEndAsync();
    JsonObject Call(string method, JsonObject args)
    {
        var id = ++requestId;
        bridge.StandardInput.WriteLine(new JsonObject
        { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = args }.ToJsonString());
        bridge.StandardInput.Flush();
        while (true)
        {
            var line = bridge.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult()
                ?? throw new IOException("MCP bridge closed");
            var response = JsonNode.Parse(line)!.AsObject();
            if (response["id"]?.GetValue<int>() != id) continue;
            if (response["error"] is { } error) throw new IOException(error.ToJsonString());
            return response["result"]!.AsObject();
        }
    }
    JsonObject Tool(string name, JsonObject? args = null) =>
        Call("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args ?? new() })["structuredContent"]!.AsObject();
    JsonObject Ui() => Tool("ui.describe");
    JsonObject Demo() => Tool("editor.describe")["inputDemo"]!.AsObject();
    void Click(string elementId)
    {
        foreach (var phase in new[] { "down", "up" })
        {
            var accepted = false;
            for (var attempt = 0; attempt < 10 && !accepted; attempt++)
            {
                var ui = Ui();
                var host = ui["hosts"]!.AsArray().Single(n => n!["hostId"]!.GetValue<string>() == "input-demo")!;
                var element = host["elements"]!.AsArray().Single(n => n!["elementId"]!.GetValue<string>() == "host/input-demo/id/" + elementId)!;
                var bounds = element["bounds"]!;
                var result = Tool("ui.pointer", new JsonObject
                {
                    ["hostId"] = "input-demo", ["event"] = phase, ["button"] = "left",
                    ["x"] = (int)(bounds["x"]!.GetValue<double>() + bounds["width"]!.GetValue<double>() * .75),
                    ["y"] = (int)(bounds["y"]!.GetValue<double>() + bounds["height"]!.GetValue<double>() * .7),
                    ["buttons"] = phase == "up" ? new JsonArray("left") : new JsonArray(),
                    ["modifiers"] = new JsonArray(), ["pointerId"] = 0,
                    ["expectedFrameId"] = ui["frameId"]!.DeepClone(),
                    ["expectedElementId"] = "host/input-demo/id/" + elementId, ["wheelX"] = 0, ["wheelY"] = 0,
                });
                accepted = result["accepted"]!.GetValue<bool>();
                if (!accepted && result["diagnostics"]?[0]?["code"]?.GetValue<string>() != "UI_FRAME_STALE")
                    throw new InvalidOperationException(result.ToJsonString());
            }
            Check(accepted, $"Coordinate {phase} must reach {elementId}");
        }
    }
    void Key(uint key, string phase)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var ui = Ui();
            var result = Tool("ui.key", new JsonObject
            {
                ["hostId"] = "input-demo", ["event"] = phase, ["key"] = key,
                ["modifiers"] = new JsonArray(), ["expectedFrameId"] = ui["frameId"]!.DeepClone(),
            });
            if (result["accepted"]!.GetValue<bool>()) return;
            if (result["diagnostics"]?[0]?["code"]?.GetValue<string>() != "UI_FRAME_STALE")
                throw new InvalidOperationException(result.ToJsonString());
        }
        throw new InvalidOperationException("Keyboard frame never stabilized");
    }
    _ = Call("initialize", new());
    _ = Tool("editor.session.attach", new JsonObject { ["sessionId"] = sessionId });
    _ = Tool("editor.action", new JsonObject { ["action"] = "editor.input-demo" });
    Click("demo-play-area");
    Key(0x20, "down"); Key(0x20, "up");
    var firstPress = Demo();
    Check(firstPress["uiCount"]!.GetValue<int>() == 1 && firstPress["gameplayCount"]!.GetValue<int>() == 0,
        "UI consumption must prevent Gameplay from receiving the same Space press: " + firstPress.ToJsonString() + Tool("input.describe").ToJsonString());
    Click("demo-toggle");
    Click("demo-play-area");
    Key(0x20, "down"); Key(0x20, "up");
    Check(Demo()["uiCount"]!.GetValue<int>() == 1 && Demo()["gameplayCount"]!.GetValue<int>() == 1,
        "An unconsumed Space press must reach Gameplay");
    var beforeMove = Demo()["x"]!.GetValue<float>();
    Key('D', "down"); Key('D', "up");
    Check(Demo()["x"]!.GetValue<float>() > beforeMove, "Gameplay Axis2D must move the visible marker");
    Click("demo-text");
    Key(0x20, "down"); Key(0x20, "up");
    Check(Demo()["gameplayCount"]!.GetValue<int>() == 1, "Focused text input must block Gameplay");
    var input = Tool("input.describe");
    Check(input["contexts"]!.AsArray().Any(n => n!["route"]!.GetValue<string>() == "RuntimeUi" && n["active"]!.GetValue<bool>()),
        "The shared input snapshot must expose the active UI context");
    var frame = Ui();
    var png = Call("resources/read", new JsonObject
    { ["uri"] = $"sharpengine://ui/frames/{frame["frameId"]!.GetValue<ulong>()}/input-demo.png" });
    var artifact = Path.Combine(root, "Binaries/Win64/Debug/Editor/input-routing-demo.png");
    File.WriteAllBytes(artifact, Convert.FromBase64String(png["contents"]![0]!["blob"]!.GetValue<string>()));
    Console.WriteLine($"PASS: {assertions} rendered unified-input assertions; screenshot={artifact}");
}
finally
{
    if (bridge is { HasExited: false }) { bridge.Kill(true); bridge.WaitForExit(); }
    bridge?.Dispose();
    if (!editor.HasExited) { editor.Kill(true); editor.WaitForExit(); }
    var errors = editorErrors.GetAwaiter().GetResult();
    if (errors.Contains("failed", StringComparison.OrdinalIgnoreCase)) Console.Error.WriteLine(errors);
}

static Process Start(string exe, params string[] args)
{
    var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in args) info.ArgumentList.Add(arg);
    return Process.Start(info) ?? throw new IOException("Unable to start engine");
}
