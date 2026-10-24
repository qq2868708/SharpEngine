using System.Text.Json.Nodes;

internal static class RootMotionUiValidation
{
    private const string ClipId = "project://animations/root-motion-walk";

    internal static string CreateProject(string Root)
    {
        var Folder = Path.Combine(Root, "Saved/Validation/RootMotionUi-20261020");
        Directory.CreateDirectory(Path.Combine(Folder, "Content/Assets"));
        Directory.CreateDirectory(Path.Combine(Folder, "Content/Worlds"));
        var Clip = JsonNode.Parse(File.ReadAllText(Path.Combine(Root,
            "Content/Assets/Animations/UEFNMannequinWalk.sharpskeletalanimation")))!.AsObject();
        Clip["assetId"] = ClipId;
        Clip["name"] = "Root Motion Walk";
        Clip.Remove("sourcePath");
        Clip.Remove("sourceSha256");
        File.WriteAllText(Path.Combine(Folder, "Content/Assets/Walk.sskeletalanimation"), Clip.ToJsonString());
        var Manifest = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["assets"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "animations/root-motion-walk",
                    ["name"] = "Root Motion Walk",
                    ["type"] = "SkeletalAnimation",
                    ["path"] = "Assets/Walk.sskeletalanimation",
                },
            },
        };
        File.WriteAllText(Path.Combine(Folder, "Content/Assets/assets.json"), Manifest.ToJsonString());
        File.Copy(Path.Combine(Root, "Content/Worlds/Main.sworld"), Path.Combine(Folder, "Content/Worlds/Main.sworld"), true);
        var Project = new JsonObject
        {
            ["schemaVersion"] = 3,
            ["projectId"] = "root-motion-ui-validation",
            ["displayName"] = "Root Motion UI Validation",
            ["engineVersion"] = "0.2.0-dev",
            ["engine"] = new JsonObject
            {
                ["id"] = "SharpEngine",
                ["version"] = "0.2.0-dev",
                ["association"] = "root-motion-validation",
                ["manifest"] = "engine.json",
            },
            ["modules"] = new JsonArray("Source/RootMotionValidation/RootMotionValidation.csproj"),
            ["world"] = new JsonObject
            {
                ["startup"] = "Content/Worlds/Main.sworld",
            },
        };
        var PathValue = Path.Combine(Folder, "RootMotion.sharpproject");
        File.WriteAllText(PathValue, Project.ToJsonString());
        var ModuleFolder = Path.Combine(Folder, "Source/RootMotionValidation");
        Directory.CreateDirectory(ModuleFolder);
        File.Copy(Path.Combine(Root, "Templates/BasicProject/Directory.Build.props"),
            Path.Combine(Folder, "Directory.Build.props"), true);
        var ModuleProject = File.ReadAllText(Path.Combine(Root,
            "Templates/BasicProject/Source/Runtime/Runtime.csproj.template"))
            .Replace("__PROJECT_NAMESPACE__", "RootMotionValidation", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(ModuleFolder, "RootMotionValidation.csproj"), ModuleProject);
        File.WriteAllText(Path.Combine(ModuleFolder, "Module.cs"), """
            using SharpEngine.Modules;
            public sealed class RootMotionValidationModule : ISharpEngineModule
            {
                public void Startup(ModuleContext Context)
                {
                }
                public void Shutdown()
                {
                }
            }
            """);
        var BuildInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var Argument in new[]
        {
            "run", "--project", Path.Combine(Root, "Tools/ProjectBuild/SharpEngine.ProjectBuild.csproj"),
            "--", "build", PathValue, "--engine-root", Root,
            "--target", "Editor", "--configuration", "Debug",
        })
        {
            BuildInfo.ArgumentList.Add(Argument);
        }
        using var Build = System.Diagnostics.Process.Start(BuildInfo)!;
        var Output = Build.StandardOutput.ReadToEndAsync();
        var Errors = Build.StandardError.ReadToEndAsync();
        Build.WaitForExit();
        File.WriteAllText(Path.Combine(Folder, "build.log"), Output.Result + Errors.Result);
        if (Build.ExitCode != 0)
        {
            throw new InvalidOperationException("Root Motion validation project build failed: " + Errors.Result);
        }
        return PathValue;
    }

    internal static void Run(string Root, Action<bool, string> Check,
        Func<int, string, JsonObject, JsonObject> Tool,
        Func<int, string, JsonObject?, JsonObject> Call)
    {
        var Sequence = 21000;
        var Evidence = Path.Combine(Root, "Saved/Validation/RootMotionUi-20261020");
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
        Save("attached-tools", Call(Sequence++, "tools/list", null));
        var Open = Invoke("editor.action", new JsonObject
        {
            ["action"] = "editor.open-asset:" + ClipId,
        });
        var Editor = Open["assetTools"]!["editors"]!.AsArray().Single(Item =>
            Item!["assetId"]!.GetValue<string>() == ClipId)!;
        var HostId = Editor["rootId"]!.GetValue<string>();
        JsonObject Current()
        {
            return Invoke("editor.describe")["assetTools"]!["editors"]!.AsArray().Single(Item =>
                Item!["assetId"]!.GetValue<string>() == ClipId)!["state"]!.AsObject();
        }
        void Action(string Command)
        {
            Invoke("editor.action", new JsonObject
            {
                ["action"] = $"editor.host-action:{HostId}:{Command}",
            });
        }
        void Toggle()
        {
            foreach (var Event in new[] { "down", "up" })
            {
                for (var Attempt = 0; ; Attempt++)
                {
                    var Ui = Invoke("ui.describe");
                    var Target = Ui["hosts"]!.AsArray().Single(Host =>
                        Host!["hostId"]!.GetValue<string>() == HostId)!["elements"]!.AsArray().Single(Item =>
                        Item!["semanticId"]?.GetValue<string>() == "animationRootMotion")!;
                    Check(Target["visible"]!.GetValue<bool>() && Target["enabled"]!.GetValue<bool>(),
                        "project animation exposes an editable Root Motion control");
                    var Bounds = Target["bounds"]!;
                    var Response = Invoke("ui.pointer", new JsonObject
                    {
                        ["hostId"] = HostId,
                        ["event"] = Event,
                        ["button"] = "left",
                        ["x"] = (int)(Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>() / 2),
                        ["y"] = (int)(Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>() / 2),
                        ["buttons"] = new JsonArray(),
                        ["modifiers"] = new JsonArray(),
                        ["pointerId"] = 0,
                        ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                        ["expectedElementId"] = Target["elementId"]!.DeepClone(),
                    });
                    if (Response["accepted"]?.GetValue<bool>() == true)
                    {
                        break;
                    }
                    if (Attempt == 5 || Response["diagnostics"]?[0]?["code"]?.GetValue<string>() != "UI_FRAME_STALE")
                    {
                        throw new InvalidOperationException(Response.ToJsonString());
                    }
                }
            }
        }
        var Before = Current();
        Save("before", Before);
        Toggle();
        var Enabled = Current();
        Check(Enabled["rootMotion"]!["enabled"]!.GetValue<bool>() && Enabled["dirty"]!.GetValue<bool>() &&
            Enabled["documentRevision"]!.GetValue<ulong>() > Before["documentRevision"]!.GetValue<ulong>(),
            "coordinate click commits root motion to the live dirty asset revision");
        Action("editor.asset-command:add-notify");
        var WithNotify = Current();
        Action("editor.asset-command:undo");
        var UndoNotify = Current();
        Check(UndoNotify["rootMotion"]!["enabled"]!.GetValue<bool>() &&
            UndoNotify["document"]!.ToJsonString() == Enabled["document"]!.ToJsonString(),
            "notify and root-motion settings share one ordered undo history");
        Action("editor.asset-command:undo");
        Check(!Current()["rootMotion"]!["enabled"]!.GetValue<bool>() && !Current()["dirty"]!.GetValue<bool>(),
            "undo root motion returns to the clean source state");
        Action("editor.asset-command:redo");
        Action("editor.asset-save");
        Check(!Current()["dirty"]!.GetValue<bool>(), "explicit Save establishes an asset savepoint");
        var SavedClip = JsonNode.Parse(File.ReadAllText(Path.Combine(Evidence, "Content/Assets/Walk.sskeletalanimation")))!;
        Check(SavedClip["rootMotion"]!["enabled"]!.GetValue<bool>(), "saved source retains the enabled root motion setting");
        Save("enabled-saved", Current());
        var UiFrame = Invoke("ui.describe");
        Save("ui", UiFrame);
        var Frame = Call(Sequence++, "resources/read", new JsonObject
        {
            ["uri"] = $"sharpengine://ui/frames/{UiFrame["frameId"]!.GetValue<ulong>()}/{HostId}.png",
        });
        File.WriteAllBytes(Path.Combine(Evidence, "root-motion.png"), Convert.FromBase64String(
            Frame["result"]!["contents"]![0]!["blob"]!.GetValue<string>()));
        Action("editor.asset-close");
        var Components = Invoke("components.describe");
        var Patched = Invoke("components.edit", new JsonObject
        {
            ["context"] = "authoring",
            ["operation"] = "patch",
            ["expectedRevision"] = Components["revision"]!.DeepClone(),
            ["componentId"] = "player-animator",
            ["entityId"] = "player",
            ["state"] = new JsonObject
            {
                ["Controller"] = null,
                ["Clip"] = ClipId,
                ["MovementSpeedParameter"] = "",
            },
        });
        Check(Patched["success"]?.GetValue<bool>() == true, "authoring clip setup commits atomically");
        Components = Invoke("components.describe");
        Invoke("components.edit", new JsonObject
        {
            ["context"] = "authoring",
            ["operation"] = "enabled",
            ["expectedRevision"] = Components["revision"]!.DeepClone(),
            ["entityId"] = "player",
            ["componentId"] = "player-controller",
            ["value"] = false,
        });
        JsonObject PlaySample()
        {
            Invoke("editor.action", new JsonObject
            {
                ["action"] = "editor.play",
            });
            Thread.Sleep(1300);
            var Sample = Invoke("components.describe");
            Check(Sample["context"]!.GetValue<string>() == "runtime", "Play enters the Runtime world");
            return Sample;
        }
        float PlayerZ(JsonObject Snapshot)
        {
            return Snapshot["document"]!["entities"]!.AsArray().Single(Entity =>
                Entity!["entityId"]!.GetValue<string>() == "player")!["components"]!.AsArray().Single(Component =>
                Component!["componentId"]!.GetValue<string>() == "player-transform")!["state"]!["Position"]![2]!.GetValue<float>();
        }
        var Driven = PlaySample();
        Save("runtime-enabled", Driven);
        Check(PlayerZ(Driven) < -0.5f, "enabled mannequin root motion drives the runtime entity across a loop");
        Invoke("editor.action", new JsonObject
        {
            ["action"] = "editor.stop",
        });
        var Reopened = Invoke("editor.action", new JsonObject
        {
            ["action"] = "editor.open-asset:" + ClipId,
        });
        HostId = Reopened["assetTools"]!["editors"]!.AsArray().Single(Item =>
            Item!["assetId"]!.GetValue<string>() == ClipId)!["rootId"]!.GetValue<string>();
        Check(Current()["rootMotion"]!["enabled"]!.GetValue<bool>(), "reopened asset reads its saved root motion flag");
        Toggle();
        Action("editor.asset-save");
        Action("editor.asset-close");
        var InPlace = PlaySample();
        Save("runtime-disabled", InPlace);
        Check(Math.Abs(PlayerZ(InPlace)) < 0.001f, "disabled mannequin root motion retains the entity position after asset save and reload");
        Invoke("editor.action", new JsonObject
        {
            ["action"] = "editor.stop",
        });
        Save("result", new JsonObject
        {
            ["passed"] = true,
            ["coordinateToggle"] = true,
            ["sharedUndoRedo"] = true,
            ["explicitSave"] = true,
            ["runtimeEnabledDisabled"] = true,
        });
    }
}
