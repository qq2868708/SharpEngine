using System.Text.Json.Nodes;

internal static class SettingsUiValidation
{
    public static void Run(string root, Action<bool, string> require,
        Func<int, string, JsonObject, JsonObject> tool,
        Func<int, string, JsonObject?, JsonObject> call)
    {
        var sequence = 1000;
        JsonObject Result(JsonObject response) =>
            response["result"]?["structuredContent"]?.AsObject() ??
            throw new InvalidDataException(response.ToJsonString());
        JsonObject Read() => Result(tool(sequence++, "ui.describe", new()));
        void AwaitHost(string hostId, bool present)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (Read()["hosts"]!.AsArray().Any(node =>
                        node!["hostId"]!.GetValue<string>() == hostId) == present) return;
                Thread.Sleep(25);
            }
            throw new InvalidDataException($"Host {hostId} did not reach present={present}");
        }
        JsonObject Host(JsonObject snapshot, string id) => snapshot["hosts"]!.AsArray()
            .Select(node => node!.AsObject()).SingleOrDefault(node => node["hostId"]!.GetValue<string>() == id)
            ?? throw new InvalidDataException($"Missing host {id}; hosts: " +
                string.Join(", ", snapshot["hosts"]!.AsArray().Select(node => node!["hostId"])) +
                "; status: " + snapshot["hosts"]![0]!["elements"]!.AsArray()
                    .FirstOrDefault(node => node!["semanticId"]?.GetValue<string>() == "statusText")?["text"]);
        void Click(string hostId, string id, bool action = false)
        {
            var snapshot = Read();
            var element = Host(snapshot, hostId)["elements"]!.AsArray().Select(node => node!.AsObject())
                .FirstOrDefault(node => action
                    ? node["actions"]!.AsArray().Any(value => value?.GetValue<string>() == id)
                    : node["semanticId"]?.GetValue<string>() == id ||
                      node["elementId"]!.GetValue<string>() == id)
                ?? throw new InvalidDataException($"Missing {hostId}/{id}");
            require(element["visible"]!.GetValue<bool>() && element["hitTarget"]!.GetValue<bool>(),
                $"Settings control {hostId}/{id} must be visible and hit-testable");
            var bounds = element["bounds"]!;
            var frame = snapshot["frameId"]!.GetValue<ulong>();
            foreach (var inputEvent in new[] { "down", "up" })
            {
                var result = Result(tool(sequence++, "ui.pointer", new()
                {
                    ["hostId"] = hostId, ["event"] = inputEvent, ["button"] = "left",
                    ["x"] = (int)(bounds["x"]!.GetValue<double>() + bounds["width"]!.GetValue<double>() / 2),
                    ["y"] = (int)(bounds["y"]!.GetValue<double>() + bounds["height"]!.GetValue<double>() / 2),
                    ["buttons"] = inputEvent == "up" ? new JsonArray("left") : new JsonArray(),
                    ["modifiers"] = new JsonArray(), ["pointerId"] = 0,
                    ["expectedFrameId"] = frame, ["expectedElementId"] = element["elementId"]!.GetValue<string>(),
                    ["wheelX"] = 0, ["wheelY"] = 0,
                }));
                require(result["accepted"]!.GetValue<bool>(), $"Coordinate click must reach {id}: {result}");
                frame = result["frameId"]!.GetValue<ulong>();
            }
            if (action && id == "window.close") AwaitHost(hostId, present: false);
        }
        JsonObject Page() => Result(tool(sequence++, "editor.describe", new()))["settings"]!["pages"]!
            .AsArray().Select(node => node!.AsObject())
            .Single(page => page["id"]!.GetValue<string>() == "sharpengine.editor-navigation");
        void Open(string kind)
        {
            Click("main", "menu-root-file");
            Click("main", $"menu-file-category-settings-command-core.{kind}-settings");
            AwaitHost("settings." + kind, present: true);
        }
        void Capture(string hostId, string fileName)
        {
            var snapshot = Read();
            var host = Host(snapshot, hostId);
            var frame = call(sequence++, "resources/read", new JsonObject
            {
                ["uri"] = $"sharpengine://ui/frames/{snapshot["frameId"]!.GetValue<ulong>()}/{hostId}.png",
            });
            var content = frame["result"]!["contents"]![0]!;
            var pixels = Convert.FromBase64String(content["blob"]!.GetValue<string>());
            require(content["mimeType"]!.GetValue<string>() == "image/png" &&
                host["bounds"]!["width"]!.GetValue<double>() > 0 && pixels.Length > 1000,
                "Settings windows must expose a rendered frame matching their semantic host");
            var directory = Path.Combine(root, "Saved", "Validation", "Settings");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, fileName), pixels);
        }

        Open("engine");
        var initial = Page()["values"]!["invertYAxis"]!.GetValue<bool>();
        Click("settings.engine", "setting-invertYAxis");
        require(Page()["values"]!["invertYAxis"]!.GetValue<bool>() != initial && Page()["dirty"]!.GetValue<bool>(),
            "Actual toggle input must commit settings and dirty the page");
        Click("settings.engine", "settings-undo");
        require(Page()["values"]!["invertYAxis"]!.GetValue<bool>() == initial,
            "Actual Undo button must restore the prior value");
        Click("settings.engine", "settings-redo");
        require(Page()["values"]!["invertYAxis"]!.GetValue<bool>() != initial,
            "Actual Redo button must restore the edited value");
        Capture("settings.engine", "engine-settings.png");
        Click("settings.engine", "window.close", action: true);
        Open("engine");
        require(Page()["dirty"]!.GetValue<bool>(), "Closing a settings window must preserve unsaved values");
        Click("settings.engine", "settings-revert");
        require(!Page()["dirty"]!.GetValue<bool>() &&
                Page()["values"]!["invertYAxis"]!.GetValue<bool>() == initial,
            "Reopened window must revert to the savepoint");
        Open("project");
        var projectPage = Result(tool(sequence++, "editor.describe", new()))["settings"]!["pages"]!
            .AsArray().FirstOrDefault(node => node!["scope"]!.GetValue<string>() == "project");
        if (projectPage is not null)
        {
            var nextSpeed = projectPage["values"]!["moveSpeed"]!.GetValue<float>() == 123 ? 124 : 123;
            var typedSpeed = nextSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Click("settings.project", "setting-moveSpeed");
            JsonObject Key(string inputEvent, uint key, string? text, bool control = false)
            {
                for (var attempt = 0; ; attempt++)
                {
                    var response = Result(tool(sequence++, "ui.key", new()
                    {
                        ["hostId"] = "settings.project", ["event"] = inputEvent, ["key"] = key,
                        ["text"] = text, ["modifiers"] = control ? new JsonArray("control") : new JsonArray(),
                        ["expectedFrameId"] = Read()["frameId"]!.GetValue<ulong>(),
                    }));
                    if (attempt >= 5 || response["diagnostics"]?.AsArray().Any(diagnostic =>
                            diagnostic?["code"]?.GetValue<string>() == "UI_FRAME_STALE") != true)
                        return response;
                }
            }
            require(Key("down", 0x41, null, true)["accepted"]!.GetValue<bool>(), "Ctrl+A must reach settings input");
            Key("up", 0x41, null, true);
            foreach (var character in typedSpeed)
            {
                var typed = Key("text", 0, character.ToString());
                require(typed["accepted"]!.GetValue<bool>(),
                    $"Repeated character input must retain settings text focus: {typed}");
            }
            var observed = Host(Read(), "settings.project")["elements"]!.AsArray()
                .First(node => node!["semanticId"]?.GetValue<string>() == "setting-moveSpeed")!;
            require(observed["focused"]!.GetValue<bool>() && observed["value"]!.GetValue<string>() == typedSpeed,
                "Typing several characters must preserve input focus and the complete value");
            Click("settings.project", "settings-save");
            var projectId = projectPage["id"]!.GetValue<string>();
            var savedPage = Result(tool(sequence++, "editor.describe", new()))["settings"]!["pages"]!
                .AsArray().First(node => node!["id"]!.GetValue<string>() == projectId)!;
            require(!savedPage["dirty"]!.GetValue<bool>() &&
                savedPage["values"]!["moveSpeed"]!.GetValue<float>() == nextSpeed,
                "Project module discovery, coordinate typing and Save must update the real settings page");
            var saved = JsonNode.Parse(File.ReadAllText(savedPage["path"]!.GetValue<string>()))!;
            require(saved["values"]!["moveSpeed"]!.GetValue<float>() == nextSpeed,
                "Save through the visible Project Settings window must write the project override");
        }
        Capture("settings.project", "project-settings.png");
        Open("engine");
        require(Read()["hosts"]!.AsArray().Count(node =>
                node!["hostId"]!.GetValue<string>().StartsWith("settings.")) == 2,
            "Repeated File commands must reuse the two independent settings windows");
        Click("settings.engine", "window.close", action: true);
        Click("settings.project", "window.close", action: true);
        require(Read()["hosts"]!.AsArray().Count == 1,
            "Both settings windows must close independently of the main dock layout");
    }
}
