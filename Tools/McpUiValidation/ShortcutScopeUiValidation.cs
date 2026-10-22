using System.Security.Cryptography;
using System.Text.Json.Nodes;

/// <summary>Proves that editor shortcuts are routed by focused document scope: the
/// focused asset editor consumes Ctrl+S, and the same gesture only reaches the World
/// when the key event comes from another host.</summary>
internal static class ShortcutScopeUiValidation
{
    private const string AssetId = "project://input/actions/gameplay.move";

    internal static void Run(string Root, Action<bool, string> Require,
        Func<int, string, JsonObject, JsonObject> Tool)
    {
        var Sequence = 24000;
        JsonObject Invoke(string Name, JsonObject? Arguments = null)
        {
            var Response = Tool(Sequence++, Name, Arguments ?? new JsonObject());
            return Response["result"]?["structuredContent"]?.AsObject() ??
                throw new InvalidDataException(Response.ToJsonString());
        }
        JsonObject Ui() => Invoke("ui.describe");
        JsonObject Host(string HostId) => Ui()["hosts"]!.AsArray()
            .Select(Node => Node!.AsObject())
            .Single(Node => Node["hostId"]!.GetValue<string>() == HostId);
        string? Status(JsonObject Snapshot) => Snapshot["hosts"]!.AsArray()
            .Select(Node => Node!.AsObject())
            .Single(Node => Node["hostId"]!.GetValue<string>() == "main")
            ["elements"]!.AsArray().Select(Node => Node!.AsObject())
            .FirstOrDefault(Element => Element["semanticId"]?.GetValue<string>() == "statusText")
            ?["text"]?.GetValue<string>();

        var Opened = Invoke("editor.action", new JsonObject
        {
            ["action"] = "editor.open-asset:" + AssetId,
        });
        var Editor = Opened["assetTools"]!["editors"]!.AsArray()
            .Select(Node => Node!.AsObject())
            .SingleOrDefault(Node => Node["assetId"]!.GetValue<string>() == AssetId) ??
            throw new InvalidDataException(
                "The shortcut scope check requires an editable project asset editor: " +
                Opened["assetTools"]!.ToJsonString());
        var RootId = Editor["rootId"]!.GetValue<string>();
        Thread.Sleep(250);

        var ScopeHost = Host(RootId);
        Require(ScopeHost["kind"]!.GetValue<string>() == "top-level-editor",
            "The focused asset editor must own its own top-level host");

        var FocusTarget = ScopeHost["elements"]!.AsArray().Select(Node => Node!.AsObject())
            .FirstOrDefault(Element => Element["visible"]!.GetValue<bool>() &&
                Element["hitTarget"]!.GetValue<bool>() &&
                (Element["role"]?.GetValue<string>() == "tab" ||
                 Element["role"]?.GetValue<string>() == "group")) ??
            throw new InvalidDataException("The asset editor exposed no focusable surface");
        var Bounds = FocusTarget["bounds"]!;
        var X = (int)(Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>() / 2);
        var Y = (int)(Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>() / 2);
        foreach (var Phase in new[] { "down", "up" })
        {
            Invoke("ui.pointer", new JsonObject
            {
                ["hostId"] = RootId,
                ["event"] = Phase,
                ["button"] = "left",
                ["x"] = X,
                ["y"] = Y,
                ["buttons"] = Phase == "up" ? new JsonArray("left") : new JsonArray(),
                ["modifiers"] = new JsonArray(),
                ["pointerId"] = 0,
                ["expectedFrameId"] = Ui()["frameId"]!.DeepClone(),
                ["wheelX"] = 0,
                ["wheelY"] = 0,
            });
        }

        bool Key(string HostId, string Event, uint KeyCode, JsonArray Modifiers)
        {
            for (var Attempt = 0; Attempt < 10; Attempt++)
            {
                var Result = Invoke("ui.key", new JsonObject
                {
                    ["hostId"] = HostId,
                    ["event"] = Event,
                    ["key"] = KeyCode,
                    ["modifiers"] = Modifiers.DeepClone(),
                    ["expectedFrameId"] = Ui()["frameId"]!.DeepClone(),
                });
                var Accepted = Result["result"]?["structuredContent"]?["accepted"]?.GetValue<bool>() ??
                    Result["accepted"]?.GetValue<bool>() ?? false;
                if (Accepted) return true;
                if (Result["diagnostics"]?[0]?["code"]?.GetValue<string>() != "UI_FRAME_STALE") return false;
            }
            return false;
        }

        var Evidence = Path.Combine(Root, "Saved/Validation/ShortcutScope");
        Directory.CreateDirectory(Evidence);
        // Hash the current project's World so the check proves the actual save target.
        var Project = Environment.GetEnvironmentVariable("SHARPENGINE_VALIDATION_PROJECT") ??
            throw new InvalidOperationException(
                "SHARPENGINE_VALIDATION_PROJECT must name a project with an editable World");
        var Startup = JsonNode.Parse(File.ReadAllText(Project))!["world"]!["startup"]!
            .GetValue<string>();
        var World = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Project))!, Startup);
        if (!File.Exists(World))
        {
            throw new InvalidOperationException(
                "The shortcut scope check requires a saved World at " + World);
        }
        string WorldHash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(World)));
        var WorldBefore = WorldHash();
        var ProjectRoot = Path.GetDirectoryName(Path.GetFullPath(Project))!;
        var RelativeAsset = Invoke("editor.describe")["assets"]!.AsArray()
            .Select(Node => Node!.AsObject())
            .First(Node => Node["id"]!.GetValue<string>() == AssetId)["path"]!.GetValue<string>();
        var AssetPath = Path.Combine(ProjectRoot, RelativeAsset);
        string AssetHash() => File.Exists(AssetPath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(AssetPath))) : "missing";
        var AssetBefore = AssetHash();

        // Make the document dirty so a consumed Ctrl+S must produce a real asset save.
        _ = Invoke("editor.action", new JsonObject
        {
            ["action"] = $"editor.host-action:{RootId}:editor.input-toggle-continuous",
        });
        Thread.Sleep(200);

        // 1. Ctrl+S inside the focused asset editor must save that document, not the World.
        Require(Key(RootId, "down", 0x53, new JsonArray("control")),
            "Ctrl+S must reach the focused asset editor through the real RmlUi path");
        _ = Key(RootId, "up", 0x53, new JsonArray("control"));
        Thread.Sleep(600);
        var AfterScope = Ui();
        File.WriteAllText(Path.Combine(Evidence, "focused-asset-editor.json"),
            AfterScope.ToJsonString());
        Require(!(Status(AfterScope) ?? string.Empty).Contains(".sworld", StringComparison.Ordinal),
            "The focused asset editor must consume Ctrl+S instead of saving the World: " +
            Status(AfterScope));
        Require(WorldHash() == WorldBefore,
            "A consumed Ctrl+S must leave the World source unchanged");
        Require(AssetHash() != AssetBefore,
            "A consumed Ctrl+S must save the focused document: " + Status(AfterScope));

        // 2. Ctrl+S from the main editor host must bubble to the global World save.
        var Main = Ui()["hosts"]!.AsArray().Select(Node => Node!.AsObject())
            .Single(Node => Node["hostId"]!.GetValue<string>() == "main");
        var Viewport = Main["elements"]!.AsArray().Select(Node => Node!.AsObject())
            .First(Element => Element["elementId"]!.GetValue<string>() ==
                "host/main/id/viewportCanvas");
        var ViewportBounds = Viewport["bounds"]!;
        var ViewportX = (int)(ViewportBounds["x"]!.GetValue<double>() +
            ViewportBounds["width"]!.GetValue<double>() / 2);
        var ViewportY = (int)(ViewportBounds["y"]!.GetValue<double>() +
            ViewportBounds["height"]!.GetValue<double>() / 2);
        foreach (var Phase in new[] { "down", "up" })
        {
            Invoke("ui.pointer", new JsonObject
            {
                ["hostId"] = "main",
                ["event"] = Phase,
                ["button"] = "left",
                ["x"] = ViewportX,
                ["y"] = ViewportY,
                ["buttons"] = Phase == "up" ? new JsonArray("left") : new JsonArray(),
                ["modifiers"] = new JsonArray(),
                ["pointerId"] = 0,
                ["expectedFrameId"] = Ui()["frameId"]!.DeepClone(),
                ["expectedElementId"] = "host/main/id/viewportCanvas",
                ["wheelX"] = 0,
                ["wheelY"] = 0,
            });
        }
        Require(Key("main", "down", 0x53, new JsonArray("control")),
            "Ctrl+S must reach the main editor host");
        _ = Key("main", "up", 0x53, new JsonArray("control"));
        Thread.Sleep(700);
        var AfterMain = Ui();
        File.WriteAllText(Path.Combine(Evidence, "main-host.json"), AfterMain.ToJsonString());
        Require((Status(AfterMain) ?? string.Empty).Contains(".sworld", StringComparison.Ordinal),
            "Ctrl+S outside a document scope must bubble to the World save: " + Status(AfterMain));

        var Shortcuts = Invoke("editor.describe")["shortcuts"]!;
        Require(Shortcuts["bindings"]!.AsArray().Any(Node =>
                Node!["name"]!.GetValue<string>() == "save" &&
                Node["gesture"]!.GetValue<string>() == "Ctrl+S"),
            "The engine shortcut map must publish the Ctrl+S save gesture");
        Require(Shortcuts["routing"]!["scopes"]!.AsArray().Any(Node =>
                Node!["hostId"]!.GetValue<string>() == RootId &&
                Node["actions"]!.AsArray().Any(Action => Action!.GetValue<string>()
                    .EndsWith("/editor.save", StringComparison.Ordinal))),
            "The open asset editor must publish its own Save shortcut scope");
        Console.WriteLine("Shortcut scope evidence: " + Evidence);
    }
}
