using System.Text.Json.Nodes;

internal static class AssetViewportUiValidation
{
    internal static void Run(string RootId, string ViewportId, Action<bool, string> Check,
        Func<int, string, JsonObject, JsonObject> Tool)
    {
        var Sequence = 32000;
        JsonObject Invoke(string Name, JsonObject? Arguments = null)
        {
            var Response = Tool(Sequence++, Name, Arguments ?? new JsonObject());
            return Response["result"]?["structuredContent"]?.AsObject() ??
                throw new InvalidDataException(Response.ToJsonString());
        }
        JsonArray Editors() { return Invoke("editor.describe")["assetTools"]!["editors"]!.AsArray(); }
        JsonObject State()
        {
            return Editors().Single(Editor => Editor!["rootId"]!.GetValue<string>() == RootId)!["state"]!.AsObject();
        }
        JsonObject Host(JsonObject Ui)
        {
            return Ui["hosts"]!.AsArray().Single(Host => Host!["hostId"]!.GetValue<string>() == RootId)!.AsObject();
        }
        void Pointer(string Event, string Button, int OffsetX = 0, int OffsetY = 0, float WheelY = 0, bool Focus = false)
        {
            for (var Attempt = 0; Attempt < 8; Attempt++)
            {
                var Ui = Invoke("ui.describe");
                var Target = Host(Ui)["elements"]!.AsArray().Single(Element => Focus
                    ? Element!["actions"]!.AsArray().Any(Action =>
                        Action!.GetValue<string>() == "ui.viewport3d:" + ViewportId + ":focus")
                    : Element!["semanticId"]?.GetValue<string>() == ViewportId)!;
                var Bounds = Target["bounds"]!;
                var Result = Invoke("ui.pointer", new JsonObject
                {
                    ["hostId"] = RootId,
                    ["event"] = Event,
                    ["button"] = Button,
                    ["x"] = (int)(Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>() / 2) + OffsetX,
                    ["y"] = (int)(Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>() / 2) + OffsetY,
                    ["buttons"] = Event == "move" && Button != "none" ? new JsonArray(Button) : new JsonArray(),
                    ["modifiers"] = new JsonArray(),
                    ["pointerId"] = 0,
                    ["wheelX"] = 0,
                    ["wheelY"] = WheelY,
                    ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                    ["expectedElementId"] = Target["elementId"]!.DeepClone(),
                });
                if (Result["accepted"]?.GetValue<bool>() == true)
                {
                    return;
                }
                if (Result["diagnostics"]?.AsArray().FirstOrDefault()?["code"]?.GetValue<string>() != "UI_FRAME_STALE")
                {
                    throw new InvalidOperationException(Result.ToJsonString());
                }
            }
            throw new InvalidOperationException("Could not observe a retained viewport input frame");
        }
        var Before = State();
        var OtherStates = Editors().Where(Editor => Editor!["rootId"]!.GetValue<string>() != RootId &&
                Editor["state"]?["viewport"] is not null)
            .ToDictionary(Editor => Editor!["rootId"]!.GetValue<string>(),
                Editor => Editor!["state"]!["viewport"]!.ToJsonString(), StringComparer.Ordinal);
        Pointer("down", "right");
        Pointer("move", "right", 40, 15);
        Pointer("up", "right", 40, 15);
        var Orbit = State();
        Check(Orbit["viewport"]!["yaw"]!.GetValue<double>() > Before["viewport"]!["yaw"]!.GetValue<double>() &&
            Orbit["viewport"]!["pitch"]!.GetValue<double>() < Before["viewport"]!["pitch"]!.GetValue<double>(),
            ViewportId + " uses the shared orbit direction through real pointer input");
        Pointer("wheel", "none", WheelY: -1);
        var Zoomed = State();
        Check(Zoomed["viewport"]!["distance"]!.GetValue<double>() < Orbit["viewport"]!["distance"]!.GetValue<double>(),
            ViewportId + " uses shared wheel zoom");
        Pointer("down", "middle");
        Pointer("move", "middle", 20, 10);
        Pointer("up", "middle", 20, 10);
        var Panned = State();
        Check(Panned["viewport"]!["pivot"]!.ToJsonString() != Zoomed["viewport"]!["pivot"]!.ToJsonString(),
            ViewportId + " uses shared middle-button pan");
        Pointer("down", "left", Focus: true);
        Pointer("up", "left", Focus: true);
        var Focused = State();
        Check(Focused["viewport"]!["pivot"]!.ToJsonString() == Before["viewport"]!["pivot"]!.ToJsonString(),
            ViewportId + " shared Focus button restores the framed target");
        Pointer("down", "right");
        Pointer("cancel", "right");
        Pointer("move", "none", 20, 10);
        Check(State()["viewport"]!.ToJsonString() == Focused["viewport"]!.ToJsonString(),
            ViewportId + " capture cancellation prevents stray camera movement");
        Check(Panned["dirty"]!.ToJsonString() == Before["dirty"]!.ToJsonString() &&
            Panned["documentRevision"]!.ToJsonString() == Before["documentRevision"]!.ToJsonString(),
            ViewportId + " navigation never dirties or revises its authoritative asset");
        Check(Panned["previewWorld"]?.ToJsonString() == Before["previewWorld"]?.ToJsonString(),
            ViewportId + " navigation leaves all World records including authored cameras unchanged");
        Check(Editors().Where(Editor => OtherStates.ContainsKey(Editor!["rootId"]!.GetValue<string>()))
            .All(Editor => Editor!["state"]!["viewport"]!.ToJsonString() ==
                OtherStates[Editor["rootId"]!.GetValue<string>()]),
            ViewportId + " never changes camera state in another editor host");
    }
}
