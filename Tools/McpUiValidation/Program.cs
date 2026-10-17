using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

var root = FindEngineRoot();
var configuration = args.Length > 0 ? args[0] : "Debug";
var executable = Environment.GetEnvironmentVariable("SHARPENGINE_VALIDATION_EXE");
if (string.IsNullOrWhiteSpace(executable))
    executable = Path.Combine(root, "Binaries", "Win64", configuration,
        "Editor", "Engine.exe");
executable = Path.GetFullPath(executable);
if (!File.Exists(executable))
    throw new FileNotFoundException("Build the Editor before MCP UI validation", executable);

var assertions = 0;
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

var sessionId = Guid.NewGuid().ToString("N");
var settingsProject = Environment.GetEnvironmentVariable("SHARPENGINE_VALIDATION_PROJECT");
var editorArguments = new List<string> { "--mcp-isolated", "--mcp-session-id", sessionId };
if (!string.IsNullOrWhiteSpace(settingsProject)) editorArguments.AddRange(["--project", settingsProject]);
using var editor = Start(executable, editorArguments.ToArray());
var editorErrors = editor.StandardError.ReadToEndAsync();
var editorOutput = editor.StandardOutput.ReadToEndAsync();
Process? bridge = null;
try
{
    var registry = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "SharpEngine", "McpSessions",
        sessionId + ".json");
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
    while (!File.Exists(registry) && DateTime.UtcNow < deadline)
    {
        if (editor.HasExited)
            throw new InvalidOperationException(
                $"The isolated Editor exited {editor.ExitCode}: {editorErrors.GetAwaiter().GetResult()}");
        Thread.Sleep(50);
    }
    Require(File.Exists(registry), "Isolated Editor must publish a session registration");
    Thread.Sleep(250);

    bridge = Start(executable, "--mcp-stdio");
    _ = bridge.StandardError.ReadToEndAsync();
    JsonObject Call(int id, string method, JsonObject? parameters = null)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject(),
        };
        bridge.StandardInput.WriteLine(request.ToJsonString());
        bridge.StandardInput.Flush();
        var line = bridge.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult() ??
            throw new IOException($"MCP bridge closed while handling {method}");
        return JsonNode.Parse(line)?.AsObject() ??
            throw new InvalidDataException($"MCP response was not JSON: {line}");
    }

    JsonObject Tool(int id, string name, JsonObject arguments) =>
        Call(id, "tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments,
        });

    _ = Call(1, "initialize");
    var bootstrap = Call(2, "tools/list");
    Require(bootstrap["result"]?["tools"]?.AsArray().Count == 3,
        "Sessionless bridge must expose only explicit session-selection tools");
    var sessions = Tool(3, "editor.sessions.list", new JsonObject());
    Require(sessions.ToJsonString().Contains(sessionId, StringComparison.Ordinal),
        "Session discovery must include the isolated Editor");
    var attach = Tool(4, "editor.session.attach", new JsonObject
    {
        ["sessionId"] = sessionId,
    });
    Require(attach["result"]?["structuredContent"]?["attached"]?.GetValue<bool>() == true,
        "Explicit session attachment must succeed");
    Require(JsonNode.Parse(bridge.StandardOutput.ReadLine()!)?["method"]?.GetValue<string>() ==
            "notifications/tools/list_changed",
        "Attachment must notify clients that the tool list changed");
    Require(JsonNode.Parse(bridge.StandardOutput.ReadLine()!)?["method"]?.GetValue<string>() ==
            "notifications/resources/list_changed",
        "Attachment must notify clients that the resource list changed");

    if (args.Contains("--source-only", StringComparer.Ordinal))
    {
        SourceWizardUiValidation.Run(root, Require, Tool, Call);
        Console.WriteLine($"PASS: {assertions} Source Wizard UI integration assertions");
        return;
    }
    if (args.Contains("--inspector-only", StringComparer.Ordinal))
    {
        InspectorUiValidation.Run(root, Require, Tool, Call);
        Console.WriteLine($"PASS: {assertions} Inspector UI integration assertions");
        return;
    }
    if (args.Contains("--settings-only", StringComparer.Ordinal))
    {
        SettingsUiValidation.Run(root, Require, Tool, Call);
        Console.WriteLine($"PASS: {assertions} Settings UI integration assertions");
        return;
    }
    var tools = Call(5, "tools/list")["result"]?["tools"]?.AsArray() ?? [];
    var pointerTool = tools.Select(node => node?.AsObject()).First(tool =>
        tool?["name"]?.GetValue<string>() == "ui.pointer")!;
    Require(pointerTool["outputSchema"] is JsonObject &&
            pointerTool["annotations"]?["destructiveHint"]?.GetValue<bool>() == true &&
            pointerTool["_meta"]?["sharpengine/capability"]?["effect"]?.GetValue<string>() == "external",
        "UI pointer discovery must expose output schema and its maximum side effect");
    var templates = Call(6, "resources/templates/list")["result"]?
        ["resourceTemplates"]?.AsArray() ?? [];
    Require(templates.Any(template => template?["uriTemplate"]?.GetValue<string>() ==
            "sharpengine://ui/frames/{frameId}/{hostId}.png"),
        "Frame resource template must be discoverable");

    if (args.Contains("--content-only", StringComparer.Ordinal))
    {
        ContentManagerUiValidation.Run(root, Tool, Call);
        return;
    }

    var initial = Structured(Tool(7, "ui.describe", new JsonObject()));
    JsonObject? unchanged = null;
    for (var attempt = 0; attempt < 8; attempt++)
    {
        var candidate = Structured(Tool(70 + attempt, "ui.describe", new JsonObject()));
        if (initial["stateRevision"]!.GetValue<ulong>() ==
            candidate["stateRevision"]!.GetValue<ulong>())
        {
            unchanged = candidate;
            break;
        }
        initial = candidate;
    }
    Require(unchanged is not null &&
            initial["hosts"]!.ToJsonString() == unchanged["hosts"]!.ToJsonString(),
        "Two reads of unchanged semantic UI state must serialize identically apart from frameId");
    var host = initial["hosts"]!.AsArray()[0]!.AsObject();
    Require(host["elements"]!.AsArray().Where(element =>
            element?["visible"]?.GetValue<bool>() == false ||
            element?["enabled"]?.GetValue<bool>() == false).All(element =>
            element?["hitTarget"]?.GetValue<bool>() == false),
        "Hidden or disabled elements must never be reported as hit targets");
    var splitter = host["elements"]!.AsArray().Select(node => node!.AsObject())
        .First(element => element["role"]?.GetValue<string>() == "splitter" &&
            element["visible"]!.GetValue<bool>() && element["hitTarget"]!.GetValue<bool>());
    var bounds = splitter["bounds"]!.AsObject();
    var x = (int)Math.Floor(bounds["x"]!.GetValue<double>() +
        bounds["width"]!.GetValue<double>() / 2);
    var y = (int)Math.Floor(bounds["y"]!.GetValue<double>() +
        bounds["height"]!.GetValue<double>() / 2);
    var elementId = splitter["elementId"]!.GetValue<string>();
    JsonObject PointerArguments(string inputEvent, string button, ulong frameId,
        string targetId, int pointerX, int pointerY, string[]? buttons = null) => new()
    {
        ["hostId"] = host["hostId"]!.GetValue<string>(),
        ["event"] = inputEvent,
        ["x"] = pointerX,
        ["y"] = pointerY,
        ["button"] = button,
        ["buttons"] = new JsonArray((buttons ?? []).Select(value =>
            (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["modifiers"] = new JsonArray(),
        ["pointerId"] = 0,
        ["expectedFrameId"] = frameId,
        ["expectedElementId"] = targetId,
        ["wheelX"] = 0,
        ["wheelY"] = 0,
    };

    var initialLayout = initial["layoutRevision"]!.GetValue<ulong>();
    var initialDocument = initial["documentRevision"]!.GetValue<ulong>();
    Require(initial["layoutDirty"]!.GetValue<bool>() == false,
        "Fresh isolated layout must not start dirty");
    var down = Structured(Tool(8, "ui.pointer", PointerArguments("down", "left",
        initial["frameId"]!.GetValue<ulong>(), elementId, x, y)));
    Require(down["accepted"]!.GetValue<bool>(),
        "Pointer down must traverse the visible splitter input path");
    var move = Structured(Tool(9, "ui.pointer", PointerArguments("move", "none",
        down["frameId"]!.GetValue<ulong>(), elementId, x + 20, y, ["left"])));
    Require(move["accepted"]!.GetValue<bool>() &&
            move["capture"]?["elementId"]?.GetValue<string>() == elementId,
        "Pointer move must preserve native splitter capture");
    var preview = Structured(Tool(10, "ui.describe", new JsonObject()));
    Require(preview["layoutRevision"]!.GetValue<ulong>() == initialLayout &&
            preview["layoutDirty"]!.GetValue<bool>() == false &&
            preview["hosts"]![0]?["interaction"]?["kind"]?.GetValue<string>() ==
                "splitter-resize",
        "Resize preview must remain transient and observable");
    var previewElements = preview["hosts"]![0]?["elements"]?.AsArray()
        .Select(node => node!.AsObject()).ToDictionary(element =>
            element["elementId"]!.GetValue<string>(), StringComparer.Ordinal) ??
        throw new InvalidDataException("Preview elements were unavailable");
    var activeBefore = splitter["bounds"]!.AsObject();
    var activePreview = previewElements[elementId]["bounds"]!.AsObject();
    Require(activeBefore["x"]!.GetValue<double>() !=
            activePreview["x"]!.GetValue<double>() ||
            activeBefore["y"]!.GetValue<double>() !=
            activePreview["y"]!.GetValue<double>(),
        "Active splitter boundary must move in the preview snapshot");
    var preserved = host["elements"]!.AsArray().Select(node => node!.AsObject())
        .Where(element => element["role"]?.GetValue<string>() == "splitter" &&
            element["elementId"]?.GetValue<string>() != elementId &&
            previewElements.ContainsKey(element["elementId"]!.GetValue<string>()))
        .Select(element => (Before: element["bounds"]!.AsObject(),
            After: previewElements[element["elementId"]!.GetValue<string>()]["bounds"]!.AsObject()))
        .FirstOrDefault(pair => pair.Before["width"]!.GetValue<double>() <
            pair.Before["height"]!.GetValue<double>());
    Require(preserved.Before is null || Math.Abs(
            preserved.Before["x"]!.GetValue<double>() -
            preserved.After["x"]!.GetValue<double>()) < 0.01,
        "Other same-axis splitters must retain their absolute boundary position");

    var frameId = preview["frameId"]!.GetValue<ulong>();
    var frame = Call(11, "resources/read", new JsonObject
    {
        ["uri"] = $"sharpengine://ui/frames/{frameId}/{host["hostId"]!.GetValue<string>()}.png",
    });
    var frameContent = frame["result"]?["contents"]?[0]?.AsObject() ??
        throw new InvalidDataException("Frame resource had no content");
    var png = Convert.FromBase64String(frameContent["blob"]!.GetValue<string>());
    Require(frameContent["mimeType"]!.GetValue<string>() == "image/png" &&
            png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "Pinned frame resource must contain a valid PNG signature");
    var decoded = DecodePng(png);
    Require(decoded.Width == (uint)host["bounds"]!["width"]!.GetValue<double>() &&
            decoded.Height == (uint)host["bounds"]!["height"]!.GetValue<double>(),
        "Semantic host bounds and pinned PNG dimensions must agree");
    var splitterX = (int)Math.Floor(activePreview["x"]!.GetValue<double>() +
        activePreview["width"]!.GetValue<double>() / 2);
    var splitterY = (int)Math.Floor(activePreview["y"]!.GetValue<double>() +
        activePreview["height"]!.GetValue<double>() / 2);
    var pixel = decoded.Pixel(splitterX, splitterY);
    Require(Math.Abs(pixel.R - 112) <= 3 && Math.Abs(pixel.G - 169) <= 3 &&
            Math.Abs(pixel.B - 223) <= 3,
        "Pinned PNG must visibly contain the active splitter preview color");

    var up = Structured(Tool(12, "ui.pointer", PointerArguments("up", "left",
        frameId, elementId, x + 20, y, ["left"])));
    Require(up["accepted"]!.GetValue<bool>(), "Pointer up must commit the captured resize");
    var committed = Structured(Tool(13, "ui.describe", new JsonObject()));
    Require(committed["layoutRevision"]!.GetValue<ulong>() == initialLayout + 1 &&
            committed["layoutDirty"]!.GetValue<bool>() &&
            committed["documentRevision"]!.GetValue<ulong>() == initialDocument &&
            committed["hosts"]![0]?["interaction"] is null,
        $"Pointer up must commit one layout revision without changing the scene document: layout {initialLayout}->{committed["layoutRevision"]}, document {initialDocument}->{committed["documentRevision"]}, dirty={committed["layoutDirty"]}, interaction={committed["hosts"]![0]?["interaction"]}");

    Require(committed["hosts"]![0]?["elements"]?.AsArray().Any(node =>
            node?["elementId"]?.GetValue<string>() == elementId) == true,
        "Stable splitter ID must survive layout recomputation and commit");
    var staleArguments = PointerArguments("down", "left",
        initial["frameId"]!.GetValue<ulong>(), elementId, x, y);
    staleArguments["expectedElementId"] = null;
    var stale = Structured(Tool(14, "ui.pointer", staleArguments));
    Require(stale["accepted"]!.GetValue<bool>() == false &&
            stale["diagnostics"]?[0]?["code"]?.GetValue<string>() == "UI_FRAME_STALE",
        "Coordinates from a changed semantic frame must fail without input");
    var switchAttempt = Tool(15, "editor.session.attach", new JsonObject
    {
        ["sessionId"] = sessionId,
    });
    Require(switchAttempt["error"]?["message"]?.GetValue<string>()?.Contains(
            "already attached", StringComparison.Ordinal) == true,
        "One MCP connection must not switch sessions after attachment");

    var input = committed["hosts"]![0]?["elements"]?.AsArray()
        .Select(node => node!.AsObject()).First(element =>
            element["role"]?.GetValue<string>() == "input" &&
            element["visible"]!.GetValue<bool>() && element["hitTarget"]!.GetValue<bool>()) ??
        throw new InvalidDataException("No visible input element was exposed");
    var inputBounds = input["bounds"]!.AsObject();
    var inputX = (int)Math.Floor(inputBounds["x"]!.GetValue<double>() +
        inputBounds["width"]!.GetValue<double>() / 2);
    var inputY = (int)Math.Floor(inputBounds["y"]!.GetValue<double>() +
        inputBounds["height"]!.GetValue<double>() / 2);
    var inputId = input["elementId"]!.GetValue<string>();
    var focusDown = Structured(Tool(16, "ui.pointer",
        PointerArguments("down", "left", committed["frameId"]!.GetValue<ulong>(),
            inputId, inputX, inputY)));
    var focusUp = Structured(Tool(17, "ui.pointer",
        PointerArguments("up", "left", focusDown["frameId"]!.GetValue<ulong>(),
            inputId, inputX, inputY, ["left"])));
    Require(focusDown["accepted"]!.GetValue<bool>() &&
            focusUp["accepted"]!.GetValue<bool>(),
        "Coordinate input must focus a real RmlUi input control");
    var key = Structured(Tool(18, "ui.key", new JsonObject
    {
        ["hostId"] = host["hostId"]!.GetValue<string>(),
        ["event"] = "text",
        ["text"] = "mcp",
        ["modifiers"] = new JsonArray(),
        ["expectedFrameId"] = focusUp["frameId"]!.GetValue<ulong>(),
    }));
    Require(key["accepted"]!.GetValue<bool>() &&
            key["targetElementId"]!.GetValue<string>() == inputId,
        "Text input must traverse the currently focused RmlUi path");
    var afterText = Structured(Tool(19, "ui.describe", new JsonObject()));
    var observedInput = afterText["hosts"]![0]?["elements"]?.AsArray()
        .Select(node => node!.AsObject()).First(element =>
            element["elementId"]?.GetValue<string>() == inputId) ??
        throw new InvalidDataException("Focused input disappeared from the snapshot");
    Require(observedInput["focused"]!.GetValue<bool>() &&
            observedInput["value"]!.GetValue<string>().Contains("mcp", StringComparison.Ordinal),
        "Semantic UI snapshot must expose the focused input value after text entry");

    var viewport = afterText["hosts"]![0]?["elements"]?.AsArray()
        .Select(node => node!.AsObject()).First(element =>
            element["elementId"]?.GetValue<string>() == "host/main/id/viewportCanvas" &&
            element["visible"]!.GetValue<bool>() && element["hitTarget"]!.GetValue<bool>()) ??
        throw new InvalidDataException("The visible viewport input surface was unavailable");
    var viewportBounds = viewport["bounds"]!.AsObject();
    var viewportX = (int)Math.Floor(viewportBounds["x"]!.GetValue<double>() +
        viewportBounds["width"]!.GetValue<double>() / 2);
    var viewportY = (int)Math.Floor(viewportBounds["y"]!.GetValue<double>() +
        viewportBounds["height"]!.GetValue<double>() / 2);
    var viewportDown = Structured(Tool(100, "ui.pointer",
        PointerArguments("down", "left", afterText["frameId"]!.GetValue<ulong>(),
            "host/main/id/viewportCanvas", viewportX, viewportY)));
    var viewportUp = Structured(Tool(101, "ui.pointer",
        PointerArguments("up", "left", viewportDown["frameId"]!.GetValue<ulong>(),
            "host/main/id/viewportCanvas", viewportX, viewportY, ["left"])));
    var playKeyDown = Structured(Tool(102, "ui.key", new JsonObject
    {
        ["hostId"] = host["hostId"]!.GetValue<string>(),
        ["event"] = "down",
        ["key"] = 0x74,
        ["modifiers"] = new JsonArray(),
        ["expectedFrameId"] = viewportUp["frameId"]!.GetValue<ulong>(),
    }));
    var inputResource = Call(103, "resources/read", new JsonObject
    {
        ["uri"] = "sharpengine://input",
    });
    var inputSnapshot = JsonNode.Parse(inputResource["result"]?["contents"]?[0]?
        ["text"]?.GetValue<string>() ?? throw new InvalidDataException(
            "Input resource had no JSON content"))?.AsObject() ??
        throw new InvalidDataException("Input resource was not a JSON object");
    Require(playKeyDown["accepted"]!.GetValue<bool>() &&
            inputSnapshot["recentEvents"]!.AsArray().Any(node =>
                node?["actionId"]?.GetValue<string>() ==
                    "engine://input/actions/editor.play-pause" &&
                node?["phase"]?.GetValue<string>() == "Performed"),
        "Viewport F5 must traverse RmlUi and the unified InputRouter before executing the Editor action");
    var playKeyUp = Structured(Tool(104, "ui.key", new JsonObject
    {
        ["hostId"] = host["hostId"]!.GetValue<string>(),
        ["event"] = "up",
        ["key"] = 0x74,
        ["modifiers"] = new JsonArray(),
        ["expectedFrameId"] = playKeyDown["frameId"]!.GetValue<ulong>(),
    }));
    var stopKeyDown = Structured(Tool(105, "ui.key", new JsonObject
    {
        ["hostId"] = host["hostId"]!.GetValue<string>(),
        ["event"] = "down",
        ["key"] = 0x77,
        ["modifiers"] = new JsonArray(),
        ["expectedFrameId"] = playKeyUp["frameId"]!.GetValue<ulong>(),
    }));
    _ = Structured(Tool(106, "ui.key", new JsonObject
    {
        ["hostId"] = host["hostId"]!.GetValue<string>(),
        ["event"] = "up",
        ["key"] = 0x77,
        ["modifiers"] = new JsonArray(),
        ["expectedFrameId"] = stopKeyDown["frameId"]!.GetValue<ulong>(),
    }));

    afterText = Structured(Tool(107, "ui.describe", new JsonObject()));
    var splitterAfterText = afterText["hosts"]![0]?["elements"]?.AsArray()
        .Select(node => node!.AsObject()).First(element =>
            element["elementId"]?.GetValue<string>() == elementId) ??
        throw new InvalidDataException("Splitter disappeared before cancel validation");
    var cancelBounds = splitterAfterText["bounds"]!.AsObject();
    var cancelX = (int)Math.Floor(cancelBounds["x"]!.GetValue<double>() +
        cancelBounds["width"]!.GetValue<double>() / 2);
    var cancelY = (int)Math.Floor(cancelBounds["y"]!.GetValue<double>() +
        cancelBounds["height"]!.GetValue<double>() / 2);
    var cancelDown = Structured(Tool(20, "ui.pointer",
        PointerArguments("down", "left", afterText["frameId"]!.GetValue<ulong>(),
            elementId, cancelX, cancelY)));
    var cancelMove = Structured(Tool(21, "ui.pointer",
        PointerArguments("move", "none", cancelDown["frameId"]!.GetValue<ulong>(),
            elementId, cancelX + 15, cancelY, ["left"])));
    var cancel = Structured(Tool(22, "ui.pointer",
        PointerArguments("cancel", "none", cancelMove["frameId"]!.GetValue<ulong>(),
            elementId, cancelX + 15, cancelY)));
    var afterCancel = Structured(Tool(23, "ui.describe", new JsonObject()));
    Require(cancel["accepted"]!.GetValue<bool>() &&
            afterCancel["layoutRevision"]!.GetValue<ulong>() ==
                committed["layoutRevision"]!.GetValue<ulong>() &&
            afterCancel["hosts"]![0]?["interaction"] is null,
        "Capture cancel must clear preview without committing another layout revision");
    var invalidUp = Structured(Tool(27, "ui.pointer",
        PointerArguments("up", "left", afterCancel["frameId"]!.GetValue<ulong>(),
            elementId, cancelX, cancelY, ["left"])));
    Require(invalidUp["accepted"]!.GetValue<bool>() == false &&
            invalidUp["diagnostics"]?[0]?["code"]?.GetValue<string>() ==
                "UI_POINTER_SEQUENCE_INVALID",
        "Invalid pointer sequences must fail deterministically without mutation");

    _ = Tool(24, "editor.action", new JsonObject
        { ["action"] = "editor.dock-toggle:console" });
    _ = Tool(25, "editor.action", new JsonObject
        { ["action"] = "editor.dock-toggle:console" });
    var floating = Structured(Tool(26, "ui.describe", new JsonObject()));
    Require(floating["hosts"]!.AsArray().Count == 2 &&
            floating["hosts"]!.AsArray().Select(node =>
                node?["hostId"]?.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() == 2,
        "Semantic snapshot must expose main and floating hosts with stable independent IDs");
    foreach (var floatingHost in floating["hosts"]!.AsArray().Select(node => node!.AsObject()))
    {
        var floatingFrame = Call(80 + assertions, "resources/read", new JsonObject
        {
            ["uri"] = $"sharpengine://ui/frames/{floating["frameId"]!.GetValue<ulong>()}/" +
                $"{Uri.EscapeDataString(floatingHost["hostId"]!.GetValue<string>())}.png",
        });
        Require(floatingFrame["result"]?["contents"]?[0]?["mimeType"]?.GetValue<string>() ==
                "image/png",
            "Every visible host must publish pixels in its own coordinate space");
    }

    Console.WriteLine($"PASS: {assertions} MCP UI integration assertions");
}
finally
{
    if (bridge is { HasExited: false }) bridge.Kill(entireProcessTree: true);
    if (!editor.HasExited) editor.Kill(entireProcessTree: true);
    if (args.Contains("--inspector-only", StringComparer.Ordinal) || args.Contains("--source-only", StringComparer.Ordinal))
    {
        editor.WaitForExit(5000);
        var output = Path.Combine(root, "Saved", "Validation", args.Contains("--source-only") ? "SourceWizard" : "Inspector"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "editor-stderr.txt"), editorErrors.GetAwaiter().GetResult());
        File.WriteAllText(Path.Combine(output, "editor-stdout.txt"), editorOutput.GetAwaiter().GetResult());
    }
}

static JsonObject Structured(JsonObject response) =>
    response["result"]?["structuredContent"]?.AsObject() ??
    throw new InvalidDataException($"Tool response had no structured content: {response}");

static DecodedPng DecodePng(byte[] png)
{
    var offset = 8;
    uint width = 0, height = 0;
    using var compressed = new MemoryStream();
    while (offset + 12 <= png.Length)
    {
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
        var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
        var data = png.AsSpan(offset + 8, length);
        if (type == "IHDR")
        {
            width = BinaryPrimitives.ReadUInt32BigEndian(data);
            height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        }
        else if (type == "IDAT") compressed.Write(data);
        else if (type == "IEND") break;
        offset += 12 + length;
    }
    if (width == 0 || height == 0) throw new InvalidDataException("PNG has no IHDR");
    compressed.Position = 0;
    using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
    using var raw = new MemoryStream();
    zlib.CopyTo(raw);
    var scanlines = raw.ToArray();
    var rowBytes = checked((int)width * 4);
    if (scanlines.Length != checked((rowBytes + 1) * (int)height))
        throw new InvalidDataException("PNG scanline size is invalid");
    var rgba = new byte[checked(rowBytes * (int)height)];
    for (var y = 0; y < height; y++)
    {
        var source = checked((int)y * (rowBytes + 1));
        if (scanlines[source] != 0)
            throw new InvalidDataException("Validation PNG used an unexpected filter");
        Buffer.BlockCopy(scanlines, source + 1, rgba, checked((int)y * rowBytes), rowBytes);
    }
    return new DecodedPng(width, height, rgba);
}

static Process Start(string executable, params string[] arguments)
{
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}");
    return process;
}

static string FindEngineRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
         directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "engine.json")))
            return directory.FullName;
    throw new DirectoryNotFoundException("Could not find the SharpEngine root");
}

readonly record struct DecodedPng(uint Width, uint Height, byte[] Rgba)
{
    internal (byte R, byte G, byte B, byte A) Pixel(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            throw new ArgumentOutOfRangeException($"Pixel {x},{y} is outside {Width}x{Height}");
        var offset = checked(((int)Width * y + x) * 4);
        return (Rgba[offset], Rgba[offset + 1], Rgba[offset + 2], Rgba[offset + 3]);
    }
}
