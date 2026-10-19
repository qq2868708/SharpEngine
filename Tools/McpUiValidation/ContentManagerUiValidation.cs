using System.Text.Json.Nodes;

internal static class ContentManagerUiValidation
{
    internal static void Run(string root,
        Func<int, string, JsonObject, JsonObject> tool,
        Func<int, string, JsonObject?, JsonObject> call)
    {
        var requestId = 1000;
        var assertions = 0;
        var output = Path.Combine(root, "Saved", "Validation", "ContentManager");
        Directory.CreateDirectory(output);
        void Require(bool success, string message)
        {
            if (!success) throw new InvalidOperationException(message);
            assertions++;
        }
        JsonObject Invoke(string name, JsonObject? args = null)
        {
            var response = tool(requestId++, name, args?.DeepClone().AsObject() ?? new());
            return response["result"]?["structuredContent"]?.AsObject() ??
                throw new InvalidOperationException(response.ToJsonString());
        }
        JsonObject Snapshot() => Invoke("ui.describe");
        JsonObject Content() => Invoke("content.describe");
        JsonObject State(string pane) => Content()["managers"]!.AsArray()
            .Single(m => m!["paneId"]!.GetValue<string>() == pane)!["state"]!.AsObject();
        IEnumerable<(JsonObject Host, JsonObject Element)> Elements(JsonObject snapshot) =>
            snapshot["hosts"]!.AsArray().SelectMany(h => h!["elements"]!.AsArray()
                .Select(e => (h.AsObject(), e!.AsObject())));
        bool Match(JsonObject e, string id) => e["elementId"]?.GetValue<string>() == id ||
            e["elementId"]!.GetValue<string>().EndsWith("/id/" + id, StringComparison.Ordinal) ||
            e["actions"]!.AsArray().Any(a => a!.GetValue<string>() == id);
        void SaveFrames(string name)
        {
            var snapshot = Snapshot();
            File.WriteAllText(Path.Combine(output, name + ".json"), snapshot.ToJsonString());
            foreach (var h in snapshot["hosts"]!.AsArray())
            {
                var hostId = h!["hostId"]!.GetValue<string>();
                var frame = call(requestId++, "resources/read", new JsonObject
                {
                    ["uri"] = $"sharpengine://ui/frames/{snapshot["frameId"]}/{hostId}.png",
                });
                var blob = frame["result"]?["contents"]?[0]?["blob"]?.GetValue<string>()
                    ?? throw new InvalidOperationException(frame.ToJsonString());
                File.WriteAllBytes(Path.Combine(output, name + "-" + hostId + ".png"),
                    Convert.FromBase64String(blob));
            }
        }
        JsonObject Click(string id, string button = "left")
        {
            var snap = Snapshot();
            var target = Elements(snap).FirstOrDefault(p => Match(p.Element, id) &&
                p.Element["visible"]!.GetValue<bool>() && p.Element["hitTarget"]!.GetValue<bool>());
            if (target.Element is null)
            {
                SaveFrames("failure");
                throw new InvalidOperationException($"Control is not visible/clickable: {id}");
            }
            var b = target.Element["bounds"]!;
            var args = new JsonObject
            {
                ["hostId"] = target.Host["hostId"]!.GetValue<string>(),
                ["event"] = "down", ["button"] = button,
                ["x"] = (int)(b["x"]!.GetValue<double>() + b["width"]!.GetValue<double>() / 2),
                ["y"] = (int)(b["y"]!.GetValue<double>() + b["height"]!.GetValue<double>() / 2),
                ["buttons"] = new JsonArray(), ["modifiers"] = new JsonArray(),
                ["pointerId"] = 0, ["wheelX"] = 0, ["wheelY"] = 0,
                ["expectedFrameId"] = snap["frameId"]!.GetValue<ulong>(),
                ["expectedElementId"] = target.Element["elementId"]!.GetValue<string>(),
            };
            var down = Invoke("ui.pointer", args);
            Require(down["accepted"]!.GetValue<bool>(), $"Pointer down failed: {down}");
            args["event"] = "up";
            args["buttons"] = new JsonArray();
            args["expectedFrameId"] = down["frameId"]!.GetValue<ulong>();
            var up = Invoke("ui.pointer", args);
            Require(up["accepted"]!.GetValue<bool>(), $"Pointer up failed: {up}");
            return target.Host;
        }
        void DoubleClick(string id)
        {
            var snap = Snapshot();
            var target = Elements(snap).FirstOrDefault(p => Match(p.Element, id) &&
                p.Element["visible"]!.GetValue<bool>() && p.Element["hitTarget"]!.GetValue<bool>());
            if (target.Element is null)
                throw new InvalidOperationException($"Control is not visible/clickable: {id}");
            var bounds = target.Element["bounds"]!;
            var hostId = target.Host["hostId"]!.GetValue<string>();
            var elementId = target.Element["elementId"]!.GetValue<string>();
            var x = (int)(bounds["x"]!.GetValue<double>() + bounds["width"]!.GetValue<double>() / 2);
            var y = (int)(bounds["y"]!.GetValue<double>() + bounds["height"]!.GetValue<double>() / 2);
            var frame = snap["frameId"]!.GetValue<ulong>();
            for (var click = 0; click < 2; click++)
            {
                var down = Invoke("ui.pointer", new JsonObject
                {
                    ["hostId"] = hostId, ["event"] = "down", ["button"] = "left",
                    ["x"] = x, ["y"] = y, ["buttons"] = new JsonArray(),
                    ["modifiers"] = new JsonArray(), ["pointerId"] = 0,
                    ["expectedFrameId"] = frame,
                    ["expectedElementId"] = elementId,
                });
                Require(down["accepted"]!.GetValue<bool>(), $"Double-click down failed: {down}");
                var up = Invoke("ui.pointer", new JsonObject
                {
                    ["hostId"] = hostId, ["event"] = "up", ["button"] = "left",
                    ["x"] = x, ["y"] = y, ["buttons"] = new JsonArray(),
                    ["modifiers"] = new JsonArray(), ["pointerId"] = 0,
                    ["expectedFrameId"] = down["frameId"]!.GetValue<ulong>(),
                    ["expectedElementId"] = elementId,
                });
                Require(up["accepted"]!.GetValue<bool>(), $"Double-click up failed: {up}");
                frame = up["frameId"]!.GetValue<ulong>();
            }
        }
        JsonObject MovePointer(string id)
        {
            var snap = Snapshot();
            var target = Elements(snap).FirstOrDefault(p => Match(p.Element, id) &&
                p.Element["visible"]!.GetValue<bool>() && p.Element["hitTarget"]!.GetValue<bool>());
            if (target.Element is null)
                throw new InvalidOperationException($"Control is not visible/clickable: {id}");
            var b = target.Element["bounds"]!;
            return Invoke("ui.pointer", new JsonObject
            {
                ["hostId"] = target.Host["hostId"]!.GetValue<string>(),
                ["event"] = "move", ["button"] = "none",
                ["x"] = (int)(b["x"]!.GetValue<double>() + b["width"]!.GetValue<double>() / 2),
                ["y"] = (int)(b["y"]!.GetValue<double>() + b["height"]!.GetValue<double>() / 2),
                ["buttons"] = new JsonArray(), ["modifiers"] = new JsonArray(),
                ["pointerId"] = 0, ["wheelX"] = 0, ["wheelY"] = 0,
                ["expectedFrameId"] = snap["frameId"]!.GetValue<ulong>(),
                ["expectedElementId"] = target.Element["elementId"]!.GetValue<string>(),
            });
        }
        void Search(string pane, string text)
        {
            var host = Click(pane + "-search");
            JsonObject result = new();
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var snap = Snapshot();
                result = Invoke("ui.key", new JsonObject
                {
                    ["hostId"] = host["hostId"]!.GetValue<string>(),
                    ["event"] = "text", ["text"] = text, ["modifiers"] = new JsonArray(),
                    ["expectedFrameId"] = snap["frameId"]!.GetValue<ulong>(),
                });
                if (result["diagnostics"]?.AsArray().FirstOrDefault()?["code"]?.GetValue<string>() != "UI_FRAME_STALE") break;
            }
            Require(result["accepted"]!.GetValue<bool>(), $"Search must accept text input: {result}");
        }
        List<JsonObject> Entries(string pane) => Elements(Snapshot())
            .Where(p => p.Element["visible"]!.GetValue<bool>() &&
                p.Element["actions"]!.AsArray().Any(a =>
                    a!.GetValue<string>().StartsWith($"editor.content.open:{pane}:", StringComparison.Ordinal) ||
                    a!.GetValue<string>().StartsWith($"editor.content.select:{pane}:", StringComparison.Ordinal)))
            .Select(p => p.Element).ToList();

