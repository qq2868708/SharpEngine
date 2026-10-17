using System.Text.Json.Nodes;

internal static class InspectorUiValidation
{
    public static void Run(string root, Action<bool, string> require,
        Func<int, string, JsonObject, JsonObject> tool, Func<int, string, JsonObject?, JsonObject> call)
    {
        int sequence = 4000;
        JsonObject Result(JsonObject response) => response["result"]?["structuredContent"]?.AsObject()
            ?? throw new InvalidDataException(response.ToJsonString());
        JsonObject Read() => Result(tool(sequence++, "ui.describe", new()));
        JsonObject Data() => Result(tool(sequence++, "components.describe", new()));
        JsonObject Host(JsonObject snapshot, string hostId = "main") => snapshot["hosts"]!.AsArray().Select(n => n!.AsObject()).Single(h => h["hostId"]!.GetValue<string>() == hostId);
        JsonObject Find(JsonObject snapshot, string id, string hostId = "main") => Host(snapshot, hostId)["elements"]!.AsArray().Select(n => n!.AsObject())
            .FirstOrDefault(e => e["semanticId"]?.GetValue<string>() == id || e["actions"]!.AsArray().Any(a => a?.GetValue<string>() == id))
            ?? throw new InvalidDataException("Missing control " + id + ": " + Host(snapshot)["elements"]!.ToJsonString());
        void Click(string id, string hostId = "main")
        {
            var snapshot = Read(); var element = Find(snapshot, id, hostId);
            for (int attempt = 0; attempt < 10 && (!element["visible"]!.GetValue<bool>() || !element["hitTarget"]!.GetValue<bool>()); attempt++)
            {
                var pane = Find(snapshot, "inspectorContent", hostId)["bounds"]!;
                var delta = element["bounds"]!["y"]!.GetValue<double>() < pane["y"]!.GetValue<double>() ? -3 : 3;
                _ = Result(tool(sequence++, "ui.pointer", new() { ["hostId"] = "main", ["event"] = "wheel", ["button"] = "none",
                    ["x"] = (int)(pane["x"]!.GetValue<double>() + pane["width"]!.GetValue<double>() - 25),
                    ["y"] = (int)(pane["y"]!.GetValue<double>() + pane["height"]!.GetValue<double>() / 2),
                    ["buttons"] = new JsonArray(), ["modifiers"] = new JsonArray(), ["pointerId"] = 0,
                    ["expectedFrameId"] = snapshot["frameId"]!.GetValue<ulong>(), ["expectedElementId"] = null, ["wheelX"] = 0, ["wheelY"] = delta }));
                snapshot = Read(); element = Find(snapshot, id, hostId);
            }
            var bounds = element["bounds"]!;
            if (element["visible"]!.GetValue<bool>() != true || element["hitTarget"]!.GetValue<bool>() != true)
            {
                var output = Path.Combine(root, "Saved", "Validation", "Inspector"); Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "failed-ui.json"), snapshot.ToJsonString());
                var pixels = call(sequence++, "resources/read", new() { ["uri"] = $"sharpengine://ui/frames/{snapshot["frameId"]}/main.png" });
                File.WriteAllBytes(Path.Combine(output, "failed-ui.png"), Convert.FromBase64String(pixels["result"]!["contents"]![0]!["blob"]!.GetValue<string>()));
            }
            require(element["visible"]!.GetValue<bool>() && element["hitTarget"]!.GetValue<bool>(), "Inspector control is visible and hit-testable: " + id);
            var frame = snapshot["frameId"]!.GetValue<ulong>();
            foreach (var ev in new[] { "down", "up" })
            {
                if (ev == "up")
                {
                    var current = Read(); element = Find(current, id, hostId); bounds = element["bounds"]!;
                    frame = current["frameId"]!.GetValue<ulong>();
                }
                JsonObject result;
                for (int attempt = 0; ; attempt++)
                {
                if (attempt > 0)
                {
                    Thread.Sleep(40);
                    var current = Read(); element = Find(current, id, hostId); bounds = element["bounds"]!;
                    frame = current["frameId"]!.GetValue<ulong>();
                }
                result = Result(tool(sequence++, "ui.pointer", new()
                {
                    ["hostId"] = hostId, ["event"] = ev, ["button"] = "left",
                    ["x"] = (int)(bounds["x"]!.GetValue<double>() + bounds["width"]!.GetValue<double>() / 2),
                    ["y"] = (int)(bounds["y"]!.GetValue<double>() + bounds["height"]!.GetValue<double>() / 2),
                    ["buttons"] = ev == "up" ? new JsonArray("left") : new JsonArray(), ["modifiers"] = new JsonArray(), ["pointerId"] = 0,
                    ["expectedFrameId"] = frame, ["expectedElementId"] = element["elementId"]!.GetValue<string>(), ["wheelX"] = 0, ["wheelY"] = 0,
                }));
                if (attempt >= 5 || result["diagnostics"]?.AsArray().Any(d => d?["code"]?.GetValue<string>() is "UI_FRAME_STALE" or "UI_TARGET_CHANGED") != true) break;
                }
                require(result["accepted"]!.GetValue<bool>(), "Real pointer input reaches " + id + ": " + result);
                frame = result["frameId"]!.GetValue<ulong>();
            }
        }
        void Key(string ev, uint key = 0, string? text = null, bool control = false)
        {
            JsonObject result;
            for (int attempt = 0; ; attempt++)
            {
            result = Result(tool(sequence++, "ui.key", new()
            {
                ["hostId"] = "main", ["event"] = ev, ["key"] = key, ["text"] = text,
                ["modifiers"] = control ? new JsonArray("control") : new JsonArray(),
                ["expectedFrameId"] = Read()["frameId"]!.GetValue<ulong>(),
            }));
            if (attempt >= 5 || result["diagnostics"]?.AsArray().Any(d => d?["code"]?.GetValue<string>() == "UI_FRAME_STALE") != true) break;
            }
            require(result["accepted"]!.GetValue<bool>(), "Real keyboard input reaches Inspector: " + result);
        }
        void Type(string field, string text, bool commit)
        {
            Click(field); Key("down", 0x41, control: true); Key("up", 0x41, control: true);
            Key("text", text: text);
            if (commit) { Key("down", 0x0D); Key("up", 0x0D); }
        }
        void DragMainDockSplitterPreservesInspector(string controlId)
        {
            var before = Read();
            var splitter = Host(before)["elements"]!.AsArray().Select(node => node!.AsObject())
                .FirstOrDefault(element => element["role"]?.GetValue<string>() == "splitter" &&
                    element["semanticId"]?.GetValue<string>() == "main.body")
                ?? throw new InvalidDataException("Main body splitter was unavailable");
            var splitterBounds = splitter["bounds"]!.AsObject();
            var x = (int)(splitterBounds["x"]!.GetValue<double>() +
                splitterBounds["width"]!.GetValue<double>() / 2);
            var y = (int)(splitterBounds["y"]!.GetValue<double>() +
                splitterBounds["height"]!.GetValue<double>() / 2);
            var frame = before["frameId"]!.GetValue<ulong>();
            var elementId = splitter["elementId"]!.GetValue<string>();
            JsonObject Pointer(string inputEvent, string button, ulong expectedFrame,
                string expectedElement, int pointerX, int pointerY, string[]? buttons = null)
            {
                var result = Result(tool(sequence++, "ui.pointer", new JsonObject
                {
                    ["hostId"] = "main", ["event"] = inputEvent, ["button"] = button,
                    ["x"] = pointerX, ["y"] = pointerY,
                    ["buttons"] = new JsonArray((buttons ?? []).Select(value =>
                        (JsonNode?)JsonValue.Create(value)).ToArray()),
                    ["modifiers"] = new JsonArray(), ["pointerId"] = 0,
                    ["expectedFrameId"] = expectedFrame, ["expectedElementId"] = expectedElement,
                    ["wheelX"] = 0, ["wheelY"] = 0,
                }));
                require(result["accepted"]!.GetValue<bool>(),
                    $"Inspector splitter {inputEvent} must be accepted: {result}");
                return result;
            }
            var down = Pointer("down", "left", frame, elementId, x, y);
            var move = Pointer("move", "none", down["frameId"]!.GetValue<ulong>(),
                elementId, x + 24, y, ["left"]);
            var preview = Read();
            var previewControl = Find(preview, controlId);
            require(previewControl["visible"]!.GetValue<bool>(),
                "Inspector component remains visible during splitter preview");
            var up = Pointer("up", "left", move["frameId"]!.GetValue<ulong>(),
                elementId, x + 24, y, ["left"]);
            var committed = Read();
            var committedControl = Find(committed, controlId);
            require(up["accepted"]!.GetValue<bool>() &&
                    committedControl["visible"]!.GetValue<bool>(),
                "Inspector component remains visible after splitter layout commit");
        }
        JsonObject Entity(string id) => Data()["document"]!["entities"]!.AsArray().Single(e => e!["entityId"]!.GetValue<string>() == id)!.AsObject();
        Click("editor.component-create-entity");
        var created = Data()["document"]!["entities"]!.AsArray().Last()!;
        var entityId = created["entityId"]!.GetValue<string>();
        var transform = created["components"]![0]!["componentId"]!.GetValue<string>();
        Click("editor.component-collapse:" + transform);
        Click("add-component");
        Click("editor.component-add:engine.camera");
        var camera = Entity(entityId)["components"]!.AsArray().Single(c => c!["typeId"]!.GetValue<string>() == "engine.camera")!;
        var componentId = camera["componentId"]!.GetValue<string>();
        var fovControl = "component-field-" + componentId + "-FieldOfView";
        Type(fovControl, "123", commit: false);
        require(Entity(entityId)["components"]!.AsArray().Last()!["state"]!["FieldOfView"]!.GetValue<float>() == 60, "Typing remains in local edit buffer");
        Key("down", 0x0D); Key("up", 0x0D);
        require(Entity(entityId)["components"]!.AsArray().Last()!["state"]!["FieldOfView"]!.GetValue<float>() == 123, "Enter commits Inspector field through transaction layer");
        DragMainDockSplitterPreservesInspector(fovControl);
        Type(fovControl, "124", commit: false);
        Click("editor.select-entity:player");
        require(Entity(entityId)["components"]!.AsArray().Last()!["state"]!["FieldOfView"]!.GetValue<float>() == 124,
            "Blur caused by selection change commits to the original component owner");
        Click("editor.select-entity:" + entityId);
        Type(fovControl, "123", commit: true);
        Type(fovControl, "999", commit: true);
        require(Entity(entityId)["components"]!.AsArray().Last()!["state"]!["FieldOfView"]!.GetValue<float>() == 123, "Invalid range input leaves committed state intact");
        Key("down", 0x1B); Key("up", 0x1B);
        require(Find(Read(), fovControl)["value"]!.GetValue<string>() == "123", "Escape restores committed field value");
        var snapshot = Read();
        var frame = call(sequence++, "resources/read", new() { ["uri"] = $"sharpengine://ui/frames/{snapshot["frameId"]}/main.png" });
        var png = Convert.FromBase64String(frame["result"]!["contents"]![0]!["blob"]!.GetValue<string>());
        var directory = Path.Combine(root, "Saved", "Validation", "Inspector"); Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "component-inspector.png"), png);
        require(png.Length > 1000, "Inspector has a rendered frame matching its semantic snapshot");
        Click("editor.component-reset:" + componentId);
        require(Entity(entityId)["components"]!.AsArray().Last()!["state"]!["FieldOfView"]!.GetValue<float>() == 60, "Reset restores generated defaults");
        Click("editor.component-remove:" + componentId);
        require(Entity(entityId)["components"]!.AsArray().Count == 1, "Remove deletes component through actual Inspector control");
        var projectType = Data()["types"]!.AsArray().FirstOrDefault(t => t!["name"]!.GetValue<string>() == "HealthComponent");
        if (projectType is not null)
        {
            Click("editor.component-add:" + projectType["typeId"]!.GetValue<string>());
            var health = Entity(entityId)["components"]!.AsArray().Last()!;
            var healthId = health["componentId"]!.GetValue<string>();
            require(health["state"]!["MaxHealth"]!.GetValue<float>() == 100 && health["state"]!["_health"]!.GetValue<float>() == 80,
                "Runtime Module generated public/private defaults appear in Authoring Inspector");
            Type("component-field-" + healthId + "-MaxHealth", "145", commit: true);
            require(Entity(entityId)["components"]!.AsArray().Last()!["state"]!["MaxHealth"]!.GetValue<float>() == 145,
                "Project Module field commits through the same visible Inspector");
            Click("editor.prefab-export");
            require(Data()["document"]!["kind"]!.GetValue<string>() == "prefab" && Read()["hosts"]!.AsArray().Any(h => h!["hostId"]!.GetValue<string>() == "prefab-editor"),
                "Export opens an independent rendered Prefab document window");
            Type("component-field-" + healthId + "-MaxHealth", "150", commit: true);
            Click("editor.prefab-save", "prefab-editor");
            var prefabFile = Result(tool(sequence++, "editor.describe", new()))["world"]?["path"]?.GetValue<string>();
            Click("editor.prefab-world", "prefab-editor");
            require(Data()["document"]!["kind"]!.GetValue<string>() == "world" && Entity(entityId)["components"]!.AsArray().Last()!["state"]!["MaxHealth"]!.GetValue<float>() == 145,
                "Returning from Prefab restores independent World values");
            Click("editor.content.open:content:project|Prefabs");
            var assets = Result(tool(sequence++, "assets.describe", new()));
            var prefabAsset = assets["assets"]!.AsArray().Last(a => a!["type"]!.GetValue<string>() == "Prefab")!;
            var assetId = prefabAsset["id"]!.GetValue<string>();
            var beforeCount = Data()["document"]!["entities"]!.AsArray().Count;
            var startSnapshot = Read();
            var source = Find(startSnapshot, "editor.content.select:content:" + assetId);
            var target = Find(startSnapshot, "editor.select-entity:player");
            int Center(JsonObject item, string axis, string size) => (int)(item["bounds"]![axis]!.GetValue<double>() + item["bounds"]![size]!.GetValue<double>() / 2);
            JsonObject Pointer(string ev, int x, int y, string? expected, bool held)
            {
                JsonObject result;
                for (int attempt = 0; ; attempt++)
                {
                var snapshot = Read();
                result = Result(tool(sequence++, "ui.pointer", new() { ["hostId"] = "main", ["event"] = ev, ["button"] = ev == "move" ? "none" : "left", ["x"] = x, ["y"] = y,
                    ["buttons"] = held ? new JsonArray("left") : new JsonArray(), ["modifiers"] = new JsonArray(), ["pointerId"] = 0,
                    ["expectedFrameId"] = snapshot["frameId"]!.GetValue<ulong>(), ["expectedElementId"] = expected, ["wheelX"] = 0, ["wheelY"] = 0 }));
                if (attempt >= 5 || result["diagnostics"]?.AsArray().Any(d => d?["code"]?.GetValue<string>() == "UI_FRAME_STALE") != true) break;
                }
                require(result["accepted"]!.GetValue<bool>(), "Prefab pointer " + ev + " must be accepted: " + result);
                return result;
            }
            require(Pointer("down", Center(source,"x","width"), Center(source,"y","height"), source["elementId"]!.GetValue<string>(), false)["accepted"]!.GetValue<bool>(), "Prefab drag begins through real asset hit testing");
            _ = Pointer("move", Center(source,"x","width") + 20, Center(source,"y","height") - 15, null, true);
            _ = Pointer("move", Center(target,"x","width"), Center(target,"y","height"), null, true);
            _ = Pointer("up", Center(target,"x","width"), Center(target,"y","height"), null, true);
            require(Data()["document"]!["entities"]!.AsArray().Count == beforeCount + 1,
                "Dropping Prefab onto Hierarchy creates its subtree through the actual drag path");
        }
    }
}
