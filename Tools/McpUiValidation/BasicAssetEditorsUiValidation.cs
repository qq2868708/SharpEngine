using System.Text.Json.Nodes;

internal static class BasicAssetEditorsUiValidation
{
    private static string ProjectRoot = string.Empty;

    internal static string CreateProject(string Root)
    {
        var Manifest = UiEditorUiValidation.CreateProject(Root);
        ProjectRoot = Path.GetDirectoryName(Manifest)!;
        BasicAssetFixture.Prepare(ProjectRoot);
        return Manifest;
    }

    internal static void Run(string Root, Action<bool, string> Check,
        Func<int, string, JsonObject, JsonObject> Tool,
        Func<int, string, JsonObject?, JsonObject> Call, Func<byte[], DecodedPng> DecodeFrame,
        bool SkeletonOnly = false, bool StateMachineOnly = false, bool StaticMeshOnly = false, bool MeasurePerformance = true)
    {
        const double HierarchyFrameAverageBudgetMs = 18;
        const double HierarchyFrameP95BudgetMs = 25;
        var Output = Path.Combine(Root, "Saved", "Validation", "BasicAssetEditors");
        Directory.CreateDirectory(Output);
        var Sequence = 81000;
        var HostId = "main";
        JsonObject Invoke(string Name, JsonObject? Arguments = null)
        {
            return Tool(Sequence++, Name, Arguments ?? new JsonObject())["result"]!["structuredContent"]!.AsObject();
        }
        JsonObject Editor(string Id)
        {
            return Invoke("editor.describe")["assetTools"]!["editors"]!.AsArray()
                .Single(Node => Node!["assetId"]!.GetValue<string>() == Id)!.AsObject();
        }
        void Open(string Id)
        {
            Invoke("editor.action", new JsonObject
            {
                ["action"] = "editor.open-asset:" + Id,
            });
            HostId = Editor(Id)["rootId"]!.GetValue<string>();
        }
        JsonObject Observe() => Invoke("ui.describe");
        JsonObject Host(JsonObject Ui) => Ui["hosts"]!.AsArray()
            .Single(Node => Node!["hostId"]!.GetValue<string>() == HostId)!.AsObject();
        JsonObject Find(JsonObject Ui, string Id)
        {
            var Element = Host(Ui)["elements"]!.AsArray().Select(Node => Node!.AsObject()).FirstOrDefault(Node =>
                Node["visible"]!.GetValue<bool>() &&
                (Node["semanticId"]?.GetValue<string>() == Id ||
                    Node["actions"]!.AsArray().Any(Action => Action!.GetValue<string>() == Id)));
            if (Element is null)
            {
                File.WriteAllText(Path.Combine(Output, "missing-element.json"), Ui.ToJsonString());
                File.WriteAllText(Path.Combine(Output, "missing-element-editor.json"), Invoke("editor.describe").ToJsonString());
                var Frame = Call(Sequence++, "resources/read", new JsonObject
                {
                    ["uri"] = $"sharpengine://ui/frames/{Ui["frameId"]}/{HostId}.png",
                });
                File.WriteAllBytes(Path.Combine(Output, "missing-element.png"),
                    Convert.FromBase64String(Frame["result"]!["contents"]![0]!["blob"]!.GetValue<string>()));
                throw new InvalidOperationException("No visible UI element: " + Id);
            }
            return Element;
        }
        void Click(string Id, int Count = 1)
        {
            for (var Index = 0; Index < Count; Index++)
            {
                foreach (var Event in new[]
                {
                    "down", "up",
                })
                {
                    var Accepted = false;
                    JsonObject? LastResult = null;
                    for (var Attempt = 0; Attempt < 10; Attempt++)
                    {
                        var Ui = Observe();
                        var Element = Find(Ui, Id);
                        var Bounds = Element["bounds"]!;
                        var Clip = Element["clip"]!;
                        var Left = Math.Max(Bounds["x"]!.GetValue<double>(), Clip["x"]!.GetValue<double>());
                        var Top = Math.Max(Bounds["y"]!.GetValue<double>(), Clip["y"]!.GetValue<double>());
                        var Right = Math.Min(Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>(),
                            Clip["x"]!.GetValue<double>() + Clip["width"]!.GetValue<double>());
                        var Bottom = Math.Min(Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>(),
                            Clip["y"]!.GetValue<double>() + Clip["height"]!.GetValue<double>());
                        Check(Right > Left && Bottom > Top, Id + " has a visible hit-test area");
                        var Result = Invoke("ui.pointer", new JsonObject
                        {
                            ["hostId"] = HostId, ["event"] = Event, ["button"] = "left",
                            ["x"] = (int)((Left + Right) / 2), ["y"] = (int)((Top + Bottom) / 2),
                            ["buttons"] = new JsonArray(), ["modifiers"] = new JsonArray(), ["pointerId"] = 0,
                            ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                            ["expectedElementId"] = Element["elementId"]!.DeepClone(),
                        });
                        LastResult = Result;
                        if (Result["accepted"]?.GetValue<bool>() == true)
                        {
                            Accepted = true;
                            break;
                        }
                        var Code = Result["diagnostics"]?[0]?["code"]?.GetValue<string>();
                        if (Code is not ("UI_FRAME_STALE" or "UI_TARGET_CHANGED"))
                        {
                            throw new InvalidOperationException(Result.ToJsonString());
                        }
                    }
                    if (!Accepted)
                    {
                        var Ui = Observe();
                        File.WriteAllText(Path.Combine(Output, "rejected-pointer-ui.json"), Ui.ToJsonString());
                        var Frame = Call(Sequence++, "resources/read", new JsonObject
                        {
                            ["uri"] = $"sharpengine://ui/frames/{Ui["frameId"]}/{HostId}.png",
                        });
                        File.WriteAllBytes(Path.Combine(Output, "rejected-pointer.png"),
                            Convert.FromBase64String(Frame["result"]!["contents"]![0]!["blob"]!.GetValue<string>()));
                    }
                    Check(Accepted, "Native pointer accepted for " + Id + ": " + LastResult?.ToJsonString());
                }
            }
        }
        void Key(string Event, int Code, string Text = "", bool Control = false)
        {
            for (var Attempt = 0; Attempt < 10; Attempt++)
            {
                var Ui = Observe();
                var Result = Invoke("ui.key", new JsonObject
                {
                    ["hostId"] = HostId, ["event"] = Event, ["key"] = Code, ["text"] = Text,
                    ["modifiers"] = Control ? new JsonArray("control") : new JsonArray(),
                    ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                });
                if (Result["accepted"]?.GetValue<bool>() == true)
                {
                    return;
                }
                if (Result["diagnostics"]?[0]?["code"]?.GetValue<string>() != "UI_FRAME_STALE")
                {
                    throw new InvalidOperationException(Result.ToJsonString());
                }
            }
            throw new InvalidOperationException("Could not obtain a current key frame.");
        }
        void Capture(string Name, string SemanticId, bool? Image)
        {
            var Ui = Observe();
            var Target = Find(Ui, SemanticId);
            var Frame = Call(Sequence++, "resources/read", new JsonObject
            {
                ["uri"] = $"sharpengine://ui/frames/{Ui["frameId"]}/{HostId}.png",
            });
            var Bytes = Convert.FromBase64String(Frame["result"]!["contents"]![0]!["blob"]!.GetValue<string>());
            File.WriteAllBytes(Path.Combine(Output, Name + ".png"), Bytes);
            File.WriteAllText(Path.Combine(Output, Name + ".json"), Ui.ToJsonString());
            if (Image is null)
            {
                return;
            }
            var Pixels = DecodeFrame(Bytes);
            var Bounds = Target["bounds"]!;
            var Matched = 0;
            for (var Y = (int)Bounds["y"]!.GetValue<double>();
                Y < Math.Min(Pixels.Height, Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>()); Y++)
            {
                for (var X = (int)Bounds["x"]!.GetValue<double>();
                    X < Math.Min(Pixels.Width, Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>()); X++)
                {
                    if (X < 0 || Y < 0)
                    {
                        continue;
                    }
                    var Pixel = Pixels.Pixel(X, Y);
                    if (Image.Value ? Pixel.R > 170 && Pixel.G > 120 && Pixel.B < 110 :
                        Pixel.G > 130 && Pixel.G > Pixel.R + 40 && Pixel.B > 100)
                    {
                        Matched++;
                    }
                }
            }
            Check(Matched >= (Image.Value ? 100 : 5), Name + " renders expected pixels in its matching retained frame");
        }
        void Reveal(string Id, string ScrollOwner)
        {
            for (var Attempt = 0; Attempt < 40; Attempt++)
            {
                var Ui = Observe();
                var Owner = Find(Ui, ScrollOwner);
                var Target = Host(Ui)["elements"]!.AsArray().Single(Node =>
                    Node!["semanticId"]?.GetValue<string>() == Id ||
                    Node!["actions"]!.AsArray().Any(Action => Action!.GetValue<string>() == Id))!;
                var Bounds = Target["bounds"]!;
                var OwnerBounds = Owner["bounds"]!;
                var Top = OwnerBounds["y"]!.GetValue<double>();
                var Bottom = Top + OwnerBounds["height"]!.GetValue<double>();
                if (Target["visible"]!.GetValue<bool>() && Bounds["y"]!.GetValue<double>() >= Top &&
                    Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>() <= Bottom)
                {
                    return;
                }
                var Result = Invoke("ui.pointer", new JsonObject
                {
                    ["hostId"] = HostId,
                    ["event"] = "wheel",
                    ["button"] = "none",
                    ["buttons"] = new JsonArray(),
                    ["modifiers"] = new JsonArray(),
                    ["pointerId"] = 0,
                    ["x"] = (int)(OwnerBounds["x"]!.GetValue<double>() + OwnerBounds["width"]!.GetValue<double>() / 2),
                    ["y"] = (int)((Top + Bottom) / 2),
                    ["wheelY"] = Bounds["y"]!.GetValue<double>() < Top ? -1 : 1,
                    ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                });
                Check(Result["accepted"]?.GetValue<bool>() == true ||
                    Result["diagnostics"]?[0]?["code"]?.GetValue<string>() == "UI_FRAME_STALE",
                    "Native scroll reaches " + ScrollOwner);
            }
            throw new InvalidOperationException("Could not reveal " + Id);
        }
        IReadOnlyList<int> CaptureStateMachineViewport(string Name)
        {
            var Ui = Observe();
            var Viewport = Find(Ui, "stateMachinePreviewViewport");
            var Frame = Call(Sequence++, "resources/read", new JsonObject
            {
                ["uri"] = $"sharpengine://ui/frames/{Ui["frameId"]}/{HostId}.png",
            });
            var Bytes = Convert.FromBase64String(Frame["result"]!["contents"]![0]!["blob"]!.GetValue<string>());
            File.WriteAllBytes(Path.Combine(Output, Name + ".png"), Bytes);
            File.WriteAllText(Path.Combine(Output, Name + ".json"), Ui.ToJsonString());
            var Image = DecodeFrame(Bytes);
            var Bounds = Viewport["bounds"]!;
            var Samples = new List<int>();
            for (var Y = (int)Bounds["y"]!.GetValue<double>() + 10;
                Y < Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>() - 10; Y++)
            {
                for (var X = (int)Bounds["x"]!.GetValue<double>() + 10;
                    X < Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>() - 10; X++)
                {
                    var Pixel = Image.Pixel(X, Y);
                    Samples.Add(Pixel.R);
                    Samples.Add(Pixel.G);
                    Samples.Add(Pixel.B);
                }
            }
            Check(Samples.Count > 1000 && Samples.Count(Value => Value > 45) > 100,
                "The matching retained preview frame contains rendered model pixels");
            return Samples;
        }

        if (!SkeletonOnly && !StaticMeshOnly)
        {
            Open(BasicAssetFixture.StateMachineId);
            Check(!Editor(BasicAssetFixture.StateMachineId)["readOnly"]!.GetValue<bool>(),
                "The project state machine opens writable in its own native host");
            Click("editor.asset-command:add-state");
            Check(Editor(BasicAssetFixture.StateMachineId)["state"]!["stateCount"]!.GetValue<int>() == 3,
                "Clicking Add State commits the structured document transaction");
            Click("editor.asset-command:undo");
            Check(Editor(BasicAssetFixture.StateMachineId)["state"]!["stateCount"]!.GetValue<int>() == 2,
                "The document toolbar Undo restores the state graph");
            Click("stateMachineField-name");
            Key("down", 0x41, Control: true);
            Key("up", 0x41, Control: true);
            foreach (var Character in "Authored controller")
            {
                Key("text", 0, Character.ToString());
            }
            Key("down", 0x0D);
            Key("up", 0x0D);
            Check(Editor(BasicAssetFixture.StateMachineId)["state"]!["document"]!["name"]!.GetValue<string>() == "Authored controller",
                "Continuous native typing preserves focus and commits every character through the shared action");
            Click("editor.asset-save");
            Check(File.ReadAllText(Path.Combine(ProjectRoot, "Content", "Media", "Controller.json"))
                    .Contains("Authored controller", StringComparison.Ordinal) &&
                !Editor(BasicAssetFixture.StateMachineId)["dirty"]!.GetValue<bool>(),
                "Clicking Save writes the validated source and clears dirty state");
            JsonObject StateMachine() => Editor(BasicAssetFixture.StateMachineId)["state"]!.AsObject();
            Reveal("stateMachineField-transition-0-blendDuration", "stateMachineAuthoring");
            Click("stateMachineField-transition-0-blendDuration");
            Key("down", 0x41, Control: true);
            Key("up", 0x41, Control: true);
            Key("text", 0, "1");
            Key("down", 0x0D);
            Key("up", 0x0D);
            Reveal("stateMachineCurve-0", "stateMachineAuthoring");
            Click("stateMachineCurve-0");
            Check(StateMachine()["document"]!["transitions"]![0]!["blendDuration"]!.GetValue<double>() == 1 &&
                StateMachine()["document"]!["transitions"]![0]!["blendCurve"]!.GetValue<string>() == "SmoothStep",
                "Native duration typing and curve toggle author the actual transition settings");
            var BeforePreview = StateMachine()["document"]!.ToJsonString();
            var BeforePreviewRevision = StateMachine()["documentRevision"]!.GetValue<ulong>();
            Click("stateMachinePreviewParameter-Speed");
            Key("down", 0x41, Control: true);
            Key("up", 0x41, Control: true);
            Key("text", 0, "1");
            Key("down", 0x0D);
            Key("up", 0x0D);
            Check(StateMachine()["preview"]!["parameters"]!["Speed"]!.GetValue<double>() == 1,
                "Real typing updates transient preview parameters");
            var BeforePixels = CaptureStateMachineViewport("state-machine-before");
            Click("editor.asset-command:step");
            Check(StateMachine()["preview"]!["playback"]!["stateId"]!.GetValue<string>() == "Walk" &&
                StateMachine()["preview"]!["playback"]!["blending"]!.GetValue<bool>(),
                "A real Step click triggers the condition-driven state transition");
            Click("editor.asset-command:step", 15);
            var Weight = StateMachine()["preview"]!["playback"]!["blendWeight"]!.GetValue<double>();
            Check(Weight > 0.15 && Weight < 0.17 && !StateMachine()["playing"]!.GetValue<bool>() &&
                StateMachine()["preview"]!["pose"]!["bones"]!.AsArray().Count > 0,
                "Repeated native Step clicks produce a paused intermediate blended pose");
            Capture("state-machine-blend", "stateMachineBlendFill", false);
            var BlendedPixels = CaptureStateMachineViewport("state-machine-pose");
            Check(BeforePixels.Count == BlendedPixels.Count &&
                BeforePixels.Zip(BlendedPixels).Count(Pair => Pair.First != Pair.Second) > 25,
                "Native stepping changes the rendered skinned model inside the fixed preview camera");
            Check(StateMachine()["document"]!.ToJsonString() == BeforePreview &&
                StateMachine()["documentRevision"]!.GetValue<ulong>() == BeforePreviewRevision,
                "Preview interaction leaves the authoring document and revision unchanged");
            Click("editor.asset-command:play");
            Check(StateMachine()["playing"]!.GetValue<bool>(), "Native Play starts the preview");
            Click("editor.asset-command:pause");
            Check(!StateMachine()["playing"]!.GetValue<bool>(), "Native Pause stops advancing the preview");
            Click("editor.asset-command:stop");
            Check(StateMachine()["preview"]!["playback"]!["stateId"]!.GetValue<string>() == "Idle" &&
                !StateMachine()["preview"]!["playback"]!["blending"]!.GetValue<bool>(),
                "Native Reset returns to the initial state and clears the blend");
            Reveal("editor.state-machine-preview:state:Walk", "animationStateMachineGraph");
            Click("editor.state-machine-preview:state:Walk");
            Check(StateMachine()["preview"]!["playback"]!["blending"]!.GetValue<bool>(),
                "A native state-switch click starts the declared blend while paused");
            Click("editor.asset-command:stop");
            Reveal("editor.state-machine-preview:transition:StartWalking", "animationStateMachineGraph");
            Click("editor.state-machine-preview:transition:StartWalking");
            Check(StateMachine()["preview"]!["playback"]!["transitionId"]!.GetValue<string>() == "StartWalking",
                "The native Test Transition button starts the selected edge");
            Click("editor.asset-command:stop");
            Click("editor.asset-command:undo");
            Click("editor.asset-command:undo");
            Check(!StateMachine()["dirty"]!.GetValue<bool>(), "Undo still owns the authored duration after preview interaction");
            if (StateMachineOnly)
            {
                var ProjectPreview = StateMachine()["preview"]!["playback"]!.ToJsonString();
                var PresetId = "engine://animation-state-machines/uefn-mannequin-locomotion";
                Open(PresetId);
                Click("stateMachinePreviewParameter-speed");
                Key("down", 0x41, Control: true);
                Key("up", 0x41, Control: true);
                Key("text", 0, "2");
                Key("down", 0x0D);
                Key("up", 0x0D);
                Click("editor.asset-command:step");
                Check(Editor(PresetId)["state"]!["preview"]!["playback"]!["blending"]!.GetValue<bool>() &&
                    Editor(PresetId)["readOnly"]!.GetValue<bool>() && !Editor(PresetId)["dirty"]!.GetValue<bool>(),
                    "Read-only presets permit native parameter preview while keeping authoring clean");
                Check(StateMachine()["preview"]!["playback"]!.ToJsonString() == ProjectPreview,
                    "Two state-machine windows retain independent preview clocks and parameters");
                return;
            }
        }

        var SocketAssetId = StaticMeshOnly ? BasicAssetFixture.StaticMeshId : BasicAssetFixture.SkeletonId;
        var SocketViewportId = StaticMeshOnly ? "meshPreviewViewport" : "skeletonPreviewViewport";
        Open(SocketAssetId);
        if (!StaticMeshOnly)
        {
            Click("editor.skeleton:select:bone:hand");
        }
        else
        {
            var Layout = Editor(SocketAssetId)["main"]!.ToJsonString();
            Check(!Layout.Contains("\"source\"", StringComparison.Ordinal), "Mesh editor layout contains no Source pane");
            Click("meshSocketRoot");
            var RootState = Editor(SocketAssetId)["state"]!;
            Check(RootState["hierarchy"]!["selectedKind"]!.GetValue<string>() == "mesh" &&
                !RootState["hierarchy"]!["root"]!["editable"]!.GetValue<bool>() &&
                !RootState["gizmo"]!["editable"]!.GetValue<bool>() && RootState["gizmo"]!["handles"]!.AsArray().Count == 0,
                "Native Mesh root selection exposes an immutable root without transform handles");
            var AccessUi = Observe();
            foreach (var FieldId in new[]
            {
                "meshAssetId", "meshVertexCount", "meshTriangleCount",
            })
            {
                var ReadOnlyField = Find(AccessUi, FieldId);
                Check(!ReadOnlyField["enabled"]!.GetValue<bool>() && !ReadOnlyField["focusable"]!.GetValue<bool>() &&
                    !ReadOnlyField["hitTarget"]!.GetValue<bool>() && ReadOnlyField["actions"]!.AsArray().Count == 0,
                    "Derived metadata uses the shared disabled field with no editing action: " + FieldId);
            }
            Check(Find(AccessUi, "meshDefaultMaterial")["enabled"]!.GetValue<bool>() &&
                Find(AccessUi, "meshDefaultMaterial")["focusable"]!.GetValue<bool>(),
                "Editable authoring fields remain enabled beside disabled metadata in the same asset");
        }
        Click("editor.asset-command:add-socket");
        JsonObject RigState() => Editor(SocketAssetId)["state"]!.AsObject();
        Check(StaticMeshOnly ? RigState()["selectedSocketId"] is not null :
            RigState()["selectedKind"]!.GetValue<string>() == "socket" &&
            RigState()["document"]!["sockets"]![0]!["parentBoneId"]!.GetValue<string>() == "hand",
            "Native Add Socket uses the bone selected in the tree");
        void EditRigField(string Field, string Value)
        {
            if (Field is "position" or "rotation" or "scale")
            {
                var Values = Value.Split(',', StringSplitOptions.TrimEntries);
                for (var Axis = 0; Axis < 3; Axis++)
                {
                    EditRigField(Field + "-" + "XYZ"[Axis], Values[Axis]);
                }
                return;
            }
            var FieldId = (StaticMeshOnly ? "meshSocketField-" : "skeletonField-") + Field;
            Click(FieldId);
            var FocusUi = Observe();
            var ExpectedFocus = Find(FocusUi, FieldId)["elementId"]!.GetValue<string>();
            var BeforeTyping = RigState()["document"]!.ToJsonString();
            var TypingRevision = RigState()["documentRevision"]!.GetValue<ulong>();
            Check(Host(FocusUi)["focusedElementId"]?.GetValue<string>() == ExpectedFocus,
                "Native text input owns focus before typing: " + FieldId);
            Key("down", 0x41, Control: true);
            Key("up", 0x41, Control: true);
            foreach (var Character in Value)
            {
                Key("text", 0, Character.ToString());
            }
            Check(Host(Observe())["focusedElementId"]?.GetValue<string>() == ExpectedFocus,
                "Native text input retains focus through the entire value: " + FieldId);
            Check(RigState()["document"]!.ToJsonString() == BeforeTyping &&
                RigState()["documentRevision"]!.GetValue<ulong>() == TypingRevision,
                "Shared value inputs keep a draft until Enter or blur: " + FieldId);
            Key("down", 0x0D);
            Key("up", 0x0D);
        }
        EditRigField("name", "Weapon");
        EditRigField("position", "0.2, 0.1, 0");
        Check(RigState()["document"]!["sockets"]![0]!["name"]!.GetValue<string>() == "Weapon" &&
            Math.Abs(RigState()["document"]!["sockets"]![0]!["position"]![0]!.GetValue<double>() - 0.2) < 0.0001,
            "Socket name and transform commit through real native typing");
        if (StaticMeshOnly)
        {
            var BeforeCancel = RigState()["document"]!.ToJsonString();
            var BeforeCancelRevision = RigState()["documentRevision"]!.GetValue<ulong>();
            Click("meshSocketField-position-X");
            Key("down", 0x41, Control: true);
            Key("up", 0x41, Control: true);
            Key("text", 0, "-");
            Check(RigState()["documentRevision"]!.GetValue<ulong>() == BeforeCancelRevision,
                "An intermediate minus sign remains draft text without a rejected authoring mutation");
            Key("down", 0x1B);
            Key("up", 0x1B);
            Check(RigState()["document"]!.ToJsonString() == BeforeCancel && RigState()["documentRevision"]!.GetValue<ulong>() == BeforeCancelRevision,
                "Escape cancels a numeric draft without a transaction or changed sibling components");
            EditRigField("position-X", "0.35");
            Check(RigState()["documentRevision"]!.GetValue<ulong>() == BeforeCancelRevision + 1 &&
                Math.Abs(RigState()["document"]!["sockets"]![0]!["position"]![1]!.GetValue<double>() - 0.1) < 0.0001 &&
                RigState()["document"]!["sockets"]![0]!["position"]![2]!.GetValue<double>() == 0,
                "A committed X edit changes exactly one component and one document revision");
            Click("editor.asset-command:undo");
            Check(RigState()["document"]!.ToJsonString() == BeforeCancel, "One Undo restores the complete numeric input commit");
        }
        void GizmoPointer(string Phase, int X, int Y, string Button = "left")
        {
            for (var Attempt = 0; Attempt < 10; Attempt++)
            {
                var Ui = Observe();
                var Element = Find(Ui, SocketViewportId);
                var Result = Invoke("ui.pointer", new JsonObject
                {
                    ["hostId"] = HostId,
                    ["event"] = Phase,
                    ["button"] = Button,
                    ["x"] = X,
                    ["y"] = Y,
                    ["buttons"] = new JsonArray(),
                    ["modifiers"] = new JsonArray(),
                    ["pointerId"] = 0,
                    ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                    ["expectedElementId"] = Element["elementId"]!.DeepClone(),
                });
                if (Result["accepted"]?.GetValue<bool>() == true)
                {
                    return;
                }
                if (Result["diagnostics"]?[0]?["code"]?.GetValue<string>() is not ("UI_FRAME_STALE" or "UI_TARGET_CHANGED"))
                {
                    throw new InvalidOperationException(Result.ToJsonString());
                }
            }
            throw new InvalidOperationException("No current native Gizmo target");
        }
        (int X, int Y, int DeltaX, int DeltaY) GizmoPoint(string ToolName)
        {
            var Gizmo = RigState()["gizmo"]!;
            var Bounds = Gizmo["bounds"]!;
            var Handles = Gizmo["handles"]!.AsArray().OrderByDescending(Handle =>
            {
                var Pivot = Handle!["pivot"]!;
                var End = Handle["end"]!;
                return Math.Pow(End[0]!.GetValue<double>() - Pivot[0]!.GetValue<double>(), 2) +
                    Math.Pow(End[1]!.GetValue<double>() - Pivot[1]!.GetValue<double>(), 2);
            });
            foreach (var Handle in Handles)
            {
                var Points = ToolName == "rotate" ? Handle!["path"]!.AsArray().Where(Point => Point is not null) : [Handle!["end"]];
                foreach (var Point in Points)
                {
                    var X = Point![0]!.GetValue<double>();
                    var Y = Point[1]!.GetValue<double>();
                    var DirectionX = X - Handle!["pivot"]![0]!.GetValue<double>();
                    var DirectionY = Y - Handle["pivot"]![1]!.GetValue<double>();
                    var Length = Math.Sqrt(DirectionX * DirectionX + DirectionY * DirectionY);
                    if (Length < 20)
                    {
                        continue;
                    }
                    DirectionX /= Length;
                    DirectionY /= Length;
                    var DeltaX = (int)Math.Round((ToolName == "rotate" ? DirectionY : DirectionX) * 32);
                    var DeltaY = (int)Math.Round((ToolName == "rotate" ? -DirectionX : DirectionY) * 32);
                    if (X < 8 || Y < 8 || X + DeltaX < 8 || Y + DeltaY < 8 ||
                        X + Math.Max(0, DeltaX) > Bounds["width"]!.GetValue<double>() - 8 ||
                        Y + Math.Max(0, DeltaY) > Bounds["height"]!.GetValue<double>() - 8)
                    {
                        continue;
                    }
                    return ((int)Math.Round(X + Bounds["x"]!.GetValue<double>()),
                        (int)Math.Round(Y + Bounds["y"]!.GetValue<double>()), DeltaX, DeltaY);
                }
            }
            throw new InvalidOperationException("No visible Gizmo handle: " + Gizmo.ToJsonString());
        }
        void CaptureGizmo(string ToolName, string CapturePrefix = "")
        {
            var State = RigState();
            var Gizmo = State["gizmo"]!;
            var Ui = Observe();
            var Frame = Call(Sequence++, "resources/read", new JsonObject
            {
                ["uri"] = $"sharpengine://ui/frames/{Ui["frameId"]}/{HostId}.png",
            });
            var Bytes = Convert.FromBase64String(Frame["result"]!["contents"]![0]!["blob"]!.GetValue<string>());
            var Name = (StaticMeshOnly ? "staticmesh-gizmo-" : "skeleton-gizmo-") + CapturePrefix + ToolName;
            File.WriteAllBytes(Path.Combine(Output, Name + ".png"), Bytes);
            File.WriteAllText(Path.Combine(Output, Name + ".json"), new JsonObject
            {
                ["ui"] = Ui.DeepClone(),
                ["editorState"] = State.DeepClone(),
            }.ToJsonString());
            var Pixels = DecodeFrame(Bytes);
            var Matched = 0;
            foreach (var Handle in Gizmo["handles"]!.AsArray())
            {
                var PathValue = Handle!["path"]!.AsArray();
                var Axis = Handle["axis"]!.GetValue<string>();
                for (var Index = 1; Index < PathValue.Count; Index++)
                {
                    if (PathValue[Index - 1] is null || PathValue[Index] is null)
                    {
                        continue;
                    }
                    for (var Step = 0; Step < 10; Step++)
                    {
                        var T = Step / 10.0;
                        var X = (int)(Gizmo["bounds"]!["x"]!.GetValue<double>() +
                            PathValue[Index - 1]![0]!.GetValue<double>() * (1 - T) + PathValue[Index]![0]!.GetValue<double>() * T);
                        var Y = (int)(Gizmo["bounds"]!["y"]!.GetValue<double>() +
                            PathValue[Index - 1]![1]!.GetValue<double>() * (1 - T) + PathValue[Index]![1]!.GetValue<double>() * T);
                        var FoundColor = false;
                        for (var OffsetY = -2; OffsetY <= 2; OffsetY++)
                        {
                            for (var OffsetX = -2; OffsetX <= 2; OffsetX++)
                            {
                                if (X + OffsetX < 0 || Y + OffsetY < 0 || X + OffsetX >= Pixels.Width || Y + OffsetY >= Pixels.Height)
                                {
                                    continue;
                                }
                                var Pixel = Pixels.Pixel(X + OffsetX, Y + OffsetY);
                                FoundColor |= Axis switch
                                {
                                    "X" => Pixel.R > 180 && Pixel.G < 110 && Pixel.B < 110,
                                    "Y" => Pixel.G > 170 && Pixel.R < 120 && Pixel.B < 150,
                                    _ => Pixel.B > 190 && Pixel.R < 130 && Pixel.G > 100,
                                };
                            }
                        }
                        Matched += FoundColor ? 1 : 0;
                    }
                }
            }
            Check(Matched >= (ToolName == "rotate" ? 80 : 15),
                ToolName + " Gizmo pixels match its advertised handle paths in the retained frame");
        }
        var SocketId = RigState()[StaticMeshOnly ? "selectedSocketId" : "selectedId"]!.GetValue<string>();
        var SelectionDocument = RigState()["document"]!.ToJsonString();
        var SelectionRevision = RigState()["documentRevision"]!.GetValue<ulong>();
        Click(StaticMeshOnly ? "meshSocketRoot" : "editor.skeleton:select:bone:hand");
        Check(!RigState()["gizmo"]!["editable"]!.GetValue<bool>() &&
            RigState()["gizmo"]!["handles"]!.AsArray().Count == 0,
            "Changing native selection away from the Socket hides every manipulation handle");
        Capture(StaticMeshOnly ? "staticmesh-selection-hidden" : "skeleton-selection-hidden", SocketViewportId, null);
        Click((StaticMeshOnly ? "editor.mesh-socket:select:" : "editor.skeleton:select:socket:") + SocketId);
        Check(RigState()["gizmo"]!["handles"]!.AsArray().Count == 3 &&
            RigState()["document"]!.ToJsonString() == SelectionDocument &&
            RigState()["documentRevision"]!.GetValue<ulong>() == SelectionRevision,
            "Selecting the Socket again restores only its handles without an authoring transaction");
        CaptureGizmo("selected");
        foreach (var ToolName in new[]
        {
            "move", "rotate", "scale",
        })
        {
            Click("editor.asset-command:" + ToolName);
            Check(RigState()["gizmo"]!["tool"]!.GetValue<string>() == ToolName, "Native toolbar selects Socket " + ToolName);
            CaptureGizmo(ToolName);
            var Before = RigState()["document"]!.ToJsonString();
            var RevisionBefore = RigState()["documentRevision"]!.GetValue<ulong>();
            var CameraBefore = RigState()["viewport"]!.ToJsonString();
            var Point = GizmoPoint(ToolName);
            GizmoPointer("down", Point.X, Point.Y);
            Check(RigState()["gizmo"]!["dragging"]!.GetValue<bool>() && Host(Observe())["pointerCapture"] is not null,
                "Native " + ToolName + " begins a captured Socket gesture");
            GizmoPointer("move", Point.X + Point.DeltaX / 2, Point.Y + Point.DeltaY / 2);
            GizmoPointer("move", Point.X + Point.DeltaX, Point.Y + Point.DeltaY);
            Check(RigState()["gizmo"]!["previewTransform"] is not null &&
                RigState()["documentRevision"]!.GetValue<ulong>() == RevisionBefore &&
                RigState()["document"]!.ToJsonString() == Before, "Dragging publishes a validated transient preview without committing per-move history");
            GizmoPointer("up", Point.X + Point.DeltaX, Point.Y + Point.DeltaY);
            var After = RigState()["document"]!.ToJsonString();
            Check(After != Before && !RigState()["gizmo"]!["dragging"]!.GetValue<bool>() &&
                RigState()["documentRevision"]!.GetValue<ulong>() == RevisionBefore + 1 &&
                RigState()["viewport"]!.ToJsonString() == CameraBefore,
                "Native " + ToolName + " commits once and preserves the preview camera");
            Click("editor.asset-command:undo");
            Check(RigState()["document"]!.ToJsonString() == Before, "One Undo restores the entire " + ToolName + " gesture");
            Click("editor.asset-command:redo");
            Check(RigState()["document"]!.ToJsonString() == After, "One Redo restores the entire " + ToolName + " gesture");
        }
        foreach (var CancelKind in new[]
        {
            "escape", "capture",
        })
        {
            var Before = RigState()["document"]!.ToJsonString();
            var RevisionBefore = RigState()["documentRevision"]!.GetValue<ulong>();
            var Point = GizmoPoint("scale");
            GizmoPointer("down", Point.X, Point.Y);
            GizmoPointer("move", Point.X + Point.DeltaX, Point.Y + Point.DeltaY);
            Check(RigState()["gizmo"]!["dragging"]!.GetValue<bool>(), "Cancellation fixture has an active gesture");
            if (CancelKind == "escape")
            {
                Key("down", 0x1B);
                Key("up", 0x1B);
                GizmoPointer("up", Point.X + Point.DeltaX, Point.Y + Point.DeltaY);
            }
            else
            {
                GizmoPointer("cancel", Point.X + Point.DeltaX, Point.Y + Point.DeltaY);
            }
            Check(!RigState()["gizmo"]!["dragging"]!.GetValue<bool>() &&
                RigState()["documentRevision"]!.GetValue<ulong>() == RevisionBefore &&
                RigState()["document"]!.ToJsonString() == Before &&
                RigState()["gizmo"]!["previewTransform"] is null && Host(Observe())["pointerCapture"] is null,
                CancelKind + " cancels the transient gesture without changing document or history");
        }
        var NoOpBefore = RigState()["document"]!.ToJsonString();
        var NoOpRevision = RigState()["documentRevision"]!.GetValue<ulong>();
        var NoOpPoint = GizmoPoint("scale");
        GizmoPointer("down", NoOpPoint.X, NoOpPoint.Y);
        GizmoPointer("up", NoOpPoint.X, NoOpPoint.Y);
        var ViewBounds = RigState()["gizmo"]!["bounds"]!;
        var BlankX = (int)ViewBounds["x"]!.GetValue<double>() + 30;
        var BlankY = (int)(ViewBounds["y"]!.GetValue<double>() + ViewBounds["height"]!.GetValue<double>()) - 30;
        GizmoPointer("down", BlankX, BlankY);
        GizmoPointer("up", BlankX, BlankY);
        Check(RigState()["document"]!.ToJsonString() == NoOpBefore &&
            RigState()["documentRevision"]!.GetValue<ulong>() == NoOpRevision,
            "A no-motion handle click and a blank viewport click create no document history");
        foreach (var Button in new[]
        {
            "right", "middle",
        })
        {
            var BeforeCamera = RigState()["viewport"]!.ToJsonString();
            GizmoPointer("down", BlankX, BlankY, Button);
            GizmoPointer("move", BlankX + 20, BlankY - 10, Button);
            GizmoPointer("up", BlankX + 20, BlankY - 10, Button);
            Check(RigState()["viewport"]!.ToJsonString() != BeforeCamera &&
                RigState()["document"]!.ToJsonString() == NoOpBefore,
                Button + " navigation remains independent of Socket authoring");
        }
        if (StaticMeshOnly)
        {
            Click("editor.asset-command:duplicate");
            Check(RigState()["socketCount"]!.GetValue<int>() == 2 &&
                RigState()["selectedSocketId"]!.GetValue<string>() != SocketId, "Native duplicate creates an independent static Socket");
            Click("editor.asset-command:remove");
            Check(RigState()["socketCount"]!.GetValue<int>() == 1, "Native remove deletes the selected static Socket");
            Click("editor.asset-save");
            var Saved = JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectRoot, "Content", "Media", "Validation.sstaticmesh")))!;
            Check(Saved["schemaVersion"]!.GetValue<int>() == 3 && Saved["sockets"]!.AsArray().Count == 1 &&
                !RigState()["dirty"]!.GetValue<bool>(), "Native Save persists static Socket authoring in schema v3");
            CaptureGizmo("saved");
            Click("editor.asset-command:add-socket");
            var ChildId = RigState()["selectedSocketId"]!.GetValue<string>();
            EditRigField("name", "Child");
            EditRigField("position", "0.15, 0.05, 0");
            Click("editor.asset-command:add-socket");
            var LeafId = RigState()["selectedSocketId"]!.GetValue<string>();
            EditRigField("name", "Tip");
            EditRigField("position", "0.05, 0, 0.05");
            JsonNode SocketNode(string Id) => RigState()["document"]!["sockets"]!.AsArray()
                .Single(Node => Node!["socketId"]!.GetValue<string>() == Id)!;
            JsonNode Model(string Id) => RigState()["socketPose"]!["socketModelMatrices"]![Id]!.DeepClone();
            bool SameModel(JsonNode First, JsonNode Second) => Enumerable.Range(0, 16)
                .All(Index => Math.Abs(First[Index]!.GetValue<double>() - Second[Index]!.GetValue<double>()) < 0.0001);
            Check(SocketNode(ChildId)["parentSocketId"]!.GetValue<string>() == SocketId &&
                SocketNode(LeafId)["parentSocketId"]!.GetValue<string>() == ChildId,
                "Native Add Socket creates child and grandchild parent references");
            var TreeUi = Observe();
            Check(Find(TreeUi, "meshSocketRoot-caption")["bounds"]!["x"]!.GetValue<double>() <
                Find(TreeUi, "meshSocketNode-" + SocketId + "-caption")["bounds"]!["x"]!.GetValue<double>() &&
                Find(TreeUi, "meshSocketNode-" + SocketId + "-caption")["bounds"]!["x"]!.GetValue<double>() <
                Find(TreeUi, "meshSocketNode-" + ChildId + "-caption")["bounds"]!["x"]!.GetValue<double>() &&
                Find(TreeUi, "meshSocketNode-" + ChildId + "-caption")["bounds"]!["x"]!.GetValue<double>() <
                Find(TreeUi, "meshSocketNode-" + LeafId + "-caption")["bounds"]!["x"]!.GetValue<double>(),
                "The retained native tree shows Mesh, Socket, child and grandchild at increasing depths");
            var BeforeAncestor = Model(LeafId);
            var LeafLocal = SocketNode(LeafId).ToJsonString();
            Click("meshSocketNode-" + SocketId);
            var BeforeParentDelete = RigState()["document"]!.ToJsonString();
            var ParentDeleteRevision = RigState()["documentRevision"]!.GetValue<ulong>();
            Check(!Host(Observe())["elements"]!.AsArray().Any(Node => Node!["actions"]!.AsArray()
                .Any(Action => Action!.GetValue<string>() == "editor.asset-command:remove")),
                "Native toolbar does not expose an enabled delete action for a non-leaf Socket");
            Invoke("editor.action", new JsonObject
            {
                ["action"] = $"editor.host-action:{HostId}:editor.mesh-socket:remove",
            });
            Check(RigState()["document"]!.ToJsonString() == BeforeParentDelete &&
                RigState()["documentRevision"]!.GetValue<ulong>() == ParentDeleteRevision,
                "A direct non-leaf delete request cannot bypass the hierarchy guard");
            EditRigField("position", "0.1, 0.15, 0");
            Check(!SameModel(Model(LeafId), BeforeAncestor) && SocketNode(LeafId).ToJsonString() == LeafLocal,
                "Native ancestor editing changes inherited descendant frames while preserving local data");
            Click("meshSocketNode-" + ChildId);
            var BeforeRootReparent = Model(ChildId);
            var BeforeLeafReparent = Model(LeafId);
            Click("meshSocketField-parentSocketId");
            Key("down", 0x26);
            Key("up", 0x26);
            Key("down", 0x0D);
            Key("up", 0x0D);
            Check(SocketNode(ChildId)["parentSocketId"] is null && SameModel(Model(ChildId), BeforeRootReparent) &&
                SameModel(Model(LeafId), BeforeLeafReparent), "Native Parent selector reparents to Mesh without moving the selected subtree");
            Click("meshSocketField-parentSocketId");
            Key("down", 0x28);
            Key("up", 0x28);
            Key("down", 0x0D);
            Key("up", 0x0D);
            Check(SocketNode(ChildId)["parentSocketId"]!.GetValue<string>() == SocketId,
                "Native Parent selector can attach the Socket below another Socket");
            Click("ui.viewport3d:meshPreviewViewport:focus");
            foreach (var ToolName in new[]
            {
                "move", "rotate", "scale",
            })
            {
                Click("editor.asset-command:" + ToolName);
                CaptureGizmo(ToolName, "hierarchy-child-");
                var Before = RigState()["document"]!.ToJsonString();
                var Revision = RigState()["documentRevision"]!.GetValue<ulong>();
                var Point = GizmoPoint(ToolName);
                GizmoPointer("down", Point.X, Point.Y);
                GizmoPointer("move", Point.X + Point.DeltaX, Point.Y + Point.DeltaY);
                Check(RigState()["gizmo"]!["dragging"]!.GetValue<bool>() && RigState()["gizmo"]!["previewPose"]!["socketModelMatrices"]![LeafId] is not null &&
                    RigState()["document"]!.ToJsonString() == Before,
                    "Nested " + ToolName + " publishes a validated descendant preview without editing the document");
                GizmoPointer("up", Point.X + Point.DeltaX, Point.Y + Point.DeltaY);
                var After = RigState()["document"]!.ToJsonString();
                Check(After != Before && RigState()["documentRevision"]!.GetValue<ulong>() == Revision + 1,
                    "Nested " + ToolName + " uses the real parent frame and commits exactly one transaction");
                Click("editor.asset-command:undo");
                Check(RigState()["document"]!.ToJsonString() == Before, "One Undo restores the nested " + ToolName + " gesture");
                Click("editor.asset-command:redo");
            }
            Click("editor.asset-save");
            Saved = JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectRoot, "Content", "Media", "Validation.sstaticmesh")))!;
            Check(Saved["sockets"]!.AsArray().Single(Node => Node!["socketId"]!.GetValue<string>() == LeafId)!["parentSocketId"]!.GetValue<string>() == ChildId,
                "Native Save preserves the complete multi-level hierarchy");
            CaptureGizmo("saved", "hierarchy-");
            var TimingSamples = new JsonArray();
            // Flush the 240-frame ring after the functional RPC and PNG capture workload.
            if (MeasurePerformance)
            {
                Thread.Sleep(30000);
            }
            for (var Sample = 0; MeasurePerformance && Sample < 3; Sample++)
            {
                Thread.Sleep(5000);
                TimingSamples.Add(Invoke("render.describe").DeepClone());
            }
            var FrameAverage = MeasurePerformance ? TimingSamples.Select(Sample => Sample!["timingMilliseconds"]!["frameAverage"]!.GetValue<double>()).Order().ElementAt(1) : 0;
            var FrameP95 = MeasurePerformance ? TimingSamples.Select(Sample => Sample!["timingMilliseconds"]!["frameP95"]!.GetValue<double>()).Order().ElementAt(1) : 0;
            if (MeasurePerformance)
            {
                File.WriteAllText(Path.Combine(Output, "staticmesh-hierarchy-performance.json"), new JsonObject
                {
                    ["frameAverageBudgetMs"] = HierarchyFrameAverageBudgetMs,
                    ["frameP95BudgetMs"] = HierarchyFrameP95BudgetMs,
                    ["frameAverageMs"] = FrameAverage,
                    ["frameP95Ms"] = FrameP95,
                    ["samples"] = TimingSamples,
                    ["hierarchy"] = RigState()["hierarchy"]!.DeepClone(),
                }.ToJsonString());
            }
            var MainHostId = HostId;
            for (var Index = 0; Index < 2; Index++)
            {
                Invoke("editor.action", new JsonObject
                {
                    ["action"] = $"editor.host-action:{MainHostId}:editor.asset-pane:details",
                });
            }
            HostId = Editor(SocketAssetId)["floating"]![0]!["hostId"]!.GetValue<string>();
            EditRigField("name", "FloatingWeapon");
            Check(SocketNode(ChildId)["name"]!.GetValue<string>() == "FloatingWeapon",
                "Floating Details measures its own native host and accepts real Socket typing");
            var BeforeFloatingUndo = RigState()["document"]!.ToJsonString();
            HostId = MainHostId;
            Click("editor.asset-command:undo");
            Check(RigState()["document"]!.ToJsonString() != BeforeFloatingUndo,
                "A floating Socket edit joins the owning asset Undo history");
            Open("engine://materials/default");
            var ReadOnlyMaterial = Editor("engine://materials/default")["state"]!.DeepClone();
            var ReadOnlyUi = Observe();
            foreach (var Axis in new[]
            {
                "r", "g", "b", "a",
            })
            {
                var ReadOnlyField = Find(ReadOnlyUi, "material-color-" + Axis);
                Check(!ReadOnlyField["enabled"]!.GetValue<bool>() && !ReadOnlyField["focusable"]!.GetValue<bool>() &&
                    ReadOnlyField["actions"]!.AsArray().Count == 0 && ReadOnlyField["value"] is not null,
                    "Read-only RGBA retains its per-axis values with disabled input and no mutation binding: " + Axis);
            }
            var ReadOnlyKeyVerified = false;
            for (var Attempt = 0; Attempt < 10; Attempt++)
            {
                var BeforeReadOnlyKey = Observe();
                var KeyResult = Invoke("ui.key", new JsonObject
                {
                    ["hostId"] = HostId, ["event"] = "text", ["key"] = 0, ["text"] = "999",
                    ["modifiers"] = new JsonArray(), ["expectedFrameId"] = BeforeReadOnlyKey["frameId"]!.DeepClone(),
                });
                if (KeyResult["accepted"]?.GetValue<bool>() == true)
                {
                    ReadOnlyKeyVerified = true;
                    break;
                }
                var KeyDiagnostic = KeyResult["diagnostics"]!.AsArray().FirstOrDefault()?["code"]?.GetValue<string>();
                if (KeyDiagnostic == "UI_FRAME_STALE")
                {
                    continue;
                }
                ReadOnlyKeyVerified = KeyDiagnostic == "UI_TARGET_NOT_INTERACTIVE";
                break;
            }
            Check(ReadOnlyKeyVerified, "The read-only keyboard check used a current native frame");
            Check(Editor("engine://materials/default")["state"]!.ToJsonString() == ReadOnlyMaterial.ToJsonString(),
                "Typing with a read-only asset active cannot change its values, revision, dirty state or history");
            var ReadOnlyFrameUi = Observe();
            var ReadOnlyFrame = Call(Sequence++, "resources/read", new JsonObject
            {
                ["uri"] = $"sharpengine://ui/frames/{ReadOnlyFrameUi["frameId"]}/{HostId}.png",
            });
            File.WriteAllBytes(Path.Combine(Output, "readonly-material.png"),
                Convert.FromBase64String(ReadOnlyFrame["result"]!["contents"]![0]!["blob"]!.GetValue<string>()));
            File.WriteAllText(Path.Combine(Output, "readonly-material.json"), ReadOnlyFrameUi.ToJsonString());
            if (MeasurePerformance)
            {
                Check(FrameAverage <= HierarchyFrameAverageBudgetMs && FrameP95 <= HierarchyFrameP95BudgetMs,
                    $"Nested Socket window frame budget: average={FrameAverage:0.###} ms, P95={FrameP95:0.###} ms");
            }
            return;
        }
        Click("editor.asset-command:add-virtual-bone");
        Check(RigState()["selectedKind"]!.GetValue<string>() == "virtual" &&
            RigState()["document"]!["virtualBones"]![0]!["targetBoneId"]!.GetValue<string>() == "hand",
            "Native Add Virtual Bone retains the selected attachment's target bone");
        EditRigField("name", "Aim");
        Click("skeletonField-targetBoneId");
        Key("down", 0x26);
        Key("up", 0x26);
        Key("down", 0x0D);
        Key("up", 0x0D);
        Check(RigState()["document"]!["virtualBones"]![0]!["targetBoneId"]!.GetValue<string>() == "root",
            "Native target bone dropdown changes the authoritative reference");
        Click("skeletonField-sourceBoneId");
        Key("down", 0x28);
        Key("up", 0x28);
        Key("down", 0x0D);
        Key("up", 0x0D);
        Check(RigState()["document"]!["virtualBones"]![0]!["sourceBoneId"]!.GetValue<string>() == "hand",
            "Native source bone dropdown changes the virtual parent reference");
        var VirtualId = RigState()["selectedId"]!.GetValue<string>();
        Check(RigState()["rigPose"]!["virtualBoneLocalMatrices"]![VirtualId] is not null,
            "Virtual Bone fields drive the shared runtime evaluator");
        Check(!RigState()["gizmo"]!["editable"]!.GetValue<bool>() &&
            RigState()["gizmo"]!["handles"]!.AsArray().Count == 0,
            "Derived virtual bones never expose writable transform handles");
        Capture("skeleton-rig", "skeletonPreviewViewport", false);
        EditRigField("previewMesh", BasicAssetFixture.SkeletonMeshId);
        Check(RigState()["previewMeshAvailable"]!.GetValue<bool>(),
            "Native Preview Mesh selection validates and opens the compatible skeletal geometry");
        Capture("skeleton-rig-mesh", "skeletonPreviewViewport", false);
        Click("editor.skeleton:select:socket:" + SocketId);
        Click("skeletonField-parentBoneId");
        Key("down", 0x28);
        Key("up", 0x28);
        Key("down", 0x0D);
        Key("up", 0x0D);
        Check(RigState()["document"]!["sockets"]![0]!["parentBoneId"]!.GetValue<string>() == VirtualId,
            "The native parent dropdown attaches a Socket to a derived virtual bone");
        Click("editor.asset-command:move");
        CaptureGizmo("mesh");
        var MeshPoint = GizmoPoint("move");
        var BeforeMeshDrag = RigState()["document"]!.ToJsonString();
        GizmoPointer("down", MeshPoint.X, MeshPoint.Y);
        GizmoPointer("move", MeshPoint.X + MeshPoint.DeltaX, MeshPoint.Y + MeshPoint.DeltaY);
        GizmoPointer("up", MeshPoint.X + MeshPoint.DeltaX, MeshPoint.Y + MeshPoint.DeltaY);
        Check(RigState()["document"]!.ToJsonString() != BeforeMeshDrag && RigState()["previewMeshAvailable"]!.GetValue<bool>(),
            "Socket Gizmo also commits under a virtual parent with the compatible preview mesh attached");
        Click("skeletonField-parentBoneId");
        Key("down", 0x26);
        Key("up", 0x26);
        Key("down", 0x0D);
        Key("up", 0x0D);
        Click("editor.skeleton:select:virtual:" + VirtualId);
        Click("skeletonRemove");
        Check(RigState()["document"]!["virtualBones"]!.AsArray().Count == 0, "Native Remove deletes the selected virtual bone");
        Click("editor.asset-command:undo");
        Check(RigState()["document"]!["virtualBones"]!.AsArray().Count == 1, "Native Undo restores virtual bone definitions");
        Click("editor.asset-command:redo");
        Check(RigState()["document"]!["virtualBones"]!.AsArray().Count == 0, "Native Redo reapplies virtual bone deletion");
        Click("editor.asset-save");
        var SavedRig = JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectRoot, "Content", "Media", "Validation.sskeleton")))!;
        Check(SavedRig["schemaVersion"]!.GetValue<int>() == 3 && SavedRig["sockets"]![0]!["name"]!.GetValue<string>() == "Weapon" &&
            !RigState()["dirty"]!.GetValue<bool>(), "Native Save persists the migrated Socket definition");
        if (SkeletonOnly)
        {
            var ClosingRoot = HostId;
            Invoke("editor.action", new JsonObject
            {
                ["action"] = $"editor.host-action:{ClosingRoot}:window.close-requested",
            });
            Check(!Observe()["hosts"]!.AsArray().Any(Node =>
                Node!["hostId"]!.GetValue<string>() == ClosingRoot),
                "Closing the Skeleton editor releases its owned viewport and native host");
            return;
        }

    }
}
