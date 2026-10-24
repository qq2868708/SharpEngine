using System.Diagnostics;
using System.Text.Json.Nodes;

/// <summary>End-to-end validation of the formal RML UI editor through the real RmlUi
/// hit-test, pointer-capture and event path. Markup assertions alone are not evidence
/// that the design canvas is selectable or draggable.</summary>
internal static class UiEditorUiValidation
{
    private static string? ManifestPath;
    private const string ScreenId = "project://ui/editor-screen";
    private const string StyleId = "project://ui/editor-screen-style";

    internal static string CreateProject(string Root)
    {
        var Folder = Path.Combine(Root, "Saved/Validation/UiEditor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Folder, "Content/UI"));
        Directory.CreateDirectory(Path.Combine(Folder, "Content/Worlds"));
        Directory.CreateDirectory(Path.Combine(Folder, "Content/Assets"));
        File.Copy(Path.Combine(Root, "Content/Worlds/Main.sworld"),
            Path.Combine(Folder, "Content/Worlds/Main.sworld"), true);
        File.WriteAllText(Path.Combine(Folder, "Content/UI/Screen.sui"), new JsonObject
        {
            ["$schema"] = "https://sharpengine.dev/schemas/ui-document-v1.json",
            ["schemaVersion"] = 1,
            ["name"] = "Editor Screen",
            ["rml"] = """
                <rml>
                <head>
                <link type="text/rcss" href="Screen.rcss" />
                <style>.screen { gap: 21px; }</style>
                </head>
                <body><div id="screen" class="screen"><span id="headline" class="headline">Formal UI editor</span><button id="continue" class="primary">Continue</button></div></body>
                </rml>
                """,
        }.ToJsonString());
        File.WriteAllText(Path.Combine(Folder, "Content/UI/Screen.rcss"), """
            .screen { display: flex; flex-direction: column; padding: 16px; gap: 10px; }
            .headline { color: #ffffff; font-size: 24px; }
            .primary { color: #101418; background-color: #6cc7af; }
            """);
        File.WriteAllText(Path.Combine(Folder, "Content/Assets/assets.json"), new JsonObject
        {
            ["schemaVersion"] = 2,
            ["assets"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "ui/editor-screen",
                    ["name"] = "Editor Screen",
                    ["type"] = "UiDocument",
                    ["path"] = "UI/Screen.sui",
                    ["importer"] = "SharpEngine.UiDocumentJson",
                },
                new JsonObject
                {
                    ["id"] = "ui/editor-screen-style",
                    ["name"] = "Editor Screen Style",
                    ["type"] = "StyleSheet",
                    ["path"] = "UI/Screen.rcss",
                },
            },
        }.ToJsonString());
        var Manifest = Path.Combine(Folder, "UiEditor.sharpproject");
        ManifestPath = Manifest;
        File.WriteAllText(Manifest, new JsonObject
        {
            ["schemaVersion"] = 3,
            ["projectId"] = "ui-editor-validation",
            ["displayName"] = "UI editor validation",
            ["engine"] = new JsonObject
            {
                ["id"] = "SharpEngine",
                ["version"] = "0.2.0-dev",
                ["association"] = "ui-editor-validation",
                ["manifest"] = "engine.json",
            },
            ["modules"] = new JsonArray(),
            ["assets"] = new JsonObject
            {
                ["manifest"] = "Content/Assets/assets.json",
            },
            ["world"] = new JsonObject
            {
                ["startup"] = "Content/Worlds/Main.sworld",
            },
        }.ToJsonString());
        return Manifest;
    }

