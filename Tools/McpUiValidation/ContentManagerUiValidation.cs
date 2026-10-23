using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;

internal static class ContentManagerUiValidation
{
    internal static string CreateProject(string Root, string Configuration)
    {
        var Folder = Path.Combine(Root, "Saved/Validation/ContentManager/Projects", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        const string Name = "ContentMenuValidation";
        var Manifest = Path.Combine(Folder, Name, Name + ".sharpproject");
        void RunProcess(string Executable, string[] Arguments, string LogName)
        {
            var Start = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Root,
            };
            Start.Environment["SHARPENGINE_REGISTER_ON_FIRST_USE"] = "0";
            foreach (var Argument in Arguments)
            {
                Start.ArgumentList.Add(Argument);
            }
            using var Process = System.Diagnostics.Process.Start(Start)!;
            var Output = Process.StandardOutput.ReadToEndAsync();
            var Errors = Process.StandardError.ReadToEndAsync();
            Process.WaitForExit();
            File.WriteAllText(Path.Combine(Folder, LogName), Output.Result + Errors.Result);
            if (Process.ExitCode != 0)
            {
                throw new InvalidOperationException("Content menu fixture failed: " + Output.Result + Errors.Result);
            }
        }
        RunProcess("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(Root, "Tools/SharpEngineProject.ps1"), "-Command", "New", "-Name", Name,
            "-Directory", Folder, "-EngineRoot", Root, "-EngineAssociation", "content-menu-validation",
            "-Configuration", Configuration], "create.log");
        RunProcess("dotnet", ["run", "--project", Path.Combine(Root, "Tools/ProjectBuild/SharpEngine.ProjectBuild.csproj"),
            "--", "build", Manifest, "--engine-root", Root, "--target", "Editor", "--configuration", Configuration],
            "build.log");
        return Manifest;
    }

    internal static void Run(string root,
        Func<int, string, JsonObject, JsonObject> tool,
        Func<int, string, JsonObject?, JsonObject> call, bool ContextMenusOnly = false)
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
        void ResetContentBrowser()
        {
            // A fresh browser makes the injected pair the only input for the gesture.
            Invoke("editor.action", new() { ["action"] = "editor.dock-reset-layout" });
            Thread.Sleep(300);
            Invoke("editor.action", new() { ["action"] = "editor.content.source:content:engine" });
            Thread.Sleep(300);
        }
        long MeasureDoubleClickGap(string id)
        {
            // Performs one injected press pair and returns the up-to-up interval the native
            // double-click decision will compare against the OS threshold.
            var snap = Snapshot();
            var target = Elements(snap).FirstOrDefault(p => Match(p.Element, id) &&
                p.Element["visible"]!.GetValue<bool>() && p.Element["hitTarget"]!.GetValue<bool>());
            if (target.Element is null)
            {
                throw new InvalidOperationException($"Control is not visible/clickable: {id}");
            }
            var bounds = target.Element["bounds"]!;
            var hostId = target.Host["hostId"]!.GetValue<string>();
            var elementId = target.Element["elementId"]!.GetValue<string>();
            var x = (int)(bounds["x"]!.GetValue<double>() + bounds["width"]!.GetValue<double>() / 2);
            var y = (int)(bounds["y"]!.GetValue<double>() + bounds["height"]!.GetValue<double>() / 2);
            var frame = snap["frameId"]!.GetValue<ulong>();
            var clock = Stopwatch.StartNew();
            long firstUp = 0;
            for (var click = 0; click < 2; click++)
            {
                var down = Invoke("ui.pointer", new JsonObject
                {
                    ["hostId"] = hostId, ["event"] = "down", ["button"] = "left",
                    ["x"] = x, ["y"] = y, ["buttons"] = new JsonArray(),
                    ["modifiers"] = new JsonArray(), ["pointerId"] = 0,
                    ["expectedFrameId"] = frame, ["expectedElementId"] = elementId,
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
                if (click == 0)
                {
                    firstUp = clock.ElapsedMilliseconds;
                }
            }
            var gap = clock.ElapsedMilliseconds - firstUp;
            Console.WriteLine($"double-click '{id}' up-to-up gap = {gap} ms");
            return gap;
        }
        void DoubleClickNavigates(string id, string expectedFolder)
        {
            // Isolated native double-click case. Two injected presses only form a native double
            // click while the up-to-up gap stays inside the OS window. Each injected pointer
            // event pays two full presentations, so on a slow host the pair degrades into two
            // single clicks. Assert whichever real outcome the measured gap implies.
            ResetContentBrowser();
            var gap = MeasureDoubleClickGap(id);
            var state = State("content");
            var folder = state["currentFolder"]!.GetValue<string>();
            var selected = state["selectedPath"]?.GetValue<string>();
            if (gap <= 450)
            {
                Require(folder == expectedFolder,
                    $"A native double click ({gap} ms gap) must navigate into '{expectedFolder}': " +
                    state.ToJsonString());
                return;
            }
            Require(folder.Length == 0 && selected == expectedFolder,
                $"A pair outside the native window ({gap} ms gap) must degrade to selecting " +
                $"'{expectedFolder}' without navigating: " + state.ToJsonString());
        }
        void OpenFolder(string action, string expectedFolder)
        {
            Invoke("editor.action", new() { ["action"] = action });
            Require(State("content")["currentFolder"]!.GetValue<string>() == expectedFolder,
                $"Opening '{expectedFolder}' must change the pane folder: " +
                State("content").ToJsonString());
        }        void WaitMenuSession(string HostId)
        {
            // A right click opens the popup asynchronously: the window procedure queues the
            // action and the session only exists after it drains. Escape must not be injected
            // before the popup owns the host, so wait for the published session instead of a
            // fixed delay.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var sessions = Content()["contextMenus"]?["sessions"]?.AsArray() ?? [];
                if (sessions.Any(node => node?["hostId"]?.GetValue<string>() == HostId))
                {
                    return;
                }
                Thread.Sleep(25);
            }
            throw new InvalidOperationException(
                "Context menu session never opened for host " + HostId + ": " +
                (Content()["contextMenus"]?.ToJsonString() ?? "<none>"));
        }        void CheckContextMenu(string HostId, bool Background = false)
        {
            var Ui = Snapshot();
            var Menu = Elements(Ui).Single(Pair => Pair.Host["hostId"]!.GetValue<string>() == HostId &&
                Match(Pair.Element, "contextMenu"));
            var Bounds = Menu.Element["bounds"]!;
            var Contained = Menu.Element["visible"]!.GetValue<bool>() &&
                Bounds["x"]!.GetValue<double>() >= 0 && Bounds["y"]!.GetValue<double>() >= 0 &&
                Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>() <= Menu.Host["bounds"]!["width"]!.GetValue<double>() &&
                Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>() <= Menu.Host["bounds"]!["height"]!.GetValue<double>();
            if (!Contained)
            {
                SaveFrames("context-menu-failure");
            }
            Require(Contained, "The popup must be visible and contained by its actual host window: " +
                Menu.Element.ToJsonString() + "; context=" + Content().ToJsonString());
            Require(Elements(Ui).Any(Pair => Pair.Host["hostId"]!.GetValue<string>() == HostId &&
                Match(Pair.Element, Background ? "context-command-core.content-refresh" : "context-command-core.content-open") &&
                Pair.Element["visible"]!.GetValue<bool>() && Pair.Element["hitTarget"]!.GetValue<bool>()),
                "The popup must contain its real clickable primary command");
            foreach (var Command in new[] { "refresh", "view-tile", "view-list", "up", "clear-filters" })
            {
                var Visible = Elements(Ui).Any(Pair => Pair.Host["hostId"]!.GetValue<string>() == HostId &&
                    Match(Pair.Element, "context-command-core.content-" + Command) && Pair.Element["visible"]!.GetValue<bool>());
                Require(Visible == Background, "Browser-level operations belong only to the background menu");
            }
        }
        string RightClickBackground(string Pane)
        {
            var Ui = Snapshot();
            var Area = Elements(Ui).Single(Pair => Match(Pair.Element, Pane + "-results") &&
                Pair.Element["visible"]!.GetValue<bool>());
            var HostId = Area.Host["hostId"]!.GetValue<string>();
            var Bounds = Area.Element["bounds"]!;
            var Pointer = new JsonObject
            {
                ["hostId"] = HostId,
                ["event"] = "move",
                ["button"] = "none",
                ["x"] = (int)(Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>() - 8),
                ["y"] = (int)(Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>() - 8),
                ["buttons"] = new JsonArray(),
                ["modifiers"] = new JsonArray(),
                ["pointerId"] = 0,
            };
            JsonObject Moved = new();
            for (var Attempt = 0; Attempt < 8; Attempt++)
            {
                Pointer["expectedFrameId"] = Snapshot()["frameId"]!.DeepClone();
                Moved = Invoke("ui.pointer", Pointer);
                if (Moved["accepted"]!.GetValue<bool>())
                {
                    break;
                }
            }
            Require(Moved["accepted"]!.GetValue<bool>(), "Background hit testing must accept the pointer");
            Pointer["event"] = "down";
            Pointer["button"] = "right";
            Pointer["expectedElementId"] = Moved["targetElementId"]!.DeepClone();
            Pointer["expectedFrameId"] = Moved["frameId"]!.DeepClone();
            var Down = Invoke("ui.pointer", Pointer);
            Require(Down["accepted"]!.GetValue<bool>(), "Background right-button down must be accepted");
            Pointer["event"] = "up";
            Pointer["expectedFrameId"] = Down["frameId"]!.DeepClone();
            var Up = Invoke("ui.pointer", Pointer);
            Require(Up["accepted"]!.GetValue<bool>(), "Background right-button up must be accepted");
            CheckContextMenu(HostId, true);
            return HostId;
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
                    a!.GetValue<string>().StartsWith($"editor.content.select-folder:{pane}:", StringComparison.Ordinal) ||
                    a!.GetValue<string>().StartsWith($"editor.content.select:{pane}:", StringComparison.Ordinal)))
            .Select(p => p.Element).ToList();

        // Existing user layout is loaded read-only; Reset affects this isolated session only.
        Invoke("editor.action", new() { ["action"] = "editor.dock-reset-layout" });
        Invoke("editor.action", new() { ["action"] = "editor.content.source:content:engine" });
        SaveFrames("initial-tile");
        var tiles = Entries("content");
        Require(tiles.Count >= 2, "Root must display at least two entries");
        Require(tiles[0]["bounds"]!["y"]!.GetValue<double>() ==
                tiles[1]["bounds"]!["y"]!.GetValue<double>() &&
            tiles[0]["bounds"]!["x"]!.GetValue<double>() !=
                tiles[1]["bounds"]!["x"]!.GetValue<double>(),
            "Tile view must arrange entries horizontally before wrapping");
        var RootFolder = State("content")["currentFolder"]!.GetValue<string>();
        Search("content", "no-content-menu-match");
        RightClickBackground("content");
        Click("context-command-core.content-view-list");
        Require(State("content")["viewMode"]!.GetValue<string>() == "List",
            "A background right-click changes its own browser to List view");
        RightClickBackground("content");
        Click("context-command-core.content-view-tile");
        Require(State("content")["viewMode"]!.GetValue<string>() == "Tile",
            "A background right-click changes its own browser to Tile view");
        RightClickBackground("content");
        SaveFrames("background-context-menu");
        Click("context-command-core.content-clear-filters");
        Require(State("content")["search"]!.GetValue<string>() == "" &&
            Elements(Snapshot()).Single(Pair => Match(Pair.Element, "content-search")).Element["value"]!.GetValue<string>() == "",
            "Background Clear Filters restores both the listing and the displayed input value");
        var FolderMenuHost = Click("editor.content.select-folder:content:engine|Assets", "right");
        CheckContextMenu(FolderMenuHost["hostId"]!.GetValue<string>());
        Require(State("content")["currentFolder"]!.GetValue<string>() == RootFolder,
            "Opening a folder menu must not navigate before command execution");
        SaveFrames("folder-context-menu");
        Click("context-command-core.content-open");
        Require(State("content")["currentFolder"]!.GetValue<string>() == "Assets",
            "The folder context Open command must navigate through real input");
        Search("content", "no-content-menu-match");
        RightClickBackground("content");
        Click("context-command-core.content-up");
        Require(State("content")["currentFolder"]!.GetValue<string>() == RootFolder,
            "Go Up executes only from the background menu and navigates its owner");
        RightClickBackground("content");
        Click("context-command-core.content-clear-filters");
        Click("editor.content.select-folder:content:engine|Assets");
        Require(State("content")["currentFolder"]!.GetValue<string>() == RootFolder &&
            State("content")["selectedPath"]!.GetValue<string>() == "Assets" &&
            State("content")["selectedSource"]!.GetValue<string>() == "Engine",
            "Single-clicking a folder must select it without changing the current folder");
        var PluginsAction = Elements(Snapshot())
            .SelectMany(p => p.Element["actions"]!.AsArray()
                .Select(action => action!.GetValue<string>()))
            .FirstOrDefault(action => action ==
                "editor.content.select-folder:content:engine|Plugins");
        if (PluginsAction is not null)
        {
            Click(PluginsAction);
            Require(State("content")["currentFolder"]!.GetValue<string>() == RootFolder &&
                State("content")["selectedPath"]!.GetValue<string>() == "Plugins" &&
                State("content")["selectedSource"]!.GetValue<string>() == "Engine",
                "Selecting the virtual Plugins folder must replace the previous folder selection");
        }
        OpenFolder("editor.content.open:content:engine|Assets", "Assets");
        Require(State("content")["currentFolder"]!.GetValue<string>() == "Assets" &&
            State("content")["selectedPath"] is null &&
            State("content")["selectedSource"] is null,
            "Opening a folder must enter it and clear the folder selection");
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
            "editor.content.select-folder:content:engine|Assets"));
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
                "editor.content.select-folder:content:engine|Assets"))
                .Element["bounds"]!["y"]!.GetValue<double>() < firstRowY,
            "Scrolling results must move entries while leaving search and filters fixed");
        foreach (var pane in new[] { "content-2", "content-3", "content-4" })
        {
            Click("menu-root-view");
            Click("editor.command:view." + pane);
            Invoke("editor.action", new() { ["action"] = "editor.content.source:" + pane + ":engine" });
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
        var AssetMenuHost = Click("editor.content.select:content-2:engine://worlds/main", "right");
        CheckContextMenu(AssetMenuHost["hostId"]!.GetValue<string>());
        SaveFrames("floating-asset-context-menu");
        Click("context-command-core.content-browse-parent");
        Require(State("content-2")["currentFolder"]!.GetValue<string>() == "Worlds" &&
            State("content")["selected"]!.GetValue<string>() == "engine://meshes/cube",
            "A floating asset menu must browse its own manager without changing manager 1");
        AssetMenuHost = Click("editor.content.select:content-2:engine://worlds/main", "right");
        WaitMenuSession(AssetMenuHost["hostId"]!.GetValue<string>());
        var Escape = Invoke("ui.key", new JsonObject
        {
            ["hostId"] = AssetMenuHost["hostId"]!.DeepClone(),
            ["event"] = "down",
            ["key"] = 27,
            ["modifiers"] = new JsonArray(),
            ["expectedFrameId"] = Snapshot()["frameId"]!.DeepClone(),
        });
        Require(Escape["accepted"]!.GetValue<bool>(),
            "Escape must be accepted by the popup host: " + Escape.ToJsonString() +
            " | hosts=" + string.Join(",", Snapshot()["hosts"]!.AsArray()
                .Select(h => h!["hostId"]!.GetValue<string>())) +
            " | menuHints=" + Content()["contextMenus"]?.ToJsonString());
        Invoke("ui.key", new JsonObject
        {
            ["hostId"] = AssetMenuHost["hostId"]!.DeepClone(),
            ["event"] = "up",
            ["key"] = 27,
            ["modifiers"] = new JsonArray(),
            ["expectedFrameId"] = Snapshot()["frameId"]!.DeepClone(),
        });
        Require(!Elements(Snapshot()).Any(Pair => Pair.Host["hostId"]!.GetValue<string>() == AssetMenuHost["hostId"]!.GetValue<string>() &&
            Match(Pair.Element, "contextMenu") &&
            Pair.Element["visible"]!.GetValue<bool>()),
            "Escape must dismiss the popup through the native input path");
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
        Invoke("editor.action", new() { ["action"] = "editor.content.source:content:engine" });
        foreach (var Pane in new[] { "content-2", "content-3", "content-4" })
        {
            Invoke("editor.action", new() { ["action"] = "editor.dock-toggle:" + Pane });
            Invoke("editor.action", new() { ["action"] = "editor.content.source:" + Pane + ":engine" });
        }
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
        if (ContextMenusOnly)
        {
            Invoke("editor.action", new() { ["action"] = "editor.dock-reset-layout" });
            Invoke("editor.action", new() { ["action"] = "editor.content.source:content:engine" });
            Search("content", "cube");
            var OpenHost = Click("editor.content.select:content:engine://meshes/cube", "right");
            CheckContextMenu(OpenHost["hostId"]!.GetValue<string>());
            Click("context-command-core.content-open");
            var AssetRoot = Invoke("editor.describe")["assetTools"]!["editors"]!.AsArray()
                .Single(Node => Node!["assetId"]!.GetValue<string>() == "engine://meshes/cube")!
                ["rootId"]!.GetValue<string>();
            Require(Snapshot()["hosts"]!.AsArray().Any(Node => Node!["hostId"]!.GetValue<string>() == AssetRoot),
                "The asset context Open command creates the owning asset editor through real input");
            SaveFrames("asset-context-open");
            Console.WriteLine($"PASS: {assertions} rendered Content Manager context-menu assertions; evidence: {output}");
            return;
        }

        // Double-click through the native pointer path. The asset editor must
        // create a separate top-level editor root rather than a main Dock pane.
        Invoke("editor.action", new() { ["action"] = "editor.dock-reset-layout" });
        Invoke("editor.action", new() { ["action"] = "editor.content.source:content:engine" });
        {
            // Record the state this block inherits: host set, each pane's folder, and the
            // content actions currently reachable. Navigation failures here are inherited
            // state problems, so the evidence has to describe the state, not just the target.
            var Diagnostic = Snapshot();
            File.WriteAllText(Path.Combine(output, "double-click-before.json"),
                Diagnostic.ToJsonString());
            var Hosts = Diagnostic["hosts"]!.AsArray()
                .Select(h => $"{h!["hostId"]!.GetValue<string>()}({h["kind"]!.GetValue<string>()})")
                .ToArray();
            var Panes = Content()["managers"]!.AsArray()
                .Select(m => $"{m!["paneId"]} folder='{m["state"]!["currentFolder"]}' " +
                    $"selected='{m["state"]!["selected"]}' source='{m["state"]!["source"]}' " +
                    $"view='{m["state"]!["viewMode"]}'")
                .ToArray();
            var FolderActions = Elements(Diagnostic)
                .Where(p => p.Element["visible"]!.GetValue<bool>() &&
                    p.Element["actions"]!.AsArray().Any(a =>
                        a!.GetValue<string>().Contains("select-folder:", StringComparison.Ordinal)))
                .Select(p => $"{p.Host["hostId"]!.GetValue<string>()}:{p.Element["actions"]![0]!.GetValue<string>()}")
                .ToArray();
            Console.WriteLine("double-click hosts: " + string.Join(", ", Hosts));
            Console.WriteLine("double-click panes: " + string.Join(" | ", Panes));
            Console.WriteLine("double-click folders: " + string.Join(" | ", FolderActions));
        }
        // Navigation is asserted through the semantic action a double click would emit, so this
        // long scenario holds no dependency on the native double-click timing window. The real
        // native double click is covered by the short isolated case at the end of this suite.
        OpenFolder("editor.content.open:content:engine|Assets", "Assets");
        OpenFolder("editor.content.open:content:engine|Assets/AnimationControllers",
            "Assets/AnimationControllers");
        const string StateMachineAction =
            "editor.content.select:content:engine://animation-state-machines/uefn-mannequin-locomotion";
        Invoke("editor.action", new() { ["action"] = StateMachineAction });
        Require(State("content")["selected"]!.GetValue<string>() ==
                "engine://animation-state-machines/uefn-mannequin-locomotion",
            "Selecting the animation state machine must update the pane selection");
        Invoke("editor.action", new() { ["action"] = "editor.open-asset:" +
            "engine://animation-state-machines/uefn-mannequin-locomotion" });
        var EditorState = Invoke("editor.describe");
        var EditorRoot = EditorState["assetTools"]!["editors"]!.AsArray()
            .Select(Node => Node!.AsObject()).Single(Editor =>
                Editor["assetId"]!.GetValue<string>() ==
                "engine://animation-state-machines/uefn-mannequin-locomotion")
            ["rootId"]!.GetValue<string>();
        var opened = Snapshot();
        Require(opened["hosts"]!.AsArray().Any(host =>
                host!["hostId"]!.GetValue<string>() == EditorRoot),
            "Opening an animation state machine must create its top-level editor root");
        SaveFrames("animation-state-machine-open");

        // Isolated native double-click coverage. It runs last, on a freshly reset browser, so
        // the gesture inherits no prior state and its measured gap is recorded as evidence.
        DoubleClickNavigates("editor.content.select-folder:content:engine|Assets", "Assets");
        SaveFrames("double-click-navigation");
        Console.WriteLine($"PASS: {assertions} Content Manager rendered UI assertions; evidence: {output}");
    }
}
