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
        Func<int, string, JsonObject?, JsonObject> Call,
        Func<byte[], DecodedPng> DecodeFrame)
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
        void CheckDesignClip(JsonObject Ui)
        {
            var Viewport = Element(Ui, "uiDesignViewport")["bounds"]!.AsObject();
            foreach (var Id in new[]
            {
                "uiDesignCanvas", "uiDesignReferenceFrame", "uiDesignContentBounds", "ui-design-node-0",
            })
            {
                var Clip = Element(Ui, Id)["clip"]!.AsObject();
                Check(Clip["x"]!.GetValue<double>() >= Viewport["x"]!.GetValue<double>() - 1 &&
                    Clip["y"]!.GetValue<double>() >= Viewport["y"]!.GetValue<double>() - 1 &&
                    Clip["x"]!.GetValue<double>() + Clip["width"]!.GetValue<double>() <=
                        Viewport["x"]!.GetValue<double>() + Viewport["width"]!.GetValue<double>() + 1 &&
                    Clip["y"]!.GetValue<double>() + Clip["height"]!.GetValue<double>() <=
                        Viewport["y"]!.GetValue<double>() + Viewport["height"]!.GetValue<double>() + 1,
                    $"Design element {Id} is clipped to the viewport even when the canvas overflows (clip={Clip}, viewport={Viewport})");
            }
        }
        void Pointer(string Id, string Event, int OffsetX = 0, int OffsetY = 0)
        {
            JsonObject? LastUi = null;
            JsonObject? LastResult = null;
            for (var Attempt = 0; Attempt < 10; Attempt++)
            {
                var Ui = Observe();
                LastUi = Ui;
                var Target = Element(Ui, Id);
                var Bounds = Target["bounds"]!;
                var Clip = Target["clip"]!.AsObject();
                var Left = Math.Max(Bounds["x"]!.GetValue<double>(), Clip["x"]!.GetValue<double>());
                var Top = Math.Max(Bounds["y"]!.GetValue<double>(), Clip["y"]!.GetValue<double>());
                var Right = Math.Min(Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>(),
                    Clip["x"]!.GetValue<double>() + Clip["width"]!.GetValue<double>());
                var Bottom = Math.Min(Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>(),
                    Clip["y"]!.GetValue<double>() + Clip["height"]!.GetValue<double>());
                var Result = Invoke("ui.pointer", new JsonObject
                {
                    ["hostId"] = RootId,
                    ["event"] = Event,
                    ["button"] = "left",
                    ["x"] = (int)((Left + Right) / 2) + OffsetX,
                    ["y"] = (int)((Top + Bottom) / 2) + OffsetY,
                    ["buttons"] = Event == "down" ? new JsonArray("left") : new JsonArray(),
                    ["modifiers"] = new JsonArray(),
                    ["pointerId"] = 0,
                    ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                    ["expectedElementId"] = Target["elementId"]!.DeepClone(),
                });
                LastResult = Result;
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
            File.WriteAllText(Path.Combine(Output, "pointer-failed-ui.json"), LastUi!.ToJsonString());
            File.WriteAllText(Path.Combine(Output, "pointer-failed-result.json"), LastResult!.ToJsonString());
            var Frame = Call(Sequence++, "resources/read", new JsonObject
            {
                ["uri"] = $"sharpengine://ui/frames/{LastUi["frameId"]}/{RootId}.png",
            });
            var Blob = Frame["result"]?["contents"]?[0]?["blob"]?.GetValue<string>();
            if (Blob is not null)
            {
                File.WriteAllBytes(Path.Combine(Output, "pointer-failed.png"), Convert.FromBase64String(Blob));
            }
            throw new InvalidOperationException("Retained input unavailable for " + Id + " event=" + Event + ": " + LastResult);
        }
        void Click(string Id)
        {
            Pointer(Id, "down");
            Pointer(Id, "up");
        }

        var Initial = Observe();
        CheckDesignClip(Initial);
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
        Check(!InitialState["panes"]!.AsArray().Any(Item =>
                Item!.GetValue<string>() == "preview"),
            "UI asset editor exposes Design without a Runtime Preview pane");
        var PreviewActions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ui-preview-fit"] = "editor.ui-preview-fit",
            ["ui-preview-size-1280-720"] = "editor.ui-preview-size:1280:720",
            ["ui-preview-size-1920-1080"] = "editor.ui-preview-size:1920:1080",
            ["ui-preview-size-2560-1080"] = "editor.ui-preview-size:2560:1080",
            ["ui-preview-size-720-1280"] = "editor.ui-preview-size:720:1280",
        };
        Check(PreviewActions.All(Pair =>
        {
            var Button = Element(Initial, Pair.Key);
            return Button["visible"]!.GetValue<bool>() &&
                Button["semanticId"]!.GetValue<string>() == Pair.Key &&
                Button["actions"]!.AsArray().Any(Value =>
                    Value!.GetValue<string>() == Pair.Value);
        }), "Preview controls expose stable semantic IDs and their typed actions");
        Check(Host(Initial)["elements"]!.AsArray()
            .All(Item =>
            {
                var SemanticId = Item!["semanticId"]?.GetValue<string>() ?? string.Empty;
                return !SemanticId.StartsWith("ui-preview-zoom-", StringComparison.Ordinal) &&
                    SemanticId is not ("uiPreviewZoom" or "uiPreviewZoomValue");
            }),
            "Preview exposes no redundant zoom controls");
        Check(Element(Initial, "uiDesignCanvas")["visible"]!.GetValue<bool>() &&
            Element(Initial, "uiDesignCanvas")["bounds"]!["width"]!.GetValue<double>() == 1920 &&
            Element(Initial, "uiDesignCanvas")["bounds"]!["height"]!.GetValue<double>() == 1080,
            "The design reference frame is a visible native element with the reference screen size");
        var ContentBoundsElement = Element(Initial, "uiDesignContentBounds");
        var ContentBounds = InitialState["design"]!["contentBounds"]?.AsObject();
        Check(ContentBoundsElement["visible"]!.GetValue<bool>() &&
            ContentBounds is not null &&
            ContentBounds["width"]!.GetValue<double>() > 0 &&
            ContentBounds["height"]!.GetValue<double>() > 0 &&
            ContentBounds["width"]!.GetValue<double>() <= 1920 &&
            ContentBounds["height"]!.GetValue<double>() <= 1080,
            "The design view exposes measured content bounds separately from the reference screen");
        Click("ui-preview-size-1280-720");
        var ResizedState = State();
        Check(ResizedState["design"]!["width"]!.GetValue<int>() == 1280 &&
            ResizedState["design"]!["height"]!.GetValue<int>() == 720 &&
            ResizedState["design"]!["referenceWidth"]!.GetValue<int>() == 1280 &&
            ResizedState["design"]!["referenceHeight"]!.GetValue<int>() == 720 &&
            ResizedState["design"]!["zoom"]!.GetValue<double>() == 1 &&
            !ResizedState["dirty"]!.GetValue<bool>(),
            "A preview size control changes the reference screen independently of zoom and source state");
        Click("ui-preview-size-1920-1080");
        var RestoredPreviewState = State();
        Check(RestoredPreviewState["design"]!["width"]!.GetValue<int>() == 1920 &&
            RestoredPreviewState["design"]!["height"]!.GetValue<int>() == 1080 &&
            RestoredPreviewState["design"]!["zoom"]!.GetValue<double>() == 1,
            "The semantic preview checks restore the 1920 x 1080 default transient environment");
        var FirstNode = Element(Initial, "ui-design-node-0");
        Check(FirstNode["visible"]!.GetValue<bool>() &&
            FirstNode["actions"]!.AsArray().Any(Value =>
                Value!.GetValue<string>().StartsWith("editor.ui-select:",
                    StringComparison.Ordinal)),
            "Authored screen elements are visible and expose selectable design nodes");
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
        var StyleField = Element(Initial, "uiControlField-gap");
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

        Check(FieldHit is not null && (FieldHit["accepted"]?.GetValue<bool>() == true ||
            FieldHit["targetElementId"]?.GetValue<string>()?.EndsWith(
                "/uiInspectorField-id", StringComparison.Ordinal) == true),
            "Pointer input resolves the inspector field through native hit testing");

        Click("ui-design-node-0-1");
        var ClickedState = State();
        Check(ClickedState["selectedKey"]!.GetValue<string>() == "#continue",
            "Clicking a design node selects exactly the authored element");
        Click("uiInspectorField-binding");
        void Key(string Event, int Code, string Text, JsonArray? Modifiers = null)
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
                    ["modifiers"] = Modifiers?.DeepClone() ?? new JsonArray(),
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
        void Wheel(string Id, float WheelY)
        {
            JsonObject? LastResult = null;
            for (var Attempt = 0; Attempt < 10; Attempt++)
            {
                var Ui = Observe();
                var Target = Element(Ui, Id);
                var Bounds = Target["bounds"]!.AsObject();
                var Clip = Target["clip"]!.AsObject();
                var Left = Math.Max(Bounds["x"]!.GetValue<double>(), Clip["x"]!.GetValue<double>());
                var Top = Math.Max(Bounds["y"]!.GetValue<double>(), Clip["y"]!.GetValue<double>());
                var Right = Math.Min(Bounds["x"]!.GetValue<double>() +
                    Bounds["width"]!.GetValue<double>(),
                    Clip["x"]!.GetValue<double>() + Clip["width"]!.GetValue<double>());
                var Bottom = Math.Min(Bounds["y"]!.GetValue<double>() +
                    Bounds["height"]!.GetValue<double>(),
                    Clip["y"]!.GetValue<double>() + Clip["height"]!.GetValue<double>());
                LastResult = Invoke("ui.pointer", new JsonObject
                {
                    ["hostId"] = RootId,
                    ["event"] = "wheel",
                    ["button"] = "none",
                    ["x"] = (int)((Left + Right) / 2),
                    ["y"] = (int)((Top + Bottom) / 2),
                    ["buttons"] = new JsonArray(),
                    ["modifiers"] = new JsonArray(),
                    ["pointerId"] = 0,
                    ["wheelX"] = 0,
                    ["wheelY"] = WheelY,
                    ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                });
                if (LastResult["accepted"]?.GetValue<bool>() == true)
                {
                    return;
                }
                var Code = LastResult["diagnostics"]?.AsArray().FirstOrDefault()?["code"]
                    ?.GetValue<string>();
                if (Code is not ("UI_FRAME_STALE" or "UI_TARGET_CHANGED"))
                {
                    throw new InvalidOperationException(LastResult.ToJsonString());
                }
            }
            throw new InvalidOperationException("Retained UI editor wheel input unavailable");
        }
        var BeforeZoomUi = Observe();
        var BeforeZoomCanvas = Element(BeforeZoomUi, "uiDesignCanvas")["bounds"]!.AsObject();
        var ZoomViewport = Element(BeforeZoomUi, "uiDesignViewport")["bounds"]!.AsObject();
        var ZoomAnchorX = (int)(ZoomViewport["x"]!.GetValue<double>() + ZoomViewport["width"]!.GetValue<double>() / 2);
        var ZoomAnchorY = (int)(ZoomViewport["y"]!.GetValue<double>() + ZoomViewport["height"]!.GetValue<double>() / 2);
        void CaptureDesignClipEvidence(JsonObject Ui, string Name)
        {
            CheckDesignClip(Ui);
            File.WriteAllText(Path.Combine(Output, Name + ".json"), Ui.ToJsonString());
            var Frame = Call(Sequence++, "resources/read", new JsonObject
            {
                ["uri"] = $"sharpengine://ui/frames/{Ui["frameId"]}/{RootId}.png",
            });
            var Blob = Frame["result"]?["contents"]?[0]?["blob"]?.GetValue<string>();
            Check(Blob is not null, $"{Name} publishes matching rendered evidence");
            if (Blob is null)
            {
                return;
            }
            var Bytes = Convert.FromBase64String(Blob);
            File.WriteAllBytes(Path.Combine(Output, Name + ".png"), Bytes);
            var Pixels = DecodeFrame(Bytes);
            var Viewport = Element(Ui, "uiDesignViewport")["bounds"]!.AsObject();
            var Left = (int)Math.Floor(Viewport["x"]!.GetValue<double>());
            var Top = (int)Math.Floor(Viewport["y"]!.GetValue<double>());
            var Right = (int)Math.Ceiling(Viewport["x"]!.GetValue<double>() + Viewport["width"]!.GetValue<double>());
            var Bottom = (int)Math.Ceiling(Viewport["y"]!.GetValue<double>() + Viewport["height"]!.GetValue<double>());
            var InsideDashes = 0;
            var LeakedContentDashes = 0;
            var LeakedReferenceDashes = 0;
            for (var Y = Math.Max(0, Top - 80); Y < Math.Min(Pixels.Height, Bottom + 80); Y++)
            {
                for (var X = Math.Max(0, Left - 80); X < Math.Min(Pixels.Width, Right + 80); X++)
                {
                    var Pixel = Pixels.Pixel(X, Y);
                    var IsContentDash = Pixel.R == 214 && Pixel.G == 174 && Pixel.B == 109;
                    var IsReferenceDash = Pixel.R == 146 && Pixel.G == 164 && Pixel.B == 185;
                    if (!IsContentDash && !IsReferenceDash)
                    {
                        continue;
                    }
                    if (X >= Left && X < Right && Y >= Top && Y < Bottom)
                    {
                        InsideDashes++;
                    }
                    else if (X < Left - 1 || X > Right + 1 || Y < Top - 1 || Y > Bottom + 1)
                    {
                        if (IsContentDash)
                        {
                            LeakedContentDashes++;
                        }
                        else
                        {
                            LeakedReferenceDashes++;
                        }
                    }
                }
            }
            Check(InsideDashes > 0, $"{Name} retains visible frame dashes inside Design");
            Check(LeakedContentDashes == 0 && LeakedReferenceDashes == 0,
                $"{Name} clips both frame colors on all four viewport edges " +
                $"(contentLeaks={LeakedContentDashes}, referenceLeaks={LeakedReferenceDashes})");
        }
        Wheel("uiDesignViewport", -1);
        var WheelZoomState = State();
        Check(Math.Abs(WheelZoomState["design"]!["zoom"]!.GetValue<double>() - 1.1) < 0.0001,
            "Wheel input over the design viewport changes preview zoom through the native RmlUi path");
        var AfterZoomCanvas = Element(Observe(), "uiDesignCanvas")["bounds"]!.AsObject();
        Check(Math.Abs(AfterZoomCanvas["x"]!.GetValue<double>() -
                (ZoomAnchorX - (ZoomAnchorX - BeforeZoomCanvas["x"]!.GetValue<double>()) * 1.1)) < 1.5 &&
            Math.Abs(AfterZoomCanvas["y"]!.GetValue<double>() -
                (ZoomAnchorY - (ZoomAnchorY - BeforeZoomCanvas["y"]!.GetValue<double>()) * 1.1)) < 1.5,
            "Native wheel zoom keeps the canvas point under the pointer stationary");
        Wheel("uiDesignViewport", 1);
        Check(Math.Abs(State()["design"]!["zoom"]!.GetValue<double>() - 1) < 0.0001,
            "Wheel input can return the design preview to its default zoom");
        var NavigationRevision = State()["revision"]!.GetValue<long>();
        Wheel("uiDesignViewport", 20);
        Check(State()["design"]!["zoom"]!.GetValue<double>() < 0.25,
            "Native wheel zoom continues below the former 25 percent limit");
        Wheel("uiDesignViewport", -40);
        Check(State()["design"]!["zoom"]!.GetValue<double>() > 2,
            "Native wheel zoom continues above the former 200 percent limit");
        Wheel("uiDesignViewport", 20);
        void RightDrag(string Id, int DeltaX, int DeltaY)
        {
            var InitialUi = Observe();
            var Target = Element(InitialUi, Id);
            var Bounds = Target["bounds"]!.AsObject();
            var Clip = Target["clip"]!.AsObject();
            var Left = Math.Max(Bounds["x"]!.GetValue<double>(), Clip["x"]!.GetValue<double>());
            var Top = Math.Max(Bounds["y"]!.GetValue<double>(), Clip["y"]!.GetValue<double>());
            var Right = Math.Min(Bounds["x"]!.GetValue<double>() +
                Bounds["width"]!.GetValue<double>(),
                Clip["x"]!.GetValue<double>() + Clip["width"]!.GetValue<double>());
            var Bottom = Math.Min(Bounds["y"]!.GetValue<double>() +
                Bounds["height"]!.GetValue<double>(),
                Clip["y"]!.GetValue<double>() + Clip["height"]!.GetValue<double>());
            var StartX = (int)((Left + Right) / 2);
            var StartY = (int)((Top + Bottom) / 2);
            void Send(string Event, int X, int Y, bool Held)
            {
                JsonObject? LastResult = null;
                for (var Attempt = 0; Attempt < 10; Attempt++)
                {
                    var Ui = Observe();
                    var Arguments = new JsonObject
                    {
                        ["hostId"] = RootId,
                        ["event"] = Event,
                        ["button"] = "right",
                        ["x"] = X,
                        ["y"] = Y,
                        ["buttons"] = Held ? new JsonArray("right") : new JsonArray(),
                        ["modifiers"] = new JsonArray(),
                        ["pointerId"] = 0,
                        ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                    };
                    var Result = Invoke("ui.pointer", Arguments);
                    LastResult = Result;
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
                throw new InvalidOperationException(
                    "Retained right-drag input frame unavailable: " +
                    LastResult?.ToJsonString());
            }
            Send("down", StartX, StartY, true);
            Send("move", StartX + DeltaX, StartY + DeltaY, true);
            Send("up", StartX + DeltaX, StartY + DeltaY, false);
        }
        Wheel("uiDesignViewport", -1);
        var BeforePanCanvas = Element(Observe(), "uiDesignCanvas")["bounds"]!.AsObject();
        RightDrag("uiDesignViewport", -120, -80);
        var NegativeOverflowUi = Observe();
        var AfterPanCanvas = Element(NegativeOverflowUi, "uiDesignCanvas")["bounds"]!.AsObject();
        var NegativeViewport = Element(NegativeOverflowUi, "uiDesignViewport")["bounds"]!.AsObject();
        Check(AfterPanCanvas["x"]!.GetValue<double>() < NegativeViewport["x"]!.GetValue<double>() &&
            AfterPanCanvas["y"]!.GetValue<double>() < NegativeViewport["y"]!.GetValue<double>() &&
            AfterPanCanvas["x"]!.GetValue<double>() + AfterPanCanvas["width"]!.GetValue<double>() >
                NegativeViewport["x"]!.GetValue<double>() + NegativeViewport["width"]!.GetValue<double>() &&
            AfterPanCanvas["y"]!.GetValue<double>() + AfterPanCanvas["height"]!.GetValue<double>() >
                NegativeViewport["y"]!.GetValue<double>() + NegativeViewport["height"]!.GetValue<double>(),
            "Zoomed and panned reference frame extends beyond all four viewport edges");
        CaptureDesignClipEvidence(NegativeOverflowUi, "negative-overflow-frame");
        Check(AfterPanCanvas["x"]!.GetValue<double>() < BeforePanCanvas["x"]!.GetValue<double>() &&
            Math.Abs(State()["design"]!["zoom"]!.GetValue<double>() - 1.1) < 0.0001,
            $"Right-dragging the design viewport pans the canvas without changing document zoom " +
            $"(before={BeforePanCanvas}, after={AfterPanCanvas}, state={State()["design"]})");
        var NegativeOffset = State()["design"]!["offsetX"]!.GetValue<double>();
        RightDrag("uiDesignViewport", 260, 160);
        var OverflowUi = Observe();
        CaptureDesignClipEvidence(OverflowUi, "overflow-frame");
        Check(Math.Abs(State()["design"]!["offsetX"]!.GetValue<double>() - NegativeOffset - 260) < 1 &&
            Element(Observe(), "uiDesignCanvas")["bounds"]!["x"]!.GetValue<double>() >
                BeforePanCanvas["x"]!.GetValue<double>() &&
            State()["revision"]!.GetValue<long>() == NavigationRevision,
            "Right-drag crosses the old scroll origin in both directions without clamping or authoring edits");
        RightDrag("uiDesignViewport", -140, -80);
        Wheel("uiDesignViewport", 1);
        var ContentScale = Host(Observe())["contentScale"]?.GetValue<double>() ?? 1;
        var Delta = (int)Math.Round(40 * (ContentScale <= 0 ? 1 : ContentScale));
        var BeforeNode = Element(Observe(), "ui-design-node-0-1");
        var BeforeBounds = BeforeNode["bounds"]!.AsObject();
        var BeforeClip = BeforeNode["clip"]!.AsObject();
        var AnchorLeft = Math.Max(BeforeBounds["x"]!.GetValue<double>(),
            BeforeClip["x"]!.GetValue<double>());
        var AnchorTop = Math.Max(BeforeBounds["y"]!.GetValue<double>(),
            BeforeClip["y"]!.GetValue<double>());
        var AnchorRight = Math.Min(
            BeforeBounds["x"]!.GetValue<double>() + BeforeBounds["width"]!.GetValue<double>(),
            BeforeClip["x"]!.GetValue<double>() + BeforeClip["width"]!.GetValue<double>());
        var AnchorBottom = Math.Min(
            BeforeBounds["y"]!.GetValue<double>() + BeforeBounds["height"]!.GetValue<double>(),
            BeforeClip["y"]!.GetValue<double>() + BeforeClip["height"]!.GetValue<double>());
        var AnchorX = (int)((AnchorLeft + AnchorRight) / 2);
        var AnchorY = (int)((AnchorTop + AnchorBottom) / 2);
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

        var SecondNode = Element(Observe(), "ui-design-node-0-1");
        var SecondAnchor = SecondNode["bounds"]!.AsObject();
        var SecondClip = SecondNode["clip"]!.AsObject();
        var SecondLeft = Math.Max(SecondAnchor["x"]!.GetValue<double>(),
            SecondClip["x"]!.GetValue<double>());
        var SecondTop = Math.Max(SecondAnchor["y"]!.GetValue<double>(),
            SecondClip["y"]!.GetValue<double>());
        var SecondRight = Math.Min(
            SecondAnchor["x"]!.GetValue<double>() + SecondAnchor["width"]!.GetValue<double>(),
            SecondClip["x"]!.GetValue<double>() + SecondClip["width"]!.GetValue<double>());
        var SecondBottom = Math.Min(
            SecondAnchor["y"]!.GetValue<double>() + SecondAnchor["height"]!.GetValue<double>(),
            SecondClip["y"]!.GetValue<double>() + SecondClip["height"]!.GetValue<double>());
        var SecondX = (int)((SecondLeft + SecondRight) / 2);
        var SecondY = (int)((SecondTop + SecondBottom) / 2);
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
        var SavedRevision = State()["revision"]!.GetValue<long>();
        Click("uiTreeToggle-ui-design-node-0");
        Check(State()["tree"]!["visibleKeys"]!.AsArray().Count == 1 &&
            !State()["dirty"]!.GetValue<bool>() && State()["revision"]!.GetValue<long>() == SavedRevision,
            "A real tree-toggle click collapses a branch without dirtying the document");
        Click("ui-design-node-0-1");
        Check(State()["tree"]!["visibleKeys"]!.AsArray().Count == 3,
            "Canvas selection reveals the selected control in a collapsed tree");
        Click("uiTreeSearch");
        Key("text", 0, "Continue");
        Click("uiTreeSelect-ui-design-node-0");
        Check(State()["tree"]!["search"]!.GetValue<string>() == "Continue" &&
            State()["tree"]!["visibleKeys"]!.AsArray().Count == 2 && !State()["dirty"]!.GetValue<bool>(),
            "Native search input filters the tree while retaining the parent path");
        Click("uiTreeClear");
        Check(State()["tree"]!["visibleKeys"]!.AsArray().Count == 3,
            "The native Clear control restores the full source tree");
        for (var Scroll = 0; Scroll < 8; Scroll++)
        {
            var Ui = Observe();
            var Target = Element(Ui, "uiControlAdd-checkbox");
            var Bounds = Target["bounds"]!.AsObject();
            var Clip = Target["clip"]!.AsObject();
            if (Bounds["y"]!.GetValue<double>() >= Clip["y"]!.GetValue<double>() &&
                Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>() <=
                Clip["y"]!.GetValue<double>() + Clip["height"]!.GetValue<double>())
            {
                break;
            }
            var Anchor = Element(Ui, "uiControlPalette");
            var AnchorBounds = Anchor["bounds"]!.AsObject();
            var WheelResult = Invoke("ui.pointer", new JsonObject
            {
                ["hostId"] = RootId,
                ["event"] = "wheel",
                ["button"] = "none",
                ["buttons"] = new JsonArray(),
                ["modifiers"] = new JsonArray(),
                ["pointerId"] = 0,
                ["x"] = (int)(AnchorBounds["x"]!.GetValue<double>() + AnchorBounds["width"]!.GetValue<double>() / 2),
                ["y"] = (int)(AnchorBounds["y"]!.GetValue<double>() + AnchorBounds["height"]!.GetValue<double>() / 2),
                ["wheelY"] = Bounds["y"]!.GetValue<double>() < Clip["y"]!.GetValue<double>() ? -1 : 1,
                ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                ["expectedElementId"] = Anchor["elementId"]!.DeepClone(),
            });
            File.WriteAllText(Path.Combine(Output, "palette-wheel-" + Scroll + ".json"), WheelResult.ToJsonString());
        }
        Click("uiControlAdd-checkbox");
        Check(State()["selectedKey"]!.GetValue<string>() == "#checkbox" &&
            State()["selected"]!["controlProperties"]!.AsArray().Any(Property => Property!["name"]!.GetValue<string>() == "checked"),
            "Clicking the palette adds and selects a checkbox with typed properties");
        Click("uiControlField-checked");
        Check(State()["selected"]!["controlProperties"]!.AsArray()
            .Single(Property => Property!["name"]!.GetValue<string>() == "checked")!["value"]!.GetValue<string>() == "true",
            "A real property toggle edits the native checkbox initial state");
        Click("uiInspectorField-name");
        Key("down", 0x41, "", new JsonArray("control"));
        Key("up", 0x41, "", new JsonArray("control"));
        Key("text", 0, "Accept terms");
        Click("uiTreeSelect-ui-design-node-0");
        var Checkbox = State()["elements"]!.AsArray().Single(Node => Node!["elementId"]!.GetValue<string>() == "checkbox")!;
        Check(Checkbox["displayName"]!.GetValue<string>() == "Accept terms" &&
            Checkbox["attributes"]!["checked"] is not null &&
            !File.ReadAllText(PackagePath).Contains("Accept terms", StringComparison.Ordinal),
            "Typing a readable control name preserves its ID and remains unsaved until Save");
        var BeforeFit = State();
        Click("ui-preview-fit");
        var FitState = State();
        Check(FitState["design"]!["width"]!.GetValue<int>() ==
                BeforeFit["design"]!["width"]!.GetValue<int>() &&
            FitState["design"]!["height"]!.GetValue<int>() ==
                BeforeFit["design"]!["height"]!.GetValue<int>() &&
            FitState["design"]!["zoom"]!.GetValue<double>() <= 1 &&
            FitState["dirty"]!.GetValue<bool>() == BeforeFit["dirty"]!.GetValue<bool>(),
            "Fit changes only the Design view zoom and preserves the desired canvas size and document state");
        _ = Invoke("editor.action", new JsonObject
        {
            ["action"] = $"editor.host-action:{RootId}:editor.asset-save",
        });
        _ = Invoke("editor.action", new JsonObject
        {
            ["action"] = $"editor.host-action:{RootId}:window.close-requested",
        });
        _ = Invoke("editor.action", new JsonObject
        {
            ["action"] = "editor.open-asset:" + ScreenId,
        });
        var Reopened = State();
        var ReopenedCheckbox = Reopened["elements"]!.AsArray().Single(Node => Node!["elementId"]!.GetValue<string>() == "checkbox")!;
        Check(!Reopened["dirty"]!.GetValue<bool>() && ReopenedCheckbox["displayName"]!.GetValue<string>() == "Accept terms" &&
            ReopenedCheckbox["attributes"]!["checked"] is not null && Reopened["tree"]!["search"]!.GetValue<string>() == string.Empty,
            "Closing and reopening the real asset editor restores the authored control and resets transient search");
        File.WriteAllText(Path.Combine(Output, "ui-editor-state.json"),
            State().ToJsonString(new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerOptions.Default)
            {
                WriteIndented = true,
            }));
        Click("ui-preview-fit");
        var FittedUi = Observe();
        var FittedViewport = Element(FittedUi, "uiDesignViewport")["bounds"]!.AsObject();
        var FittedCanvas = Element(FittedUi, "uiDesignCanvas")["bounds"]!.AsObject();
        Check(Math.Abs(FittedCanvas["x"]!.GetValue<double>() + FittedCanvas["width"]!.GetValue<double>() / 2 -
                FittedViewport["x"]!.GetValue<double>() - FittedViewport["width"]!.GetValue<double>() / 2) < 1.5 &&
            Math.Abs(FittedCanvas["y"]!.GetValue<double>() + FittedCanvas["height"]!.GetValue<double>() / 2 -
                FittedViewport["y"]!.GetValue<double>() - FittedViewport["height"]!.GetValue<double>() / 2) < 1.5 &&
            Element(FittedUi, "uiDesignReferenceFrame")["visible"]!.GetValue<bool>(),
            "Fit centers the reference preview and keeps its dashed frame visible");
        File.WriteAllText(Path.Combine(Output, "ui-editor-frame.json"), FittedUi.ToJsonString());
        var FrameId = FittedUi["frameId"]!.GetValue<ulong>();
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