        // Existing user layout is loaded read-only; Reset affects this isolated session only.
        Invoke("editor.action", new() { ["action"] = "editor.dock-reset-layout" });
        SaveFrames("initial-tile");
        var tiles = Entries("content");
        Require(tiles.Count >= 2, "Root must display at least two entries");
        Require(tiles[0]["bounds"]!["y"]!.GetValue<double>() ==
                tiles[1]["bounds"]!["y"]!.GetValue<double>() &&
            tiles[0]["bounds"]!["x"]!.GetValue<double>() !=
                tiles[1]["bounds"]!["x"]!.GetValue<double>(),
            "Tile view must arrange entries horizontally before wrapping");
        Click("editor.content.open:content:engine|Assets");
        var hover = MovePointer("content-up");
        Require(hover["accepted"]!.GetValue<bool>(), "Parent folder hover must enter the native pointer path");
        SaveFrames("parent-hover");
        Require(hover["targetElementId"]?.GetValue<string>() ==
                "host/main/id/content-up",
            "Parent folder hover must resolve the button through native hit testing");
        Click("content-up");
        Click("content-view-list");
        var rows = Entries("content");
        Require(rows[0]["bounds"]!["x"]!.GetValue<double>() ==
                rows[1]["bounds"]!["x"]!.GetValue<double>() &&
            rows[0]["bounds"]!["y"]!.GetValue<double>() <
                rows[1]["bounds"]!["y"]!.GetValue<double>(),
            "List view must display separate full-width rows");
        SaveFrames("list");
        var scrollBefore = Snapshot();
        var firstRow = Elements(scrollBefore).First(p => Match(p.Element,
            "editor.content.open:content:engine|Assets"));
        var firstRowY = firstRow.Element["bounds"]!["y"]!.GetValue<double>();
        var headerY = Elements(scrollBefore).First(p => Match(p.Element, "content-search"))
            .Element["bounds"]!["y"]!.GetValue<double>();
        var scrollBounds = firstRow.Element["bounds"]!;
        var scrolled = Invoke("ui.pointer", new()
        {
            ["hostId"] = "main", ["event"] = "wheel", ["button"] = "none",
            ["x"] = (int)(scrollBounds["x"]!.GetValue<double>() + 40),
            ["y"] = (int)(firstRowY + 15),
            ["buttons"] = new JsonArray(), ["modifiers"] = new JsonArray(),
            ["wheelX"] = 0, ["wheelY"] = 3, ["pointerId"] = 0,
            ["expectedFrameId"] = scrollBefore["frameId"]!.GetValue<ulong>(),
            ["expectedElementId"] = firstRow.Element["elementId"]!.GetValue<string>(),
        });
        Require(scrolled["accepted"]!.GetValue<bool>(), "The results region must accept scrolling");
        // Smooth scrolling advances on the Editor's render loop.
        Thread.Sleep(250);
        var scrollAfter = Snapshot();
        Require(Elements(scrollAfter).First(p => Match(p.Element, "content-search"))
                .Element["bounds"]!["y"]!.GetValue<double>() == headerY &&
            Elements(scrollAfter).First(p => Match(p.Element,
                "editor.content.open:content:engine|Assets"))
                .Element["bounds"]!["y"]!.GetValue<double>() < firstRowY,
            "Scrolling results must move entries while leaving search and filters fixed");
        foreach (var pane in new[] { "content-2", "content-3", "content-4" })
        {
            Click("menu-root-view");
            Click("editor.command:view." + pane);
            Require(Content()["activePaneId"]!.GetValue<string>() == pane,
                "Opening a manager must activate it");
        }
        Require(Content()["managers"]!.AsArray().Count == 4,
            "Four independent managers must be discoverable");
        Search("content", "cube");
        Click("editor.content.select:content:engine://meshes/cube");
        Search("content-2", "Main.sworld");
        Click("editor.content.select:content-2:engine://worlds/main");
        Require(State("content")["selected"]!.GetValue<string>() == "engine://meshes/cube" &&
            State("content-2")["selected"]!.GetValue<string>() == "engine://worlds/main",
            "Selecting in manager 2 must preserve manager 1 selection");
        Click("content-search");
        Require(Content()["activePaneId"]!.GetValue<string>() == "content" &&
            Invoke("editor.describe")["extensions"]!["selection"]!["id"]!.GetValue<string>() ==
                "engine://meshes/cube", "Search field interaction must restore its manager selection");
        SaveFrames("four-managers");
        Click("editor.dock-close:content");
        Require(Content()["activePaneId"]!.GetValue<string>() == "content-2",
            "Closing the active manager must fall back to the most recently used open manager");
        Click("editor.command:core.browse-current-level");
        Require(Content()["activePaneId"]!.GetValue<string>() == "content-2" &&
            State("content-2")["currentFolder"]!.GetValue<string>() == "Worlds",
            "External browse must route to the active manager");
        foreach (var pane in new[] { "content-2", "content-3", "content-4" })
            Click("editor.dock-close:" + pane);
        Click("editor.command:core.browse-current-level");
        Require(Content()["activePaneId"]!.GetValue<string>() == "content" &&
            Entries("content").Count > 0,
            "External browse with all managers closed must open manager 1 and show results");
        SaveFrames("external-browse");

