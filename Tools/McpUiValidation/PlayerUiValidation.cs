using System.Numerics;
using System.Text.Json.Nodes;

internal static class PlayerUiValidation
{
    private const string CurrentWorldId = "Content/Worlds/Main.sworld";

    /// <summary>Rejects a supplied project whose startup World is not the current engine
    /// content. The Player assertions describe the built-in Prefab and World, so an older
    /// project snapshot would otherwise validate a contract the engine no longer has.</summary>
    internal static void RequireCurrentWorld(string Root, string ProjectManifest)
    {
        var EngineWorld = Path.GetFullPath(Path.Combine(Root, CurrentWorldId));
        var ProjectRoot = Path.GetDirectoryName(Path.GetFullPath(ProjectManifest))!;
        var Startup = JsonNode.Parse(File.ReadAllText(ProjectManifest))?["world"]?["startup"]
            ?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(Startup))
        {
            throw new InvalidOperationException(
                "PLAYER_FIXTURE_STALE: the supplied project declares no startup World");
        }
        var ProjectWorld = Path.GetFullPath(Path.Combine(ProjectRoot, Startup));
        var EngineHash = File.Exists(EngineWorld) ? FileHash(EngineWorld) : "missing";
        var ProjectHash = File.Exists(ProjectWorld) ? FileHash(ProjectWorld) : "missing";
        if (!string.Equals(EngineHash, ProjectHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PLAYER_FIXTURE_STALE: " + ProjectManifest + " uses a World snapshot that no longer " +
                "matches " + EngineWorld + " (engine=" + EngineHash[..8] + " fixture=" +
                ProjectHash[..Math.Min(8, ProjectHash.Length)] + "). Delete the fixture or unset " +
                "SHARPENGINE_VALIDATION_PROJECT so --player-only builds it from current content.");
        }
    }

    private static string FileHash(string PathValue)
    {
        using var Stream = File.OpenRead(PathValue);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Stream));
    }

    internal static void Run(string Root, Action<bool, string> Check,
        Func<int, string, JsonObject, JsonObject> Tool,
        Func<int, string, JsonObject?, JsonObject> Call)
    {
        var Sequence = 18000;
        var Evidence = Path.Combine(Root, "Saved/Validation/Player", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Evidence);
        JsonObject Invoke(string Name, JsonObject? Arguments = null)
        {
            var Response = Tool(Sequence++, Name, Arguments ?? new JsonObject());
            return Response["result"]?["structuredContent"]?.AsObject() ??
                throw new InvalidDataException(Response.ToJsonString());
        }
        void Save(string Name, JsonObject Value)
        {
            File.WriteAllText(Path.Combine(Evidence, Name + ".json"), Value.ToJsonString());
        }
        JsonObject Read() { return Invoke("ui.describe"); }
        JsonObject Main(JsonObject Ui)
        {
            return Ui["hosts"]!.AsArray().Single(Host => Host!["hostId"]!.GetValue<string>() == "main")!.AsObject();
        }
        JsonObject Element(JsonObject Ui, string Id)
        {
            return Main(Ui)["elements"]!.AsArray().Single(Item =>
                Item!["semanticId"]?.GetValue<string>() == Id)!.AsObject();
        }
        JsonObject Pointer(string Event, string Id, int OffsetX = 0, int OffsetY = 0, int Attempt = 0)
        {
            var Ui = Read();
            var Target = Element(Ui, Id);
            var Bounds = Target["bounds"]!;
            var Response = Invoke("ui.pointer", new JsonObject
            {
                ["hostId"] = "main",
                ["event"] = Event,
                ["button"] = Event == "move" ? "none" : "left",
                ["x"] = (int)(Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>() / 2) + OffsetX,
                ["y"] = (int)(Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>() / 2) + OffsetY,
                ["buttons"] = new JsonArray(),
                ["modifiers"] = new JsonArray(),
                ["pointerId"] = 0,
                ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                ["expectedElementId"] = Target["elementId"]!.DeepClone(),
            });
            if (Response["accepted"]?.GetValue<bool>() != true && Response["diagnostics"]?[0]?["code"]?.GetValue<string>() == "UI_FRAME_STALE" && Attempt < 5)
            {
                return Pointer(Event, Id, OffsetX, OffsetY, Attempt + 1);
            }
            Check(Response["accepted"]?.GetValue<bool>() == true, $"{Event} reaches {Id}: {Response}");
            return Response;
        }
        void Click(string Id)
        {
            Pointer("down", Id);
            Pointer("up", Id);
        }
        void Key(string Event, uint Code, bool Shift = false, int Attempt = 0)
        {
            var Ui = Read();
            var Response = Invoke("ui.key", new JsonObject
            {
                ["hostId"] = "main",
                ["event"] = Event,
                ["key"] = Code,
                ["modifiers"] = Shift ? new JsonArray("shift") : new JsonArray(),
                ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
            });
            if (Response["accepted"]?.GetValue<bool>() != true && Response["diagnostics"]?[0]?["code"]?.GetValue<string>() == "UI_FRAME_STALE" && Attempt < 5)
            {
                Key(Event, Code, Shift, Attempt + 1);
                return;
            }
            Check(Response["accepted"]?.GetValue<bool>() == true, $"Key {Code} {Event}: {Response}");
        }
        JsonObject Components() { return Invoke("components.describe"); }
        JsonObject Locomotion(JsonObject Snapshot)
        {
            return Snapshot["animation"]!["animators"]!.AsArray().Single(Animator =>
                Animator!["componentId"]!.GetValue<string>() == "player-animator")!.AsObject();
        }
        void CheckLocomotion(JsonObject Snapshot, string Expected)
        {
            var Animator = Locomotion(Snapshot);
            Check(Animator["controllerId"]?.GetValue<string>() == "engine://animation-state-machines/uefn-mannequin-locomotion" &&
                Animator["stateId"]?.GetValue<string>() == Expected && Animator["code"]?.GetValue<string>() == "ANIMATOR_READY" &&
                Animator["clipId"]?.GetValue<string>() == "engine://animations/uefn-mannequin-" + Expected,
                "The actual Player uses the locomotion graph and samples its " + Expected + " clip");
            Save("locomotion-" + Expected, Animator);
        }
        JsonObject State(JsonObject Snapshot, string Id)
        {
            return Snapshot["document"]!["entities"]!.AsArray().SelectMany(Entity =>
                Entity!["components"]!.AsArray()).Single(Component =>
                Component!["componentId"]!.GetValue<string>() == Id)!["state"]!.AsObject();
        }
        Vector3 Rotation(JsonObject Snapshot, string Id)
        {
            var Values = State(Snapshot, Id)["Rotation"]!;
            return new(Values[0]!.GetValue<float>(), Values[1]!.GetValue<float>(), Values[2]!.GetValue<float>());
        }
        Vector3 Forward(JsonObject Snapshot)
        {
            var Angles = Rotation(Snapshot, "player-transform") * (MathF.PI / 180);
            return Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(Angles.Y, Angles.X, Angles.Z));
        }
        JsonObject AwaitFacing(Vector3 Expected)
        {
            for (var Attempt = 0; Attempt < 30; Attempt++)
            {
                var Snapshot = Components();
                if (Vector3.Dot(Forward(Snapshot), Expected) > 0.999f)
                {
                    return Snapshot;
                }
                Thread.Sleep(20);
            }
            throw new InvalidOperationException("The Player root did not turn toward the camera-relative input direction");
        }
        Vector3 Position(JsonObject Snapshot)
        {
            var Values = State(Snapshot, "player-transform")["Position"]!;
            return new(Values[0]!.GetValue<float>(), Values[1]!.GetValue<float>(), Values[2]!.GetValue<float>());
        }
        bool Captured()
        {
            return Invoke("editor.describe")["playInputCapture"]!["active"]!.GetValue<bool>();
        }
        void AwaitCapture(bool Expected)
        {
            for (var Attempt = 0; Attempt < 30; Attempt++)
            {
                if (Captured() == Expected)
                {
                    return;
                }
                Thread.Sleep(30);
            }
            Save("capture-failure", Invoke("editor.describe"));
            Save("capture-failure-ui", Read());
            throw new InvalidOperationException($"Expected gameplay capture {Expected}");
        }

        Save("attached-tools", Call(Sequence++, "tools/list", null));
        Save("resource-templates", Call(Sequence++, "resources/templates/list", null));
        var Authored = Components();
        Save("before", Authored);
        Check(State(Authored, "player-controller")["FaceMoveDirection"]?.GetValue<bool>() == true,
            "PLAYER_FIXTURE_STALE: the Player validation requires a World authored with " +
            "FaceMoveDirection=true; rebuild the fixture from current engine content");
        Click("toolbar-tool-play-pause");
        AwaitCapture(true);
        var Focus = Read();
        Save("play-focus-ui", Focus);
        Check(Main(Focus)["focusedElementId"]?.GetValue<string>() == "host/main/id/viewportCanvas",
            "Play immediately focuses the viewport");
        var InitialFrame = Focus["frameId"]!.GetValue<ulong>();
        var InitialPng = Call(Sequence++, "resources/read", new JsonObject
        {
            ["uri"] = $"sharpengine://ui/frames/{InitialFrame}/main.png",
        });
        File.WriteAllBytes(Path.Combine(Evidence, "play-default.png"), Convert.FromBase64String(
            InitialPng["result"]!["contents"]![0]!["blob"]!.GetValue<string>()));
        var Initial = Components();
        CheckLocomotion(Initial, "idle");
        var Before = Position(Initial);
        Key("down", 'W');
        Thread.Sleep(250);
        var Moving = Components();
        Key("up", 'W');
        Save("moving", Moving);
        CheckLocomotion(Moving, "run");
        Check(Position(Moving).Z < Before.Z - 0.05f, "W moves immediately after Play without clicking the viewport");
        var StoppedMoving = Components();
        for (var Attempt = 0; Attempt < 30 && Locomotion(StoppedMoving)["stateId"]?.GetValue<string>() != "idle"; Attempt++)
        {
            Thread.Sleep(20);
            StoppedMoving = Components();
        }
        CheckLocomotion(StoppedMoving, "idle");
        var BeforeLookRotation = Rotation(StoppedMoving, "player-transform");
        var BeforeModelRotation = Rotation(StoppedMoving, "player-model-transform");
        Pointer("move", "viewportCanvas");
        Pointer("move", "viewportCanvas", 100, 30);
        Thread.Sleep(80);
        var Look = Components();
        Save("look", Look);
        Check(Math.Abs(State(Look, "player-third-person-camera")["Yaw"]!.GetValue<float>()) > 1,
            "mouse movement changes the third person camera yaw");
        var Yaw = State(Look, "player-third-person-camera")["Yaw"]!.GetValue<float>();
        Thread.Sleep(80);
        var Looking = Components();
        Check(Vector3.Distance(Rotation(Looking, "player-transform"), BeforeLookRotation) < 0.001f &&
            Vector3.Distance(Rotation(Looking, "player-model-transform"), BeforeModelRotation) < 0.001f,
            "Looking around while standing leaves character and model rotations unchanged");
        Key("down", 'D');
        Thread.Sleep(250);
        var CameraRight = Vector3.Transform(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Yaw * MathF.PI / 180));
        var TurnedMesh = AwaitFacing(CameraRight);
        Key("up", 'D');
        Check(Vector3.Dot(Forward(TurnedMesh), CameraRight) > 0.999f &&
            Vector3.Distance(Rotation(TurnedMesh, "player-model-transform"), BeforeModelRotation) < 0.001f,
            "D turns the character root toward camera-right while preserving the model's fixed local rotation");
        Save("mesh-facing", TurnedMesh);
        Key("down", 0x1B);
        Save("release-key-input", Invoke("input.describe"));
        Key("up", 0x1B);
        AwaitCapture(false);
        Save("released-input", Invoke("input.describe"));
        var Released = Position(Components());
        Key("down", 'W');
        Thread.Sleep(150);
        Key("up", 'W');
        Check(Vector3.Distance(Position(Components()), Released) < 0.001f,
            "released gameplay capture ignores movement keys");
        Click("viewportCanvas");
        AwaitCapture(true);
        Click("toolbar-tool-play-pause");
        AwaitCapture(false);
        Save("paused", Invoke("editor.describe"));
        Click("toolbar-tool-play-pause");
        AwaitCapture(true);
        var ScreenshotUi = Read();
        Save("final-ui", ScreenshotUi);
        var Frame = ScreenshotUi["frameId"]!.GetValue<ulong>();
        var Png = Call(Sequence++, "resources/read", new JsonObject
        {
            ["uri"] = $"sharpengine://ui/frames/{Frame}/main.png",
        });
        File.WriteAllBytes(Path.Combine(Evidence, "third-person.png"), Convert.FromBase64String(
            Png["result"]!["contents"]![0]!["blob"]!.GetValue<string>()));
        Click("toolbar-tool-stop");
        AwaitCapture(false);
        Save("stopped", Invoke("editor.describe"));
        Check(Components()["context"]!.GetValue<string>() == "authoring", "Stop restores authoring context");
        Save("result", new JsonObject
        {
            ["passed"] = true,
            ["immediateKeyboardCapture"] = true,
            ["cameraLook"] = true,
            ["cameraRelativeCharacterFacing"] = true,
            ["releaseRecapturePauseStop"] = true,
            ["locomotionStateMachine"] = true,
        });
        Console.WriteLine("Player UI evidence: " + Evidence);
    }
}
