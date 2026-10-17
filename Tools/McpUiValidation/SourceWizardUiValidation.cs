using System.Text.Json.Nodes;

internal static class SourceWizardUiValidation
{
    internal static void Run(string root, Action<bool, string> check,
        Func<int, string, JsonObject, JsonObject> tool, Func<int, string, JsonObject?, JsonObject> call)
    {
        var sequence = 8000;
        JsonObject Result(JsonObject response) => response["result"]?["structuredContent"]?.AsObject()
            ?? throw new Exception(response.ToJsonString());
        JsonObject Read() => Result(tool(sequence++, "ui.describe", new()));
        JsonObject Host(JsonObject snapshot, string host) => snapshot["hosts"]!.AsArray()
            .Select(node => node!.AsObject()).Single(node => node["hostId"]!.GetValue<string>() == host);
        void Click(string host, string id)
        {
            var snapshot = Read();
            var element = Host(snapshot, host)["elements"]!.AsArray().Select(node => node!.AsObject())
                .First(node => node["semanticId"]?.GetValue<string>() == id || node["elementId"]?.GetValue<string>() == id);
            check(element["visible"]!.GetValue<bool>() && element["hitTarget"]!.GetValue<bool>(), "Visible control: " + id);
            var bounds = element["bounds"]!;
            var frame = snapshot["frameId"]!.GetValue<ulong>();
            foreach (var action in new[] { "down", "up" })
            {
                var result = Result(tool(sequence++, "ui.pointer", new()
                {
                    ["hostId"] = host, ["event"] = action, ["button"] = "left",
                    ["x"] = (int)(bounds["x"]!.GetValue<double>() + bounds["width"]!.GetValue<double>() / 2),
                    ["y"] = (int)(bounds["y"]!.GetValue<double>() + bounds["height"]!.GetValue<double>() / 2),
                    ["buttons"] = action == "up" ? new JsonArray("left") : new JsonArray(), ["modifiers"] = new JsonArray(),
                    ["pointerId"] = 0, ["expectedFrameId"] = frame, ["expectedElementId"] = element["elementId"]!.GetValue<string>(),
                    ["wheelX"] = 0, ["wheelY"] = 0,
                }));
                check(result["accepted"]!.GetValue<bool>(), "Coordinate input reaches " + id + ": " + result);
                frame = result["frameId"]!.GetValue<ulong>();
            }
        }
        void Type(string id, string text)
        {
            Click("source.create", id);
            foreach (var character in text)
            {
                for (var attempt = 0; ; attempt++)
                {
                    var response = Result(tool(sequence++, "ui.key", new()
                    {
                        ["hostId"] = "source.create", ["event"] = "text", ["key"] = 0,
                        ["text"] = character.ToString(), ["modifiers"] = new JsonArray(),
                        ["expectedFrameId"] = Read()["frameId"]!.GetValue<ulong>(),
                    }));
                    if (response["accepted"]!.GetValue<bool>()) break;
                    if (attempt == 5) throw new Exception(response.ToJsonString());
                }
            }
        }
        Click("main", "menu-root-file");
        Click("main", "menu-file-category-project-command-core.new-csharp");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!Read()["hosts"]!.AsArray().Any(host => host!["hostId"]!.GetValue<string>() == "source.create"))
        { if (DateTime.UtcNow > deadline) throw new Exception("Source wizard did not open"); Thread.Sleep(50); }
        var name = "UiCreated" + Guid.NewGuid().ToString("N")[..8];
        Type("source-directory", "UiTests");
        Type("source-name", name);
        var snapshot = Read();
        var host = Host(snapshot, "source.create");
        check(host["elements"]!.AsArray().Any(node => node!["semanticId"]?.GetValue<string>() == "source-namespace" &&
            node["text"]!.GetValue<string>().EndsWith(".UiTests")), "Wizard displays derived readonly namespace");
        var frame = call(sequence++, "resources/read", new()
        { ["uri"] = $"sharpengine://ui/frames/{snapshot["frameId"]!.GetValue<ulong>()}/source.create.png" });
        var image = Convert.FromBase64String(frame["result"]!["contents"]![0]!["blob"]!.GetValue<string>());
        var evidence = Path.Combine(root, "Saved", "Validation", "SourceWizard");
        Directory.CreateDirectory(evidence); File.WriteAllBytes(Path.Combine(evidence, "wizard.png"), image);
        Click("source.create", "source-create");
        var manifest = Environment.GetEnvironmentVariable("SHARPENGINE_VALIDATION_PROJECT")!;
        var path = Path.Combine(Path.GetDirectoryName(manifest)!, "Source", "Game", "UiTests", name + ".cs");
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(path)) { if (DateTime.UtcNow > deadline) throw new Exception("UI did not create source: " + Host(Read(), "source.create")); Thread.Sleep(50); }
        var source = File.ReadAllText(path);
        check(source.Contains("partial class " + name) && source.Contains("namespace Sample.Game.UiTests;"), "Actual wizard click creates Component source in the selected Module");
        deadline = DateTime.UtcNow.AddSeconds(100);
        while (!Result(tool(sequence++, "components.describe", new())).ToJsonString().Contains(name, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Created component was not reloaded: " + Result(tool(sequence++, "source.status", new())));
            Thread.Sleep(100);
        }
        check(true, "Created component becomes discoverable without restarting Editor");
        Console.WriteLine("UI evidence: " + Path.Combine(evidence, "wizard.png"));
    }
}