        // Set up a narrow, four-pane dock arrangement, then operate each view
        // switch through the same coordinate input path used above.
        Invoke("editor.action", new() { ["action"] = "editor.dock-reset-layout" });
        foreach (var pane in new[] { "content-2", "content-3", "content-4" })
            Invoke("editor.action", new() { ["action"] = "editor.dock-toggle:" + pane });
        Invoke("editor.action", new() { ["action"] = "editor.dock-resize:main:main.center=0.42" });
        Invoke("editor.action", new() { ["action"] = "editor.dock-drop:content-2:unused:main:main.content:right:0" });
        string PaneNode(JsonObject node, string pane)
        {
            if (node["tabGroup"]?["panes"]?.AsArray().Any(p => p!["paneId"]!.GetValue<string>() == pane) == true)
                return node["nodeId"]!.GetValue<string>();
            return node["first"] is JsonObject a ? PaneNode(a, pane) is { Length: > 0 } found
                ? found : PaneNode(node["second"]!.AsObject(), pane) : string.Empty;
        }
        foreach (var pair in new[] { ("content-3", "content"), ("content-4", "content-2") })
        {
            var node = PaneNode(Invoke("editor.describe")["layout"]!["main"]!["root"]!.AsObject(), pair.Item2);
            Invoke("editor.action", new() { ["action"] = $"editor.dock-drop:{pair.Item1}:unused:main:{node}:right:0" });
        }
        // Equalize the splits so each pane is approximately 270 pixels wide.
        var dock = Invoke("editor.describe")["layout"]!["main"]!["root"]!.AsObject();
        var ratios = new List<string>();
        void CollectContentSplits(JsonObject node)
        {
            if (node["first"] is not JsonObject a) return;
            if (node["nodeId"]!.GetValue<string>() == "main.content" ||
                node["nodeId"]!.GetValue<string>().Contains("content", StringComparison.Ordinal))
                ratios.Add(node["nodeId"]!.GetValue<string>() + "=0.5");
            CollectContentSplits(a);
            CollectContentSplits(node["second"]!.AsObject());
        }
        CollectContentSplits(dock);
        Invoke("editor.action", new() { ["action"] = "editor.dock-resize:main:" + string.Join(',', ratios) });
        foreach (var pane in new[] { "content", "content-2", "content-3", "content-4" })
        {
            Click(pane + "-view-list");
            Require(Entries(pane).Count >= 2, "Narrow List view must leave room for visible entries");
            Click(pane + "-view-tile");
            Require(Entries(pane).Count >= 2, "Narrow Tile view must leave room for visible entries");
        }
        Click("content-2-view-list");
        Click("content-4-view-list");
        SaveFrames("four-docked");

        // Double-click through the native pointer path. Closing the Timeline
        // tab first forces the shared asset opener to create its floating host,
        // which is the lifecycle used by a hidden animation editor pane.
        Invoke("editor.action", new() { ["action"] = "editor.dock-reset-layout" });
        Invoke("editor.action", new() { ["action"] = "editor.dock-close:timeline" });
        Click("editor.content.open:content:engine|Assets");
        Click("editor.content.open:content:engine|Assets/AnimationControllers");
        const string StateMachineAction =
            "editor.content.select:content:engine://animation-state-machines/uefn-mannequin-locomotion";
        DoubleClick(StateMachineAction);
        var opened = Snapshot();
        Require(opened["hosts"]!.AsArray().Any(host =>
                host!["hostId"]!.GetValue<string>().StartsWith("float.timeline", StringComparison.Ordinal)),
            "Double-clicking an animation state machine must keep the floating Timeline host visible");
        SaveFrames("animation-state-machine-open");
        Console.WriteLine($"PASS: {assertions} Content Manager rendered UI assertions; evidence: {output}");
    }
}
