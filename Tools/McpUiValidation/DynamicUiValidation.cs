using System.Diagnostics;
using System.Text.Json.Nodes;

internal static class DynamicUiValidation
{
    internal static string CreateProject(string Root, string Configuration)
    {
        var Folder = Path.Combine(Root, "Saved/Validation/DynamicUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Folder, "Source/DynamicUi"));
        Directory.CreateDirectory(Path.Combine(Folder, "Source/DynamicUi.Editor"));
        Directory.CreateDirectory(Path.Combine(Folder, "Content/Assets"));
        Directory.CreateDirectory(Path.Combine(Folder, "Content/UI"));
        Directory.CreateDirectory(Path.Combine(Folder, "Content/Worlds"));
        File.Copy(Path.Combine(Root, "Templates/BasicProject/Directory.Build.props"), Path.Combine(Folder, "Directory.Build.props"));
        File.Copy(Path.Combine(Root, "Content/Worlds/Main.sworld"), Path.Combine(Folder, "Content/Worlds/Main.sworld"));
        File.WriteAllText(Path.Combine(Folder, "Content/Assets/assets.json"), """{"schemaVersion":2,"assets":[]}""");
        File.WriteAllText(Path.Combine(Folder, "Content/UI/Dynamic.rml"), """
            <template><div id="panel" style="padding:20px;"><span>${Caption}</span><div style="margin-top:12px;">
            <button id="fixture-add" data-action="fixture.add">Add runtime UI</button>
            <button id="fixture-refresh" data-action="fixture.refresh">Refresh layout</button>
            <button id="fixture-remove" data-action="fixture.remove">Remove runtime UI</button>
            </div></div></template>
            """);
        foreach (var Kind in new[] { "Runtime", "Editor" })
        {
            var Name = Kind == "Runtime" ? "DynamicUi" : "DynamicUi.Editor";
            var Project = File.ReadAllText(Path.Combine(Root, "Templates/BasicProject/Source", Kind, Kind + ".csproj.template"))
                .Replace("__PROJECT_NAMESPACE__", "DynamicUi", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(Folder, "Source", Name, Name + ".csproj"), Project);
        }
        File.WriteAllText(Path.Combine(Folder, "Source/DynamicUi/Module.cs"), """
            using SharpEngine.Modules;
            public sealed class RuntimeModule : ISharpEngineModule
            {
                public void Startup(ModuleContext Context)
                {
                }
                public void Shutdown()
                {
                }
            }
            """);
        using var Stream = typeof(DynamicUiValidation).Assembly.GetManifestResourceStream("DynamicUiEditorFixture")!;
        using var Reader = new StreamReader(Stream);
        File.WriteAllText(Path.Combine(Folder, "Source/DynamicUi.Editor/Module.cs"), Reader.ReadToEnd());
        var Manifest = Path.Combine(Folder, "DynamicUi.sharpproject");
        File.WriteAllText(Manifest, new JsonObject
        {
            ["schemaVersion"] = 3,
            ["projectId"] = "dynamic-ui-validation",
            ["displayName"] = "Dynamic UI validation",
            ["engine"] = new JsonObject
            {
                ["id"] = "SharpEngine",
                ["version"] = "0.2.0-dev",
                ["association"] = "dynamic-ui-validation",
                ["manifest"] = "engine.json",
            },
            ["modules"] = new JsonArray("Source/DynamicUi/DynamicUi.csproj", "Source/DynamicUi.Editor/DynamicUi.Editor.csproj"),
            ["assets"] = new JsonObject
            {
                ["manifest"] = "Content/Assets/assets.json",
                ["root"] = "Content/Assets",
            },
            ["world"] = new JsonObject
            {
                ["startup"] = "Content/Worlds/Main.sworld",
            },
        }.ToJsonString());
        var Start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string[] Arguments = ["run", "--project", Path.Combine(Root, "Tools/ProjectBuild/SharpEngine.ProjectBuild.csproj"),
            "--", "build", Manifest, "--engine-root", Root, "--target", "Editor", "--configuration", Configuration];
        foreach (var Argument in Arguments)
        {
            Start.ArgumentList.Add(Argument);
        }
        using var Build = Process.Start(Start)!;
        var Output = Build.StandardOutput.ReadToEndAsync();
        var Errors = Build.StandardError.ReadToEndAsync();
        Build.WaitForExit();
        File.WriteAllText(Path.Combine(Folder, "build.log"), Output.Result + Errors.Result);
        if (Build.ExitCode != 0)
        {
            throw new InvalidOperationException("Dynamic UI fixture build failed: " + Output.Result + Errors.Result);
        }
        return Manifest;
    }

    internal static void Run(string Root, Action<bool, string> Check,
        Func<int, string, JsonObject, JsonObject> Tool, Func<int, string, JsonObject?, JsonObject> Call)
    {
        var Sequence = 41000;
        JsonObject Invoke(string Name, JsonObject? Arguments = null)
        {
            var Result = Tool(Sequence++, Name, Arguments ?? new JsonObject());
            return Result["result"]?["structuredContent"]?.AsObject() ?? throw new InvalidOperationException(Result.ToJsonString());
        }
        string Open(string AssetId)
        {
            var State = Invoke("editor.action", new JsonObject
            {
                ["action"] = "editor.open-asset:" + AssetId,
            });
            var Editor = State["assetTools"]!["editors"]!.AsArray().Single(Item => Item!["assetId"]!.GetValue<string>() == AssetId)!;
            Check(Editor["editorId"]!.GetValue<string>() == "dynamic-ui-validation", "The explicit project fixture owns its editor root");
            return Editor["rootId"]!.GetValue<string>();
        }
        JsonObject State(string HostId)
        {
            return Invoke("editor.describe")["assetTools"]!["editors"]!.AsArray().Single(Item => Item!["rootId"]!.GetValue<string>() == HostId)!["state"]!.AsObject();
        }
        JsonObject Host(JsonObject Ui, string HostId)
        {
            return Ui["hosts"]!.AsArray().Single(Item => Item!["hostId"]!.GetValue<string>() == HostId)!.AsObject();
        }
        JsonObject Observe(string HostId)
        {
            var Timer = Stopwatch.StartNew();
            while (Timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                var Snapshot = Invoke("ui.describe");
                if (Snapshot["hosts"]!.AsArray().Any(Item => Item!["hostId"]!.GetValue<string>() == HostId))
                {
                    return Snapshot;
                }
                Thread.Sleep(10);
            }
            throw new TimeoutException("The isolated UI host did not publish a rendered frame: " + HostId);
        }
        JsonObject Element(JsonObject Ui, string HostId, string Id)
        {
            return Host(Ui, HostId)["elements"]!.AsArray().Single(Item => Item!["semanticId"]?.GetValue<string>() == Id)!.AsObject();
        }
        void Pointer(string HostId, string Id, string Event)
        {
            for (var Attempt = 0; Attempt < 8; Attempt++)
            {
                var Ui = Observe(HostId);
                var Target = Element(Ui, HostId, Id);
                var Bounds = Target["bounds"]!;
                var Result = Invoke("ui.pointer", new JsonObject
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
                if (Result["accepted"]?.GetValue<bool>() == true)
                {
                    return;
                }
                if (Result["diagnostics"]?.AsArray().FirstOrDefault()?["code"]?.GetValue<string>() != "UI_FRAME_STALE")
                {
                    throw new InvalidOperationException(Result.ToJsonString());
                }
            }
            throw new InvalidOperationException("Retained input frame unavailable");
        }
        void Click(string HostId, string Id)
        {
            Pointer(HostId, Id, "down");
            Pointer(HostId, Id, "up");
        }
        void Key(string HostId, string Event, int Code, string Text = "")
        {
            for (var Attempt = 0; Attempt < 8; Attempt++)
            {
                var Snapshot = Observe(HostId);
                var Result = Invoke("ui.key", new JsonObject
                {
                    ["hostId"] = HostId,
                    ["event"] = Event,
                    ["key"] = Code,
                    ["text"] = Text,
                    ["modifiers"] = new JsonArray(),
                    ["expectedFrameId"] = Snapshot["frameId"]!.DeepClone(),
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
            throw new InvalidOperationException("Retained keyboard frame unavailable");
        }
        var StartupTimer = Stopwatch.StartNew();
        while (true)
        {
            var ProjectCode = Invoke("editor.describe")["projectCode"]!.AsObject();
            var BuildState = ProjectCode["state"]!.GetValue<string>();
            if (BuildState == "Idle" && ProjectCode["message"]?.GetValue<string>() == "Project code reloaded")
            {
                break;
            }
            if (BuildState == "BuildFailed" || BuildState == "ReloadFailed" || StartupTimer.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new InvalidOperationException("Dynamic UI fixture startup did not settle: " + ProjectCode.ToJsonString());
            }
            Thread.Sleep(100);
        }
        var First = Open("engine://animations/uefn-mannequin-walk");
        Check(State(First)["buttonId"] is null, "The original layout contains no code-created controls");
        var SourcePath = State(First)["sourcePath"]!.GetValue<string>();
        var Original = File.ReadAllBytes(SourcePath);
        Click(First, "fixture-add");
        var ButtonId = State(First)["buttonId"]!.GetValue<string>();
        var InputId = State(First)["inputId"]!.GetValue<string>();
        var Ui = Observe(First);
        var RuntimeButton = Element(Ui, First, ButtonId);
        var OldAction = RuntimeButton["actions"]![0]!.GetValue<string>();
        Check(RuntimeButton["visible"]!.GetValue<bool>() && RuntimeButton["text"]!.GetValue<string>() == "Code ${Literal} <&>",
            "Code-created controls are visible, literal and hit-testable in the actual native layout");
        Click(First, ButtonId);
        Check(State(First)["clicks"]!.GetValue<int>() == 1, "A real click invokes the code-bound handler");
        Click(First, InputId);
        var Expected = string.Empty;
        foreach (var Character in "A<&:中")
        {
            Expected += Character;
            var Accepted = false;
            for (var Attempt = 0; Attempt < 8; Attempt++)
            {
                Ui = Observe(First);
                var Input = Element(Ui, First, InputId);
                Check(Host(Ui, First)["focusedElementId"]!.GetValue<string>() == Input["elementId"]!.GetValue<string>(),
                    "Continuous input retains focus through instance refresh");
                var Result = Invoke("ui.key", new JsonObject
                {
                    ["hostId"] = First,
                    ["event"] = "text",
                    ["key"] = 0,
                    ["text"] = Character.ToString(),
                    ["modifiers"] = new JsonArray(),
                    ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                });
                if (Result["accepted"]?.GetValue<bool>() == true)
                {
                    Accepted = true;
                    break;
                }
            }
            Check(Accepted && State(First)["text"]!.GetValue<string>() == Expected,
                "Text and value-change events survive repeated layout refresh");
        }
        for (var Index = 0; Index < 2; Index++)
        {
            Key(First, "down", 0x25);
            Key(First, "up", 0x25);
        }
        Key(First, "text", 0, "X");
        Key(First, "text", 0, "Y");
        Expected = "A<&XY:中";
        Check(State(First)["text"]!.GetValue<string>() == Expected,
            "The native caret remains in the middle of the text across value-change refreshes");
        Click(First, "fixture-refresh");
        Check(State(First)["buttonId"]!.GetValue<string>() == ButtonId && State(First)["text"]!.GetValue<string>() == Expected,
            "An explicit parent layout refresh preserves code-created state");
        var Second = Open("engine://animations/uefn-mannequin-idle");
        Check(State(Second)["buttonId"] is null, "A second use of the same definition has no leaked nodes");
        Click(Second, "fixture-add");
        var SecondButton = State(Second)["buttonId"]!.GetValue<string>();
        Click(Second, SecondButton);
        Check(SecondButton != ButtonId && State(Second)["clicks"]!.GetValue<int>() == 1 && State(First)["clicks"]!.GetValue<int>() == 1,
            "Runtime IDs and event callbacks are isolated between editor instances");
        var Evidence = Path.Combine(Path.GetDirectoryName(SourcePath)!, "../../ui-evidence");
        Directory.CreateDirectory(Evidence);
        Ui = Observe(First);
        File.WriteAllText(Path.Combine(Evidence, "ui.json"), Ui.ToJsonString());
        var Frame = Call(Sequence++, "resources/read", new JsonObject
        {
            ["uri"] = "sharpengine://ui/frames/" + Ui["frameId"]!.GetValue<ulong>() + "/" + First + ".png",
        });
        File.WriteAllBytes(Path.Combine(Evidence, "dynamic-ui.png"), Convert.FromBase64String(Frame["result"]!["contents"]![0]!["blob"]!.GetValue<string>()));
        Click(First, "fixture-remove");
        Check(State(First)["buttonId"] is null, "Runtime subtree removal reaches the displayed UI");
        Ui = Observe(First);
        Check(!Host(Ui, First)["elements"]!.AsArray().Any(Item => Item!["semanticId"]?.GetValue<string>() == ButtonId),
            "Removed controls no longer exist in the native rendered UI tree");
        Invoke("editor.action", new JsonObject
        {
            ["action"] = "editor.host-action:" + First + ":" + OldAction,
        });
        Check(State(First)["clicks"]!.GetValue<int>() == 1 && State(Second)["buttonId"]!.GetValue<string>() == SecondButton,
            "Removed event identities cannot invoke callbacks or alter another instance");
        Check(File.ReadAllBytes(SourcePath).SequenceEqual(Original) && State(First)["sourceUnchanged"]!.GetValue<bool>() && !State(First)["dirty"]!.GetValue<bool>(),
            "Runtime UI never changes the source layout or authoring dirty state");
    }
}