    internal static void Run(string Root, Action<bool, string> Check,
        Func<int, string, JsonObject, JsonObject> Tool,
        Func<int, string, JsonObject?, JsonObject> Call)
    {
        var Output = Path.Combine(Root, "Saved", "Validation", "UiEditor");
        Directory.CreateDirectory(Output);
        var Sequence = 71000;
        var PathValue = ManifestPath ??
            throw new InvalidOperationException("UI editor validation requires its fixture project");
        var PackagePath = Path.Combine(Path.GetDirectoryName(PathValue)!, "Content/UI/Screen.sui");
        JsonObject Invoke(string Name, JsonObject? Arguments = null)
        {
            var Result = Tool(Sequence++, Name, Arguments ?? new JsonObject());
            return Result["result"]?["structuredContent"]?.AsObject() ??
                throw new InvalidOperationException(Result.ToJsonString());
        }
        var Opened = Invoke("editor.action", new JsonObject
        {
            ["action"] = "editor.open-asset:" + ScreenId,
        });
        var Editor = Opened["assetTools"]!["editors"]!.AsArray()
            .Single(Item => Item!["assetId"]!.GetValue<string>() == ScreenId)!;
        var RootId = Editor["rootId"]!.GetValue<string>();
        Check(Editor["editorId"]!.GetValue<string>() == "ui-editor",
            "The RML document opens in the formal UI editor");
        JsonObject State()
        {
            return Invoke("editor.describe")["assetTools"]!["editors"]!.AsArray()
                .Single(Item => Item!["rootId"]!.GetValue<string>() == RootId)!
                ["state"]!.AsObject();
        }
        JsonObject Observe()
        {
            var Timer = Stopwatch.StartNew();
            while (Timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                var Snapshot = Invoke("ui.describe");
                if (Snapshot["hosts"]!.AsArray().Any(Item =>
                        Item!["hostId"]!.GetValue<string>() == RootId))
                {
                    return Snapshot;
                }
                Thread.Sleep(20);
            }
            throw new TimeoutException("The UI editor host published no rendered frame");
        }
        JsonObject Host(JsonObject Ui)
        {
            return Ui["hosts"]!.AsArray()
                .Single(Item => Item!["hostId"]!.GetValue<string>() == RootId)!.AsObject();
        }
        JsonObject Element(JsonObject Ui, string Id)
        {
            return Host(Ui)["elements"]!.AsArray()
                .Single(Item => Item!["semanticId"]?.GetValue<string>() == Id)!.AsObject();
        }
        void Pointer(string Id, string Event, int OffsetX = 0, int OffsetY = 0)
        {
            for (var Attempt = 0; Attempt < 10; Attempt++)
            {
                var Ui = Observe();
                var Target = Element(Ui, Id);
                var Bounds = Target["bounds"]!;
                var Result = Invoke("ui.pointer", new JsonObject
                {
                    ["hostId"] = RootId,
                    ["event"] = Event,
                    ["button"] = "left",
                    ["x"] = (int)(Bounds["x"]!.GetValue<double>() +
                        Bounds["width"]!.GetValue<double>() / 2) + OffsetX,
                    ["y"] = (int)(Bounds["y"]!.GetValue<double>() +
                        Bounds["height"]!.GetValue<double>() / 2) + OffsetY,
                    ["buttons"] = Event == "down" ? new JsonArray("left") : new JsonArray(),
                    ["modifiers"] = new JsonArray(),
                    ["pointerId"] = 0,
                    ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                    ["expectedElementId"] = Target["elementId"]!.DeepClone(),
                });
                if (Result["accepted"]?.GetValue<bool>() == true)
                {
                    return;
                }
                var Code = Result["diagnostics"]?.AsArray().FirstOrDefault()?["code"]
                    ?.GetValue<string>();
                if (Code is not ("UI_FRAME_STALE" or "UI_TARGET_CHANGED"))
                {
                    throw new InvalidOperationException(Result.ToJsonString());
                }
            }
            throw new InvalidOperationException("Retained UI editor input frame unavailable");
        }
        void Click(string Id)
        {
            Pointer(Id, "down");
            Pointer(Id, "up");
        }

        var Initial = Observe();
        File.WriteAllText(Path.Combine(Output, "initial-host.json"),
            Host(Initial).ToJsonString(new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerOptions.Default)
            {
                WriteIndented = true,
            }));
        var InitialState = State();
        Check(InitialState["schemaVersion"]!.GetValue<int>() == 3 &&
            InitialState["selectedKey"]!.GetValue<string>() == "#screen" &&
            InitialState["elements"]!.AsArray().Count == 3,
            "UI editor publishes schema v3 identity for every screen element");
        Check(Element(Initial, "uiDesignCanvas")["visible"]!.GetValue<bool>() &&
            Element(Initial, "uiDesignCanvas")["bounds"]!["width"]!.GetValue<double>() == 1280 &&
            Element(Initial, "uiDesignCanvas")["bounds"]!["height"]!.GetValue<double>() == 720,
            "The design canvas is a visible native element with the authored screen size");
        var FirstNode = Element(Initial, "ui-design-node-0");
        Check(FirstNode["visible"]!.GetValue<bool>() &&
            FirstNode["hitTarget"]!.GetValue<bool>() &&
            FirstNode["actions"]!.AsArray().Any(Value =>
                Value!.GetValue<string>().StartsWith("editor.ui-select:",
                    StringComparison.Ordinal)),
            "Authored screen elements are visible, selectable design nodes");
        Check(InitialState["styleSheet"]!["files"]!.AsArray().Count == 1 &&
            InitialState["styleSheet"]!["inlineBlocks"]!.GetValue<int>() == 1 &&
            InitialState["styleSheet"]!["ruleCount"]!.GetValue<int>() == 4,
            "Linked RCSS and inline <style> rules both feed the design surface");
        var InlineComputed = InitialState["selected"]!["computed"]!.AsObject();
        Check(InlineComputed["gap"]!["value"]!.GetValue<string>() == "21px" &&
            InlineComputed["gap"]!["source"]!.GetValue<string>() == ".screen",
            "An inline <style> override wins over linked RCSS on the rendered element");
        var IdField = Element(Initial, "uiInspectorField-id");
        Check(IdField["role"]!.GetValue<string>() == "input" &&
            IdField["visible"]!.GetValue<bool>() &&
            IdField["enabled"]!.GetValue<bool>() &&
            IdField["hitTarget"]!.GetValue<bool>() &&
            IdField["value"]?.GetValue<string>() == "screen",
            "Inspector text fields are visible, enabled, clickable native inputs");
        var StyleField = Element(Initial, "uiStyleField-position");
        Check(StyleField["role"]!.GetValue<string>() == "input" &&
            StyleField["visible"]!.GetValue<bool>() &&
            StyleField["hitTarget"]!.GetValue<bool>() &&
            StyleField["bounds"]!["width"]!.GetValue<double>() >= 80,
            "Style fields are clickable native inputs with a usable width");
        var FieldBounds = Element(Observe(), "uiInspectorField-id")["bounds"]!.AsObject();
        var FieldPoint = new JsonObject
        {
            ["x"] = (int)(FieldBounds["x"]!.GetValue<double>() + 10),
            ["y"] = (int)(FieldBounds["y"]!.GetValue<double>() +
                FieldBounds["height"]!.GetValue<double>() / 2),
        };
        JsonObject? FieldHit = null;
        for (var Attempt = 0; Attempt < 10; Attempt++)
        {
            var Ui = Observe();
            var CenterBounds = Element(Ui, "uiInspectorField-id")["bounds"]!.AsObject();
            FieldHit = Invoke("ui.pointer", new JsonObject
            {
                ["hostId"] = RootId,
                ["event"] = "move",
                ["button"] = "left",
                ["x"] = (int)(CenterBounds["x"]!.GetValue<double>() +
                    CenterBounds["width"]!.GetValue<double>() / 2),
                ["y"] = (int)(CenterBounds["y"]!.GetValue<double>() +
                    CenterBounds["height"]!.GetValue<double>() / 2),
                ["buttons"] = new JsonArray(),
                ["modifiers"] = new JsonArray(),
                ["pointerId"] = 0,
                ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
            });
            if (FieldHit["accepted"]?.GetValue<bool>() == true ||
                FieldHit["diagnostics"]?.AsArray().FirstOrDefault()?["code"]
                    ?.GetValue<string>() != "UI_FRAME_STALE")
            {
                break;
            }
        }

