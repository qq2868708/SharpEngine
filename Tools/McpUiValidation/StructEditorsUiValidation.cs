using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class StructEditorsUiValidation
{
    private static string ProjectFolder = string.Empty;

    internal static string CreateProject(string Root, string Configuration)
    {
        ProjectFolder = Path.Combine(Root, "Saved/Validation/Structures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ProjectFolder, "Source/Structures"));
        Directory.CreateDirectory(Path.Combine(ProjectFolder, "Content/Assets"));
        Directory.CreateDirectory(Path.Combine(ProjectFolder, "Content/Worlds"));
        File.Copy(Path.Combine(Root, "Templates/BasicProject/Directory.Build.props"),
            Path.Combine(ProjectFolder, "Directory.Build.props"));
        File.Copy(Path.Combine(Root, "Content/Worlds/Main.sworld"),
            Path.Combine(ProjectFolder, "Content/Worlds/Main.sworld"));
        var ProjectText = File.ReadAllText(Path.Combine(Root,
            "Templates/BasicProject/Source/Runtime/Runtime.csproj.template"))
            .Replace("__PROJECT_NAMESPACE__", "Structures", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(ProjectFolder, "Source/Structures/Structures.csproj"), ProjectText);
        using var Stream = typeof(StructEditorsUiValidation).Assembly
            .GetManifestResourceStream("StructEditorsFixture")!;
        using var Reader = new StreamReader(Stream);
        File.WriteAllText(Path.Combine(ProjectFolder, "Source/Structures/Structures.cs"), Reader.ReadToEnd());
        var Value = JsonNode.Parse("""{"power":5,"offset":[1,2,3],"child":{"title":"Nested fields","enabled":true}}""")!;
        var Document = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["kind"] = "Validation.StructureData",
            ["assetId"] = "project://data/structures",
            ["name"] = "Struct Controls",
            ["configuration"] = Value.DeepClone(),
            ["entries"] = new JsonArray(Value.DeepClone()),
            ["lookup"] = new JsonObject
            {
                ["first"] = Value.DeepClone(),
            },
        };
        File.WriteAllText(Path.Combine(ProjectFolder, "Content/Assets/Structures.sdataasset"), Document.ToJsonString());
        File.WriteAllText(Path.Combine(ProjectFolder, "Content/Assets/assets.json"), """
            {"schemaVersion":2,"assets":[{"id":"data/structures","name":"Struct Controls","type":"Validation.StructureData","path":"Assets/Structures.sdataasset"}]}
            """);
        var Manifest = Path.Combine(ProjectFolder, "Structures.sharpproject");
        File.WriteAllText(Manifest, new JsonObject
        {
            ["schemaVersion"] = 3,
            ["projectId"] = "struct-controls-validation",
            ["engine"] = new JsonObject
            {
                ["id"] = "SharpEngine",
                ["version"] = "0.2.0-dev",
                ["association"] = "struct-controls-validation",
                ["manifest"] = "engine.json",
            },
            ["modules"] = new JsonArray("Source/Structures/Structures.csproj"),
            ["assets"] = new JsonObject
            {
                ["manifest"] = "Content/Assets/assets.json",
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
        foreach (var Argument in new[]
        {
            "run", "--project", Path.Combine(Root, "Tools/ProjectBuild/SharpEngine.ProjectBuild.csproj"),
            "--", "build", Manifest, "--engine-root", Root, "--target", "Editor",
            "--configuration", Configuration,
        })
        {
            Start.ArgumentList.Add(Argument);
        }
        using var Build = Process.Start(Start)!;
        var Output = Build.StandardOutput.ReadToEndAsync();
        var Errors = Build.StandardError.ReadToEndAsync();
        Build.WaitForExit();
        var Log = Output.Result + Errors.Result;
        File.WriteAllText(Path.Combine(ProjectFolder, "build.log"), Log);
        if (Build.ExitCode != 0)
        {
            throw new InvalidOperationException("Struct fixture build failed: " + Log);
        }
        return Manifest;
    }

    internal static void Run(string Root, Action<bool, string> Check,
        Func<int, string, JsonObject, JsonObject> Tool,
        Func<int, string, JsonObject?, JsonObject> Call, string ExpectedBackend)
    {
        var Sequence = 91000;
        var HostId = string.Empty;
        var Output = Path.Combine(Root, "Saved/Validation/Structures");
        JsonObject Invoke(string Name, JsonObject? Arguments = null)
        {
            var Result = Tool(Sequence++, Name, Arguments ?? new JsonObject());
            return Result["result"]?["structuredContent"]?.AsObject() ??
                throw new InvalidOperationException(Result.ToJsonString());
        }
        JsonObject State()
        {
            return Invoke("editor.describe")["assetTools"]!["editors"]!.AsArray()
                .Single(Node => Node!["assetId"]!.GetValue<string>() == "project://data/structures")!["state"]!.AsObject();
        }
        JsonObject Observe()
        {
            return Invoke("ui.describe");
        }
        var Render = Invoke("render.describe");
        Check(Render["backend"]!.GetValue<string>() == ExpectedBackend,
            "Struct UI validation uses the requested native backend");
        File.WriteAllText(Path.Combine(Output, "render.json"), Render.ToJsonString());
        JsonObject Host(JsonObject Ui)
        {
            return Ui["hosts"]!.AsArray().Single(Node =>
                Node!["hostId"]!.GetValue<string>() == HostId)!.AsObject();
        }
        JsonObject Element(JsonObject Ui, string Id)
        {
            var Matches = Host(Ui)["elements"]!.AsArray().Select(Node => Node!.AsObject()).Where(Node =>
                Node["semanticId"]?.GetValue<string>() == Id ||
                Node["actions"]!.AsArray().Any(Action => Action!.GetValue<string>() == Id)).ToArray();
            return Matches.FirstOrDefault(Node => Node["visible"]!.GetValue<bool>() &&
                Node["hitTarget"]!.GetValue<bool>()) ?? Matches.First();
        }
        void Capture(string Name)
        {
            var Ui = Observe();
            var Frame = Call(Sequence++, "resources/read", new JsonObject
            {
                ["uri"] = $"sharpengine://ui/frames/{Ui["frameId"]}/{HostId}.png",
            });
            var Image = Frame["result"]!["contents"]![0]!["blob"]!.GetValue<string>();
            File.WriteAllBytes(Path.Combine(Output, Name + ".png"), Convert.FromBase64String(Image));
            File.WriteAllText(Path.Combine(Output, Name + ".json"), Ui.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        void Click(string Id)
        {
            foreach (var Event in new[] { "down", "up" })
            {
                var Accepted = false;
                JsonObject? LastResult = null;
                for (var Attempt = 0; Attempt < 10; Attempt++)
                {
                    var Ui = Observe();
                    var Target = Element(Ui, Id);
                    if (!Target["visible"]!.GetValue<bool>() && HostId != "settings.project")
                    {
                        var Pane = Element(Ui, "dataAsset-properties")["bounds"]!;
                        var Direction = Target["bounds"]!["y"]!.GetValue<double>() < Pane["y"]!.GetValue<double>() ? -3 : 3;
                        var Scroll = Invoke("ui.pointer", new JsonObject
                        {
                            ["hostId"] = HostId,
                            ["event"] = "wheel",
                            ["button"] = "none",
                            ["x"] = (int)(Pane["x"]!.GetValue<double>() + Pane["width"]!.GetValue<double>() - 25),
                            ["y"] = (int)(Pane["y"]!.GetValue<double>() + Pane["height"]!.GetValue<double>() / 2),
                            ["buttons"] = new JsonArray(),
                            ["modifiers"] = new JsonArray(),
                            ["pointerId"] = 0,
                            ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                            ["wheelX"] = 0,
                            ["wheelY"] = Direction,
                        });
                        var ScrollCode = Scroll["diagnostics"]?.AsArray().FirstOrDefault()?["code"]?.GetValue<string>();
                        Check(Scroll["accepted"]!.GetValue<bool>() || ScrollCode == "UI_FRAME_STALE",
                            "Real wheel input reaches the struct properties pane or requests a fresh frame");
                        continue;
                    }
                    var Bounds = Target["bounds"]!;
                    var Clip = Target["clip"]!;
                    var Left = Math.Max(Bounds["x"]!.GetValue<double>(), Clip["x"]!.GetValue<double>());
                    var Top = Math.Max(Bounds["y"]!.GetValue<double>(), Clip["y"]!.GetValue<double>());
                    var Right = Math.Min(Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>(),
                        Clip["x"]!.GetValue<double>() + Clip["width"]!.GetValue<double>());
                    var Bottom = Math.Min(Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>(),
                        Clip["y"]!.GetValue<double>() + Clip["height"]!.GetValue<double>());
                    Check(Right > Left && Bottom > Top && Target["visible"]!.GetValue<bool>(),
                        Id + " must have a visible hit target");
                    var Result = Invoke("ui.pointer", new JsonObject
                    {
                        ["hostId"] = HostId,
                        ["event"] = Event,
                        ["button"] = "left",
                        ["x"] = (int)((Left + Right) / 2),
                        ["y"] = (int)((Top + Bottom) / 2),
                        ["buttons"] = new JsonArray(),
                        ["modifiers"] = new JsonArray(),
                        ["pointerId"] = 0,
                        ["expectedFrameId"] = Ui["frameId"]!.DeepClone(),
                        ["expectedElementId"] = Target["elementId"]!.DeepClone(),
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
                    Capture("failed-struct-input");
                    File.WriteAllText(Path.Combine(Output, "failed-struct-input-result.json"),
                        LastResult?.ToJsonString() ?? "null");
                }
                Check(Accepted, "Coordinate input must reach " + Id);
            }
        }
        void Key(string Event, int Code = 0, string Text = "", bool Control = false)
        {
            for (var Attempt = 0; Attempt < 10; Attempt++)
            {
                var Ui = Observe();
                var Result = Invoke("ui.key", new JsonObject
                {
                    ["hostId"] = HostId,
                    ["event"] = Event,
                    ["key"] = Code,
                    ["text"] = Text,
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
            throw new TimeoutException("Struct key input frame stayed stale");
        }
        void Type(string Id, string Text)
        {
            Click(Id);
            Key("down", 0x41, Control: true);
            Key("up", 0x41, Control: true);
            Key("text", Text: Text);
            Key("down", 0x0D);
            Key("up", 0x0D);
        }
        string GroupId(params string[] Path)
        {
            return "dataAsset-struct-" + Uri.EscapeDataString(
                new JsonArray(Path.Select(Segment => (JsonNode?)JsonValue.Create(Segment)).ToArray()).ToJsonString());
        }
        var Opened = Invoke("editor.action", new JsonObject
        {
            ["action"] = "editor.open-asset:project://data/structures",
        });
        HostId = Opened["assetTools"]!["editors"]!.AsArray().Single(Node =>
            Node!["assetId"]!.GetValue<string>() == "project://data/structures")!["rootId"]!.GetValue<string>();
        var Timer = Stopwatch.StartNew();
        while (!Observe()["hosts"]!.AsArray().Any(Node =>
            Node!["hostId"]!.GetValue<string>() == HostId) && Timer.Elapsed.TotalSeconds < 15)
        {
            Thread.Sleep(25);
        }
        var Configuration = GroupId("configuration");
        Capture("data-asset-struct-expanded");
        var Layout = Observe();
        var PreviousRight = double.NegativeInfinity;
        for (var Axis = 0; Axis < 3; Axis++)
        {
            var Bounds = Element(Layout, Configuration + "-offset-" + Axis)["bounds"]!;
            var Left = Bounds["x"]!.GetValue<double>();
            var Width = Bounds["width"]!.GetValue<double>();
            Check(Width >= 40 && Left >= PreviousRight,
                "Struct Vector axes have usable widths and do not overlap");
            PreviousRight = Left + Width;
        }
        Type(Configuration + "-power", "12");
        Check(State()["document"]!["configuration"]!["power"]!.GetValue<int>() == 12,
            "Real focused input edits a DataAsset struct member");
        var ReadOnlyMember = Element(Observe(), Configuration + "-computed");
        Check(!ReadOnlyMember["enabled"]!.GetValue<bool>() &&
            ReadOnlyMember["value"]!.GetValue<string>() == "24",
            "Derived struct members display current values using native disabled inputs");
        Type(Configuration + "-offset-0", "17");
        Check(State()["document"]!["configuration"]!["offset"]![0]!.GetValue<float>() == 17 &&
            State()["document"]!["configuration"]!["offset"]![1]!.GetValue<float>() == 2,
            "Real input edits one struct Vector axis while preserving siblings");
        var BeforeFold = State()["documentRevision"]!.GetValue<ulong>();
        Click(Configuration + "-toggle");
        Capture("data-asset-struct-collapsed");
        Check(State()["documentRevision"]!.GetValue<ulong>() == BeforeFold,
            "Real struct collapse changes no authoring revision");
        Click("editor.asset-command:undo");
        Check(State()["document"]!["configuration"]!["offset"]![0]!.GetValue<float>() == 1,
            "Real Undo bypasses transient folding and restores the axis edit");
        Click(Configuration + "-toggle");
        Capture("data-asset-struct-edited");
        Click("editor.asset-save");
        var Saved = JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectFolder,
            "Content/Assets/Structures.sdataasset")))!;
        Check(Saved["configuration"]!["power"]!.GetValue<int>() == 12,
            "Explicit Save persists the struct through the actual DataAsset editor");
        Click(Configuration + "-toggle");
        Type(GroupId("entries", "0") + "-power", "31");
        Check(State()["document"]!["entries"]![0]!["power"]!.GetValue<int>() == 31,
            "Real scrolling and focused input edit a struct inside a list");
        Capture("data-asset-list-struct-edited");
        var EntriesId = "dataAsset-collection-" + Uri.EscapeDataString(new JsonArray("entries").ToJsonString());
        Click(EntriesId + "-toggle");
        Type(GroupId("lookup", "first") + "-power", "41");
        Check(State()["document"]!["lookup"]!["first"]!["power"]!.GetValue<int>() == 41,
            "Real focused input edits a struct inside a Map value");
        Capture("data-asset-map-struct-edited");
        Click("editor.asset-save");
        Invoke("editor.action", new JsonObject
        {
            ["action"] = "editor.project-settings",
        });
        HostId = "settings.project";
        var SettingsGroup = "setting-struct-" + Uri.EscapeDataString(
            "Project:validation.structures:configuration:[]");
        Capture("settings-struct-expanded");
        Type("setting-configuration/power", "23");
        var Page = Invoke("editor.describe")["settings"]!["pages"]!.AsArray()
            .Single(Node => Node!["id"]!.GetValue<string>() == "validation.structures")!;
        Check(Page["values"]!["configuration"]!["power"]!.GetValue<int>() == 23,
            "Real focused input edits a boxed Settings struct through its transaction");
        Click(SettingsGroup + "-toggle");
        Capture("settings-struct-collapsed");
        Click(SettingsGroup + "-toggle");
        Capture("settings-struct-edited");
    }
}
