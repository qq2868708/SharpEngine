using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEngine.Interop;

// The pipe thread only transports messages. Native UI calls and all mutations
// execute in Pump on the selected host's UI thread, between presentations.
internal sealed class UiMcpSessionHost : IDisposable
{
    private sealed record Pending(string Line, TaskCompletionSource<string?> Completion);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string Registry(string Role)
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpEngine", Role == "game" ? "GameMcpSessions" : "ProjectBrowserMcpSessions");
    }
    private readonly string Role;
    private readonly string HostId;
    private readonly string DomainTool;
    private readonly string DomainResource;
    private readonly Func<JsonNode> DescribeDomain;
    private readonly Func<JsonObject>? CommandTool;
    private readonly Func<JsonObject, JsonNode>? ExecuteCommand;
    private readonly ConcurrentQueue<Pending> Requests = new();
    private readonly CancellationTokenSource Cancellation = new();
    private readonly nint Api;
    private readonly string SessionId;
    private readonly string PipeName;
    private readonly string RegistrationPath;
    private readonly Task Server;

    internal UiMcpSessionHost(nint Api, string Role, Func<JsonNode> DescribeDomain,
        Func<JsonObject>? CommandTool = null, Func<JsonObject, JsonNode>? ExecuteCommand = null)
    {
        if (Role is not ("game" or "project-browser"))
        {
            throw new ArgumentException("Unsupported engine MCP UI role", nameof(Role));
        }
        this.Api = Api;
        this.Role = Role;
        this.DescribeDomain = DescribeDomain;
        this.CommandTool = CommandTool;
        this.ExecuteCommand = ExecuteCommand;
        HostId = Role == "game" ? "game" : "main";
        DomainTool = Role == "game" ? "render.describe" : "browser.describe";
        DomainResource = Role == "game" ? "sharpengine://render" : "sharpengine://project-browser";
        SessionId = Guid.TryParseExact(Argument("--mcp-session-id"), "N", out var Id)
            ? Id.ToString("N") : Guid.NewGuid().ToString("N");
        PipeName = (Role == "game" ? "SharpEngine.Game." : "SharpEngine.ProjectBrowser.") + SessionId;
        Directory.CreateDirectory(Registry(Role));
        RegistrationPath = Path.Combine(Registry(Role), SessionId + ".json");
        var Temporary = RegistrationPath + ".tmp";
        File.WriteAllText(Temporary, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            SessionId,
            Mode = Role,
            EngineRoot = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT"),
            PipeName,
            ProcessId = Environment.ProcessId,
            ProjectManifest = Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST"),
            UiAvailable = true,
        }, JsonOptions));
        File.Move(Temporary, RegistrationPath, true);
        SharpEngine.Logging.SharpLog.Info("MCP", "UI session registered: " + RegistrationPath);
        Server = Task.Run(RunServer);
    }

    internal void Pump()
    {
        while (Requests.TryDequeue(out var Request))
        {
            Request.Completion.TrySetResult(Dispatch(Request.Line, Process));
        }
    }

    private async Task RunServer()
    {
        var Token = Cancellation.Token;
        while (!Token.IsCancellationRequested)
        {
            try
            {
                await using var Pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await Pipe.WaitForConnectionAsync(Token).ConfigureAwait(false);
                using var Reader = new StreamReader(Pipe, new UTF8Encoding(false), false, 4096, true);
                await using var Writer = new StreamWriter(Pipe, new UTF8Encoding(false), 4096, true)
                {
                    AutoFlush = true,
                };
                while (Pipe.IsConnected && await Reader.ReadLineAsync(Token).ConfigureAwait(false) is { } Line)
                {
                    var Completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Requests.Enqueue(new Pending(Line, Completion));
                    var Response = await Completion.Task.WaitAsync(Token).ConfigureAwait(false);
                    if (Response is not null)
                    {
                        await Writer.WriteLineAsync(Response).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // A disconnected client can reconnect without stopping Game.
            }
        }
    }

    private JsonNode Process(string Method, JsonObject Parameters)
    {
        return Method switch
        {
            "initialize" => Initialize(Role),
            "ping" => new JsonObject(),
            "tools/list" => ToolList(),
            "tools/call" => CallTool(Parameters),
            "resources/list" => new JsonObject
            {
                ["resources"] = new JsonArray(
                    new JsonObject
                    {
                        ["uri"] = "sharpengine://ui",
                        ["name"] = "Live engine UI",
                        ["mimeType"] = "application/json",
                    },
                    new JsonObject
                    {
                        ["uri"] = DomainResource,
                        ["name"] = DomainTool,
                        ["mimeType"] = "application/json",
                    }),
            },
            "resources/templates/list" => JsonNode.Parse("""{"resourceTemplates":[{"uriTemplate":"sharpengine://ui/frames/{frameId}/{hostId}.png","name":"Presented runtime frame","mimeType":"image/png"}]}""")!,
            "resources/read" => ReadResource(Parameters),
            _ => throw new ArgumentException($"Unknown MCP method '{Method}'"),
        };
    }

    private static JsonObject Initialize(string Role)
    {
        return JsonNode.Parse("""
            {"protocolVersion":"2025-06-18","capabilities":{"tools":{},"resources":{}},
             "serverInfo":{"name":"sharpengine-game","version":"0.2.0"},
             "instructions":"Discover ui.describe before coordinate input. Read the PNG resource with the same frameId. Input changes transient runtime state only; it does not save assets."}
            """.Replace("sharpengine-game", "sharpengine-" + Role, StringComparison.Ordinal)
            .Replace("Input changes transient runtime state only; it does not save assets.",
                Role == "game" ? "Input changes transient runtime state only; it does not save assets."
                    : "Project Browser has no EditorRuntime or project Modules. UI input may create project files or launch the selected Editor. Use browser.describe and browser.command for semantic state and commands.", StringComparison.Ordinal))!.AsObject();
    }

    private JsonObject ToolList()
    {
        var Tools = JsonNode.Parse("""
            {"tools":[
              {"name":"ui.describe","description":"Pin the last completed Game frame and describe RmlUi IDs, bounds, clipping, visibility, z-order, focus and hit targets.",
               "inputSchema":{"type":"object","additionalProperties":false}},
              {"name":"render.describe","description":"Return transient backend, GPU fallback, cache, and average/P95 frame timing statistics.",
               "inputSchema":{"type":"object","additionalProperties":false}},
              {"name":"ui.pointer","description":"Host-local coordinate input through the Game window and RmlUi hit testing, focus and bubbling. Use a retained frame and expectedElementId from ui.describe.",
               "inputSchema":{"type":"object","additionalProperties":false,"required":["hostId","event","x","y","expectedFrameId"],"properties":{
                 "hostId":{"const":"game"},"event":{"enum":["move","down","up","cancel","wheel"]},
                 "x":{"type":"integer"},"y":{"type":"integer"},"button":{"enum":["none","left","right","middle"]},
                 "expectedFrameId":{"type":"integer","minimum":1},"expectedElementId":{"type":"string"},
                 "modifiers":{"type":"integer","minimum":0,"maximum":7},"pointerId":{"const":0},
                 "wheelX":{"type":"number"},"wheelY":{"type":"number"}}}},
              {"name":"ui.key","description":"Key or UTF-8 text input to the focused RmlUi element, checked against the observed state revision.",
               "inputSchema":{"type":"object","additionalProperties":false,"required":["hostId","event","expectedFrameId"],"properties":{
                 "hostId":{"const":"game"},"event":{"enum":["down","up","text"]},"key":{"type":"integer","minimum":0,"maximum":255},
                 "text":{"type":"string","maxLength":4096},"modifiers":{"type":"integer","minimum":0,"maximum":7},
                 "expectedFrameId":{"type":"integer","minimum":1}}}}
            ]}
            """)!.AsObject();
        foreach (var Item in Tools["tools"]!.AsArray())
        {
            if (Role == "project-browser")
            {
                Item!["description"] = Item["description"]!.GetValue<string>().Replace("Game", "Project Browser", StringComparison.Ordinal);
            }
            if (Item!["name"]!.GetValue<string>() == "render.describe")
            {
                Item["name"] = DomainTool;
                Item["description"] = Role == "game" ? "Read render telemetry." : "Read the latest committed lightweight Project Browser state.";
            }
            var HostProperty = Item["inputSchema"]?["properties"]?["hostId"];
            if (HostProperty is not null)
            {
                HostProperty["const"] = HostId;
            }
            var Modifiers = Item["inputSchema"]?["properties"]?["modifiers"];
            if (Modifiers is not null)
            {
                Modifiers["description"] = "RmlUi modifier mask: control=1, shift=2, alt=4";
                Modifiers["x-sharpengine-modifiers"] = new JsonObject
                {
                    ["control"] = 1,
                    ["shift"] = 2,
                    ["alt"] = 4,
                };
            }
        }
        if (CommandTool is not null)
        {
            Tools["tools"]!.AsArray().Add(CommandTool());
        }
        foreach (var Tool in Tools["tools"]!.AsArray().Select(Node => Node!.AsObject()))
        {
            var Name = Tool["name"]!.GetValue<string>();
            var Available = Tool["_meta"]?["sharpengine/capability"]?["available"]?.GetValue<bool>() ?? true;
            var Precondition = Tool["_meta"]?["sharpengine/capability"]?["revisionPrecondition"]?.GetValue<string>();
            var Read = Name == "ui.describe" || Name == DomainTool;
            Tool["inputSchema"]!["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            Tool["outputSchema"] = JsonNode.Parse("""{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"frameId":{"type":"integer"},"diagnostics":{"type":"array"}}}""");
            Tool["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = Read,
                ["destructiveHint"] = false,
                ["idempotentHint"] = Read,
                ["openWorldHint"] = !Read && Role != "game",
            };
            Tool["_meta"] = new JsonObject
            {
                ["sharpengine/capability"] = new JsonObject
                {
                    ["id"] = Name,
                    ["schemaVersion"] = 1,
                    ["effect"] = Read ? "read" : Role == "game" ? "runtime" : "external",
                    ["available"] = Available,
                    ["unavailability"] = Available ? null : new JsonObject
                    {
                        ["code"] = "BROWSER_BUSY",
                        ["message"] = "Wait for browser.describe.busy to become false",
                        ["retryable"] = true,
                    },
                    ["revisionPrecondition"] = Precondition,
                    ["state"] = "transient",
                    ["context"] = Role == "game" ? "runtime" : "authoring",
                },
            };
        }
        return Tools;
    }

    private unsafe JsonObject CallTool(JsonObject Parameters)
    {
        var Name = Parameters["name"]!.GetValue<string>();
        var Arguments = Parameters["arguments"]?.AsObject() ?? new JsonObject();
        var Definition = ToolList()["tools"]!.AsArray().FirstOrDefault(Tool => Tool?["name"]?.GetValue<string>() == Name)
            ?? throw new ArgumentException($"Unknown tool '{Name}'");
        var Properties = Definition["inputSchema"]?["properties"]?.AsObject();
        foreach (var Field in Arguments)
        {
            if (Properties?.ContainsKey(Field.Key) != true)
            {
                throw new ArgumentException($"Unknown field '{Field.Key}'");
            }
        }
        var Required = Definition["inputSchema"]?["required"] as JsonArray;
        if (Required is not null)
        {
            foreach (var Field in Required)
            {
                if (!Arguments.ContainsKey(Field!.GetValue<string>()))
                {
                    throw new ArgumentException($"Missing field '{Field}'");
                }
            }
        }
        if (Name == DomainTool)
        {
            var RenderState = DescribeDomain();
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = RenderState.ToJsonString(),
                }),
                ["structuredContent"] = RenderState,
                ["isError"] = false,
            };
        }
        if (Name == "browser.command" && ExecuteCommand is not null)
        {
            var CommandResult = ExecuteCommand(Arguments);
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = CommandResult.ToJsonString(),
                }),
                ["structuredContent"] = CommandResult,
                ["isError"] = false,
            };
        }
        var Table = (NativeEngineApiTable*)Api;
        var Result = new byte[128 * 1024];
        uint Count;
        fixed (byte* Output = Result)
        {
            if (Name == "ui.describe")
            {
                Count = Table->GetUiSnapshot(0, 0, Output, (uint)Result.Length);
            }
            else
            {
                var Host = Utf8(Arguments["hostId"]!.GetValue<string>());
                var Expected = Arguments["expectedFrameId"]!.GetValue<ulong>();
                var Modifiers = Arguments["modifiers"]?.GetValue<uint>() ?? 0;
                var Event = Arguments["event"]!.GetValue<string>();
                fixed (byte* HostPointer = Host)
                {
                    if (Name == "ui.pointer")
                    {
                        var Kind = EventIndex(Event, ["move", "down", "up", "cancel", "wheel"]);
                        var Button = EventIndex(Arguments["button"]?.GetValue<string>() ?? "none", ["none", "left", "right", "middle"]);
                        var Element = Utf8(Arguments["expectedElementId"]?.GetValue<string>() ?? "");
                        fixed (byte* ElementPointer = Element)
                        {
                            Count = Table->UiPointer(HostPointer, Kind, Arguments["x"]!.GetValue<int>(),
                                Arguments["y"]!.GetValue<int>(), Button, 0, Modifiers,
                                Arguments["pointerId"]?.GetValue<uint>() ?? 0, Expected,
                                Arguments["wheelX"]?.GetValue<float>() ?? 0, Arguments["wheelY"]?.GetValue<float>() ?? 0,
                                ElementPointer, Output, (uint)Result.Length);
                        }
                    }
                    else
                    {
                        var Text = Arguments["text"]?.GetValue<string>() ?? "";
                        if (Text.Length > 4096)
                        {
                            throw new ArgumentException("Text exceeds 4096 characters");
                        }
                        var Bytes = Utf8(Text);
                        fixed (byte* TextPointer = Bytes)
                        {
                            Count = Table->UiKey(HostPointer, EventIndex(Event, ["down", "up", "text"]),
                                Arguments["key"]?.GetValue<uint>() ?? 0, Modifiers, TextPointer, Expected, Output, (uint)Result.Length);
                        }
                    }
                }
            }
        }
        if (Count == 0 || Count > Result.Length)
        {
            throw new InvalidOperationException("Native UI response unavailable or exceeds capacity");
        }
        var State = JsonNode.Parse(Encoding.UTF8.GetString(Result, 0, (int)Count - 1))!;
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = State.ToJsonString(),
            }),
            ["structuredContent"] = State,
            ["isError"] = State["accepted"]?.GetValue<bool>() == false,
        };
    }

    private unsafe JsonObject ReadResource(JsonObject Parameters)
    {
        var Uri = Parameters["uri"]!.GetValue<string>();
        if (Uri == "sharpengine://ui")
        {
            var State = CallTool(new JsonObject
            {
                ["name"] = "ui.describe",
            });
            return Resource(Uri, "application/json", "text", State["structuredContent"]!.ToJsonString());
        }
        if (Uri == DomainResource)
        {
            return Resource(Uri, "application/json", "text",
                DescribeDomain().ToJsonString());
        }
        const string Prefix = "sharpengine://ui/frames/";
        var Parts = Uri.StartsWith(Prefix, StringComparison.Ordinal) ? Uri[Prefix.Length..].Split('/') : [];
        if (Parts.Length != 2 || Parts[1] != HostId + ".png")
        {
            throw new ArgumentException("Unknown frame resource URI");
        }
        var ValidFrame = ulong.TryParse(Parts[0], out var FrameId);
        if (!ValidFrame)
        {
            throw new ArgumentException("Unknown frame resource URI");
        }
        var Table = (NativeEngineApiTable*)Api;
        var Host = Utf8(HostId);
        uint Width = 0, Height = 0;
        fixed (byte* HostPointer = Host)
        {
            var Count = Table->ReadUiFrame(FrameId, HostPointer, null, 0, &Width, &Height);
            if (Count == 0)
            {
                return Resource(Uri, "application/json", "text", "{\"code\":\"UI_FRAME_EXPIRED\",\"retryable\":true}");
            }
            var Pixels = new byte[Count];
            fixed (byte* Output = Pixels)
            {
                if (Table->ReadUiFrame(FrameId, HostPointer, Output, Count, &Width, &Height) != Count)
                {
                    throw new InvalidOperationException("Frame readback changed during one UI observation");
                }
            }
            return Resource(Uri, "image/png", "blob", Convert.ToBase64String(UiPngEncoder.Encode(new NativeUiFrame(Width, Height, Pixels))));
        }
    }

    private static JsonObject Resource(string Uri, string MimeType, string Field, string Value)
    {
        return new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["uri"] = Uri,
                ["mimeType"] = MimeType,
                [Field] = Value,
            }),
        };
    }

    private static byte[] Utf8(string Value) { return Encoding.UTF8.GetBytes(Value + "\0"); }

    private static uint EventIndex(string Value, string[] Values)
    {
        var Index = Array.IndexOf(Values, Value);
        return Index >= 0 ? (uint)Index : throw new ArgumentException($"Unknown input value '{Value}'");
    }

    private static string? Argument(string Name)
    {
        var Arguments = Environment.GetCommandLineArgs();
        var Index = Array.IndexOf(Arguments, Name);
        return Index >= 0 && Index + 1 < Arguments.Length ? Arguments[Index + 1] : null;
    }

    private static string? Dispatch(string Line, Func<string, JsonObject, JsonNode> Handler)
    {
        JsonNode? Id = null;
        try
        {
            var Request = JsonNode.Parse(Line)!.AsObject();
            Id = Request["id"]?.DeepClone();
            if (Id is null)
            {
                return null;
            }
            var Result = Handler(Request["method"]!.GetValue<string>(), Request["params"]?.AsObject() ?? new JsonObject());
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Id,
                ["result"] = Result,
            }.ToJsonString();
        }
        catch (Exception Error)
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Id?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = Error is ArgumentException ? -32602 : -32603,
                    ["message"] = Error.Message,
                },
            }.ToJsonString();
        }
    }

    private static JsonArray ListSessions(string Role)
    {
        var Sessions = new JsonArray();
        Directory.CreateDirectory(Registry(Role));
        foreach (var FilePath in Directory.EnumerateFiles(Registry(Role), "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var Descriptor = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
                using var Process = System.Diagnostics.Process.GetProcessById(Descriptor["processId"]!.GetValue<int>());
                if (!Process.HasExited)
                {
                    Sessions.Add(Descriptor);
                }
            }
            catch (Exception Error) when (Error is IOException or ArgumentException or JsonException)
            {
                // Registrations belonging to stopped processes are not live sessions.
            }
        }
        return Sessions;
    }

    private static JsonObject BootstrapTools(string Role)
    {
        var Result = JsonNode.Parse("""
            {"tools":[
              {"name":"game.sessions.list","description":"Discover live Game sessions owned by this user.",
               "inputSchema":{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false},
               "outputSchema":{"type":"object","required":["schemaVersion","sessions"]},
               "annotations":{"readOnlyHint":true,"destructiveHint":false,"openWorldHint":false}},
              {"name":"game.session.attach","description":"Attach this MCP connection to one explicitly selected Game session.",
               "inputSchema":{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["sessionId"],"additionalProperties":false,
                 "properties":{"sessionId":{"type":"string","pattern":"^[0-9a-f]{32}$"}}},
               "outputSchema":{"type":"object","required":["schemaVersion","attached","session"]},
               "annotations":{"readOnlyHint":false,"destructiveHint":false,"openWorldHint":false}}
            ]}
            """.Replace("game.sessions.list", Role == "game" ? "game.sessions.list" : "projectBrowser.sessions.list", StringComparison.Ordinal)
            .Replace("game.session.attach", Role == "game" ? "game.session.attach" : "projectBrowser.session.attach", StringComparison.Ordinal)
            .Replace("Game", Role == "game" ? "Game" : "Project Browser", StringComparison.Ordinal))!.AsObject();
        if (Role == "project-browser")
        {
            Result["tools"]!.AsArray().Add(JsonNode.Parse("""
                {"name":"projectBrowser.session.launchIsolated","description":"Launch a hidden isolated engine Project Browser and bind this connection to its MCP session. Does not initialize EditorRuntime, World or project Modules.",
                 "inputSchema":{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,
                    "properties":{"recentFile":{"type":"string"}}},
                 "outputSchema":{"type":"object","required":["schemaVersion","attached","session"]},
                 "annotations":{"readOnlyHint":false,"destructiveHint":false,"openWorldHint":true},
                 "_meta":{"sharpengine/capability":{"schemaVersion":1,"effect":"external","available":true}}}
                """));
        }
        return Result;
    }

    // No implicit foreground target. The bridge discovers sessions through MCP
    // until the caller selects one, then proxies that session for its lifetime.
    internal static int RunBridge(string Role)
    {
        var Id = Argument("--mcp-session-id");
        NamedPipeClientStream? Pipe = null;
        StreamReader? Reader = null;
        StreamWriter? Writer = null;
        JsonObject Attach(string SessionId)
        {
            var ValidId = Guid.TryParseExact(SessionId, "N", out var Parsed);
            if (Pipe is not null || !ValidId)
            {
                throw new ArgumentException("Select one valid UI session ID per connection");
            }
            var Descriptor = ListSessions(Role).FirstOrDefault(Session => Session?["sessionId"]?.GetValue<string>() == Parsed.ToString("N"))
                ?? throw new ArgumentException("UI session is not live");
            Pipe = new NamedPipeClientStream(".", Descriptor["pipeName"]!.GetValue<string>(), PipeDirection.InOut);
            Pipe.Connect(5000);
            Reader = new StreamReader(Pipe, new UTF8Encoding(false), false, 4096, true);
            Writer = new StreamWriter(Pipe, new UTF8Encoding(false), 4096, true)
            {
                AutoFlush = true,
            };
            return new JsonObject
            {
                ["schemaVersion"] = 1,
                ["attached"] = true,
                ["session"] = Descriptor.DeepClone(),
            };
        }
        JsonObject LaunchBrowser(JsonObject Arguments)
        {
            var DirectoryPath = Path.GetDirectoryName(Environment.ProcessPath!)!;
            var Configuration = Directory.GetParent(DirectoryPath)!.Name;
            DirectoryInfo? Root = new DirectoryInfo(DirectoryPath);
            while (Root is not null && !File.Exists(Path.Combine(Root.FullName, "engine.json")))
            {
                Root = Root.Parent;
            }
            var EngineRoot = Root?.FullName ?? throw new DirectoryNotFoundException("Engine root was not found");
            using var Manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(EngineRoot, "engine.json")));
            var Binaries = Manifest.RootElement.GetProperty("paths").GetProperty("binaries").GetString()!
                .Replace("{platform}", "Win64").Replace("{configuration}", Configuration).Replace("{target}", "Editor");
            var Session = Guid.NewGuid().ToString("N");
            var Start = new ProcessStartInfo(Path.Combine(EngineRoot, Binaries, "Engine.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var Argument in new[]
                     {
                         "--project-browser", "--mcp-isolated", "--mcp-session-id", Session,
                     })
            {
                Start.ArgumentList.Add(Argument);
            }
            var RecentFile = Arguments["recentFile"]?.GetValue<string>();
            if (RecentFile is not null)
            {
                Start.ArgumentList.Add("--browser-recent-file");
                Start.ArgumentList.Add(Path.GetFullPath(RecentFile));
            }
            var Process = System.Diagnostics.Process.Start(Start) ?? throw new IOException("Could not launch Project Browser");
            _ = DrainLaunchedProcess(Process);
            var Deadline = DateTime.UtcNow.AddSeconds(30);
            while (!ListSessions(Role).Any(Item => Item!["sessionId"]!.GetValue<string>() == Session) && DateTime.UtcNow < Deadline)
            {
                if (Process.HasExited)
                {
                    throw new IOException($"Project Browser exited with code {Process.ExitCode}");
                }
                Thread.Sleep(50);
            }
            return Attach(Session);
        }
        JsonNode Bootstrap(string Method, JsonObject Parameters)
        {
            if (Method == "initialize")
            {
                var Result = Initialize(Role);
                Result["capabilities"]!["tools"]!["listChanged"] = true;
                Result["capabilities"]!["resources"]!["listChanged"] = true;
                Result["instructions"] = Role == "game"
                    ? "List Game sessions with game.sessions.list, then attach one using game.session.attach. Discover its UI tools after attachment."
                    : "List Project Browser sessions with projectBrowser.sessions.list, then attach one using projectBrowser.session.attach. Discover its full tool and resource catalog after attachment.";
                return Result;
            }
            if (Method == "ping")
            {
                return new JsonObject();
            }
            if (Method == "tools/list")
            {
                return BootstrapTools(Role);
            }
            if (Method == "resources/list")
            {
                return JsonNode.Parse("""{"resources":[]}""")!;
            }
            if (Method != "tools/call")
            {
                throw new ArgumentException("Attach a Game session before using UI resources");
            }
            var Name = Parameters["name"]?.GetValue<string>();
            var ListName = Role == "game" ? "game.sessions.list" : "projectBrowser.sessions.list";
            var AttachName = Role == "game" ? "game.session.attach" : "projectBrowser.session.attach";
            var ResultState = Name switch
            {
                var Value when Value == ListName => new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["sessions"] = ListSessions(Role),
                },
                var Value when Value == AttachName => Attach(Parameters["arguments"]!["sessionId"]!.GetValue<string>()),
                "projectBrowser.session.launchIsolated" when Role == "project-browser" => LaunchBrowser(Parameters["arguments"]?.AsObject() ?? new JsonObject()),
                _ => throw new ArgumentException("Unknown session tool"),
            };
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = ResultState.ToJsonString(),
                }),
                ["structuredContent"] = ResultState,
                ["isError"] = false,
            };
        }
        try
        {
            if (Id is not null)
            {
                Attach(Id);
            }
            while (Console.ReadLine() is { } Line)
            {
                if (Pipe is null)
                {
                    var Response = Dispatch(Line, Bootstrap);
                    if (Response is not null)
                    {
                        Console.WriteLine(Response);
                    }
                    if (Pipe is not null)
                    {
                        Console.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}");
                        Console.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/resources/list_changed\"}");
                    }
                }
                else
                {
                    Writer!.WriteLine(Line);
                    if (JsonNode.Parse(Line)?["id"] is not null)
                    {
                        Console.WriteLine(Reader!.ReadLine());
                    }
                }
                Console.Out.Flush();
            }
        }
        finally
        {
            Writer?.Dispose();
            Reader?.Dispose();
            Pipe?.Dispose();
        }
        return 0;
    }

    public void Dispose()
    {
        Cancellation.Cancel();
        while (Requests.TryDequeue(out var Request))
        {
            Request.Completion.TrySetCanceled();
        }
        try
        {
            Server.Wait(TimeSpan.FromSeconds(2));
            File.Delete(RegistrationPath);
        }
        catch (IOException)
        {
        }
        catch (AggregateException)
        {
        }
        Cancellation.Dispose();
    }

    internal static async Task DrainLaunchedProcess(Process Process)
    {
        using (Process)
        {
            var Output = Process.StandardOutput.ReadToEndAsync();
            var Errors = Process.StandardError.ReadToEndAsync();
            await Process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(Output, Errors).ConfigureAwait(false);
        }
    }
}