        Check(FieldHit["accepted"]?.GetValue<bool>() == true ||
            FieldHit["targetElementId"]?.GetValue<string>()?.EndsWith(
                "/uiInspectorField-id", StringComparison.Ordinal) == true,
            "Pointer input resolves the inspector field through native hit testing");

        Click("ui-design-node-0-1");
        var ClickedState = State();
        Check(ClickedState["selectedKey"]!.GetValue<string>() == "#continue",
            "Clicking a design node selects exactly the authored element");
        Click("uiInspectorField-binding");
        void Key(string Event, int Code, string Text)
        {
            for (var Attempt = 0; Attempt < 10; Attempt++)
            {
                var Ui = Observe();
                var Result = Invoke("ui.key", new JsonObject
                {
                    ["hostId"] = RootId,
                    ["event"] = Event,
                    ["key"] = Code,
                    ["text"] = Text,
                    ["modifiers"] = new JsonArray(),
                    ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                });
                if (Result["accepted"]?.GetValue<bool>() == true)
                {
                    return;
                }
                var KeyCode = Result["diagnostics"]?.AsArray().FirstOrDefault()?["code"]
                    ?.GetValue<string>();
                if (KeyCode is not ("UI_FRAME_STALE" or "UI_TARGET_CHANGED"))
                {
                    throw new InvalidOperationException(Result.ToJsonString());
                }
            }
            throw new InvalidOperationException("Retained UI editor key frame unavailable");
        }
        var Focused = Host(Observe())["focusedElementId"]?.GetValue<string>() ?? string.Empty;
        Check(Focused.EndsWith("/uiInspectorField-binding", StringComparison.Ordinal),
            "Clicking the inspector field moves native keyboard focus into it");
        Key("text", 0, "ALT");
        Click("ui-design-node-0");
        var TypedState = State();
        Check(TypedState["dirty"]!.GetValue<bool>() &&
            TypedState["elements"]!.AsArray().Select(Node => Node!["attributes"])
                .Any(Attributes => Attributes?["data-bind"]?.GetValue<string>()?
                    .Contains("ALT", StringComparison.Ordinal) == true),
            "Typing into the inspector commits the attribute through the native input path");
        var ContentScale = Host(Observe())["contentScale"]?.GetValue<double>() ?? 1;
        var Delta = (int)Math.Round(40 * (ContentScale <= 0 ? 1 : ContentScale));
        var BeforeBounds = Element(Observe(), "ui-design-node-0-1")["bounds"]!.AsObject();
        var AnchorX = (int)(BeforeBounds["x"]!.GetValue<double>() +
            BeforeBounds["width"]!.GetValue<double>() / 2);
        var AnchorY = (int)(BeforeBounds["y"]!.GetValue<double>() +
            BeforeBounds["height"]!.GetValue<double>() / 2);
        void Raw(string Event, int X, int Y, string? Expected = null)
        {
            for (var Attempt = 0; Attempt < 10; Attempt++)
            {
                var Ui = Observe();
                var Arguments = new JsonObject
                {
                    ["hostId"] = RootId,
                    ["event"] = Event,
                    ["button"] = "left",
                    ["x"] = X,
                    ["y"] = Y,
                    ["buttons"] = Event == "up" ? new JsonArray() : new JsonArray("left"),
                    ["modifiers"] = new JsonArray(),
                    ["pointerId"] = 0,
                    ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                };
                if (Expected is not null)
                {
                    Arguments["expectedElementId"] = Expected;
                }
                var Result = Invoke("ui.pointer", Arguments);
                if (Result["accepted"]?.GetValue<bool>() == true)
                {
                    return;
                }
                var Code = Result["diagnostics"]?.AsArray().FirstOrDefault()?["code"]
                    ?.GetValue<string>();
                if (Code is not ("UI_FRAME_STALE" or "UI_TARGET_CHANGED"))
                {
                    throw new InvalidOperationException(Result.ToJsonString());
                }
            }
            throw new InvalidOperationException("Retained UI editor input frame unavailable");
        }
        Raw("down", AnchorX, AnchorY,
            Element(Observe(), "ui-design-node-0-1")["elementId"]!.GetValue<string>());
        Raw("move", AnchorX + Delta, AnchorY);
        Raw("up", AnchorX + Delta, AnchorY);
        var DraggedState = State();
        File.WriteAllText(Path.Combine(Output, "dragged-state.json"),
            DraggedState.ToJsonString(new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerOptions.Default)
            {
                WriteIndented = true,
            }));
        var Dragged = DraggedState["selected"]!;
        var AfterBounds = Element(Observe(), "ui-design-node-0-1")["bounds"]!.AsObject();
        var ShiftX = AfterBounds["x"]!.GetValue<double>() - BeforeBounds["x"]!.GetValue<double>();
        File.WriteAllText(Path.Combine(Output, "drag-bounds.json"), new JsonObject
        {
            ["contentScale"] = ContentScale,
            ["delta"] = Delta,
            ["before"] = BeforeBounds.DeepClone(),
            ["after"] = AfterBounds.DeepClone(),
            ["shiftX"] = ShiftX,
            ["computed"] = Dragged["computed"]!.DeepClone(),
        }.ToJsonString());
        Check(DraggedState["dirty"]!.GetValue<bool>() &&
            Dragged["computed"]!["margin-left"]!["source"]!.GetValue<string>() == "inline" &&
            Math.Abs(ShiftX - Delta) <= 2,
            "Dragging a flow element writes an inline margin through the real input path");
        _ = Invoke("editor.action", new JsonObject
        {
            ["action"] = $"editor.host-action:{RootId}:editor.asset-command:undo",
        });
        var Undone = State();
        Check((!Undone["selected"]!["computed"]!.AsObject().ContainsKey("margin-left") ||
             Undone["selected"]!["computed"]!["margin-left"]!["source"]!.GetValue<string>() != "inline"),
            "One Undo reverts the whole gesture instead of individual pointer steps");

        var SecondAnchor = Element(Observe(), "ui-design-node-0-1")["bounds"]!.AsObject();
        var SecondX = (int)(SecondAnchor["x"]!.GetValue<double>() +
            SecondAnchor["width"]!.GetValue<double>() / 2);
        var SecondY = (int)(SecondAnchor["y"]!.GetValue<double>() +
            SecondAnchor["height"]!.GetValue<double>() / 2);
        Raw("down", SecondX, SecondY,
            Element(Observe(), "ui-design-node-0-1")["elementId"]!.GetValue<string>());
        Raw("move", SecondX + Delta, SecondY);
        Raw("up", SecondX + Delta, SecondY);
        Check(State()["dirty"]!.GetValue<bool>(),
            "A second drag re-applies the gesture from the committed state");
        var Saved = File.ReadAllText(PackagePath);
        Check(!Saved.Contains("margin-left", StringComparison.Ordinal),
            "Native drag preview must not write the source file before Save");
        _ = Invoke("editor.action", new JsonObject
        {
            ["action"] = $"editor.host-action:{RootId}:editor.asset-save",
        });
        Check(File.ReadAllText(PackagePath).Contains("margin-left: 40px", StringComparison.Ordinal),
            "Save persists the committed gesture into the authoritative RML source");
        var SavedPackage = JsonNode.Parse(File.ReadAllText(PackagePath))!.AsObject();
        Check(SavedPackage["schemaVersion"]!.GetValue<int>() == 1 &&
            SavedPackage["name"]!.GetValue<string>() == "Editor Screen" &&
            SavedPackage["rml"]!.GetValue<string>()
                .Contains("margin-left: 40px", StringComparison.Ordinal),
            "Save keeps the .sui envelope and writes the RML back inside it");
        File.WriteAllText(Path.Combine(Output, "ui-editor-state.json"),
            State().ToJsonString(new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerOptions.Default)
            {
                WriteIndented = true,
            }));
        var FrameId = Observe()["frameId"]!.GetValue<ulong>();
        var Frame = Call(Sequence++, "resources/read", new JsonObject
        {
            ["uri"] = $"sharpengine://ui/frames/{FrameId}/{RootId}.png",
        });
        var Blob = Frame["result"]?["contents"]?[0]?["blob"]?.GetValue<string>();
        Check(Blob is not null,
            "The UI editor publishes a rendered frame for its design canvas");
        if (Blob is not null)
        {
            File.WriteAllBytes(Path.Combine(Output, "ui-editor.png"),
                Convert.FromBase64String(Blob));
        }
        _ = Invoke("editor.action", new JsonObject
        {
            ["action"] = $"editor.host-action:{RootId}:window.close-requested",
        });
    }
}
