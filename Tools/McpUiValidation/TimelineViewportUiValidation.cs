using System.Text.Json.Nodes;

internal static class TimelineViewportUiValidation
{
    internal static void Run(string RootId, Action<bool, string> Check,
        Func<int, string, JsonObject, JsonObject> Tool,
        Func<int, string, JsonObject?, JsonObject> Call)
    {
        var Sequence = 26000;
        JsonObject Invoke(string Name, JsonObject? Arguments = null)
        {
            var Response = Tool(Sequence++, Name, Arguments ?? new JsonObject());
            return Response["result"]?["structuredContent"]?.AsObject() ??
                throw new InvalidDataException(Response.ToJsonString());
        }
        JsonObject Read() { return Invoke("ui.describe"); }
        JsonObject Host(JsonObject Ui)
        {
            return Ui["hosts"]!.AsArray().Single(Item => Item!["hostId"]!.GetValue<string>() == RootId)!.AsObject();
        }
        JsonObject Element(JsonObject Ui, string Id)
        {
            return Host(Ui)["elements"]!.AsArray().Single(Item => Item!["semanticId"]?.GetValue<string>() == Id)!.AsObject();
        }
        JsonObject State()
        {
            return Invoke("editor.describe")["assetTools"]!["editors"]!.AsArray()
                .Single(Item => Item!["rootId"]!.GetValue<string>() == RootId)!["state"]!.AsObject();
        }
        void Pointer(string Event, string Id, string Button = "left", float OffsetX = 0, float OffsetY = 0,
            bool Held = false, float WheelY = 0)
        {
            for (var Attempt = 0; Attempt < 6; Attempt++)
            {
                var Ui = Read();
                var Target = Element(Ui, Id);
                var Bounds = Target["bounds"]!;
                var X = (int)Math.Round(Bounds["x"]!.GetValue<double>() + OffsetX);
                var Y = (int)Math.Round(Bounds["y"]!.GetValue<double>() + OffsetY);
                var Response = Invoke("ui.pointer", new JsonObject
                {
                    ["hostId"] = RootId,
                    ["event"] = Event,
                    ["button"] = Button,
                    ["x"] = X,
                    ["y"] = Y,
                    ["buttons"] = Held ? new JsonArray(Button) : new JsonArray(),
                    ["modifiers"] = new JsonArray(),
                    ["pointerId"] = 0,
                    ["wheelX"] = 0,
                    ["wheelY"] = WheelY,
                    ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                    ["expectedElementId"] = Target["elementId"]!.DeepClone(),
                });
                if (Response["accepted"]?.GetValue<bool>() == true)
                {
                    Check(true, Event + " reaches " + Id);
                    return;
                }
                if (Response["diagnostics"]?[0]?["code"]?.GetValue<string>() != "UI_FRAME_STALE")
                {
                    throw new InvalidOperationException(Response.ToJsonString());
                }
            }
            throw new InvalidOperationException("Input frame kept expiring for " + Id);
        }
        void Click(string Id)
        {
            Pointer("down", Id, OffsetX: 8, OffsetY: 8);
            Pointer("up", Id, OffsetX: 8, OffsetY: 8);
        }
        var Evidence = Path.Combine(Directory.GetCurrentDirectory(), "Saved/Validation/TimelineViewport");
        Directory.CreateDirectory(Evidence);
        var InitialUi = Read();
        File.WriteAllText(Path.Combine(Evidence, "initial-ui.json"), InitialUi.ToJsonString());
        var InitialFrame = Call(Sequence++, "resources/read", new JsonObject
        {
            ["uri"] = $"sharpengine://ui/frames/{InitialUi["frameId"]!.GetValue<ulong>()}/{RootId}.png",
        });
        File.WriteAllBytes(Path.Combine(Evidence, "initial.png"),
            Convert.FromBase64String(InitialFrame["result"]!["contents"]![0]!["blob"]!.GetValue<string>()));
        Check(Element(InitialUi, "timelinePlay")["visible"]!.GetValue<bool>() &&
            Element(InitialUi, "timelineRuler")["hitTarget"]!.GetValue<bool>(),
            "Timeline owns visible transport controls and a continuous interactive ruler");
        Check(!Host(InitialUi)["elements"]!.AsArray().Any(Item =>
            Item?["actions"]?.AsArray().Any(Action => Action?.GetValue<string>() == "editor.asset-command:play") == true),
            "Asset editor toolbar does not duplicate timeline playback commands");
        Click("timelinePlay");
        Check(State()["playing"]!.GetValue<bool>(), "Clicking timeline Play starts the timeline clock");
        Pointer("down", "timelineRuler", OffsetX: 198, OffsetY: 20);
        Pointer("move", "timelineRuler", OffsetX: 217, OffsetY: 20, Held: true);
        Pointer("up", "timelineRuler", OffsetX: 227, OffsetY: 20);
        var Scrubbed = State();
        var Scale = Scrubbed["timelineState"]!["pixelsPerSecond"]!.GetValue<double>();
        Check(!Scrubbed["playing"]!.GetValue<bool>() &&
            Math.Abs(Scrubbed["currentTime"]!.GetValue<double>() - 47 / Scale) < 0.02,
            "Real pointer drag continuously positions between ruler divisions and stays paused");
        Thread.Sleep(100);
        Check(State()["currentTime"]!.GetValue<double>() == Scrubbed["currentTime"]!.GetValue<double>(),
            "Releasing a drag never resumes the old playback state");
        Pointer("down", "timelinePlayhead", OffsetX: 0, OffsetY: 3);
        Pointer("move", "timelinePlayhead", OffsetX: 16, OffsetY: 3, Held: true);
        Pointer("up", "timelinePlayhead", OffsetX: 0, OffsetY: 3);
        var ArrowTime = State()["currentTime"]!.GetValue<double>();
        Check(ArrowTime > Scrubbed["currentTime"]!.GetValue<double>() + 0.1,
            "The visible playhead arrow itself can be dragged");
        var BeforeOrbit = State()["viewport"]!["yaw"]!.GetValue<double>();
        var BeforePitch = State()["viewport"]!["pitch"]!.GetValue<double>();
        Pointer("down", "timelinePreviewViewport", "right", 100, 100);
        Pointer("move", "timelinePreviewViewport", "right", 140, 115, true);
        Pointer("up", "timelinePreviewViewport", "right", 140, 115);
        Check(State()["viewport"]!["yaw"]!.GetValue<double>() > BeforeOrbit &&
            State()["viewport"]!["pitch"]!.GetValue<double>() < BeforePitch,
            "Right/down RMB drag orbits right and raises the preview camera like the main viewport");
        var BeforeZoom = State()["viewport"]!["distance"]!.GetValue<double>();
        Pointer("wheel", "timelinePreviewViewport", OffsetX: 100, OffsetY: 100, WheelY: -1);
        Check(State()["viewport"]!["distance"]!.GetValue<double>() < BeforeZoom,
            "The preview viewport supports wheel zoom");
        var Final = State();
        Check(Final["notifyPreview"]!.GetValue<string>() == "ignored" &&
            Final["previewWorld"]!["world"]!["entities"]!.AsArray()
                .Any(Entity => Entity!["components"]!.AsArray().Any(Component =>
                    Component!["typeId"]!.GetValue<string>() == "engine.sky-light")),
            "Animation preview ignores Notify execution and contains a real Sky Light");
        File.WriteAllText(Path.Combine(Evidence, "state.json"), Final.ToJsonString());
        var FrameUi = Read();
        File.WriteAllText(Path.Combine(Evidence, "ui.json"), FrameUi.ToJsonString());
        var Frame = Call(Sequence++, "resources/read", new JsonObject
        {
            ["uri"] = $"sharpengine://ui/frames/{FrameUi["frameId"]!.GetValue<ulong>()}/{RootId}.png",
        });
        File.WriteAllBytes(Path.Combine(Evidence, "timeline-viewport.png"),
            Convert.FromBase64String(Frame["result"]!["contents"]![0]!["blob"]!.GetValue<string>()));
        Click("timelineStop");
        Check(State()["currentTime"]!.GetValue<double>() == 0, "Timeline Stop resets the preview through its time event");
    }
}
