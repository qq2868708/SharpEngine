using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Interop;

namespace SharpEngine.Host;

// The pipe thread only transports messages. Native UI calls and all mutations
// execute in Pump on the Game thread, between completed presentations.
internal sealed class GameMcpHost : IDisposable
{
    private sealed record Pending(string Line, TaskCompletionSource<string?> Completion);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string Registry
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "SharpEngine", "GameMcpSessions");
        }
    }
    private readonly ConcurrentQueue<Pending> Requests = new();
    private readonly CancellationTokenSource Cancellation = new();
    private readonly nint Api;
    private readonly RenderTelemetry RenderTelemetryService;
    private readonly Func<AssetRegistry?> Assets;
    private readonly string SessionId;
    private readonly string PipeName;
    private readonly string RegistrationPath;
    private readonly Task Server;

    internal GameMcpHost(nint Api, RenderTelemetry RenderTelemetryService,
        Func<AssetRegistry?> Assets)
    {
        this.Api = Api;
        this.RenderTelemetryService = RenderTelemetryService;
        this.Assets = Assets;
        SessionId = Guid.TryParseExact(Argument("--mcp-session-id"), "N", out var Id)
            ? Id.ToString("N") : Guid.NewGuid().ToString("N");
        PipeName = "SharpEngine.Game." + SessionId;
        Directory.CreateDirectory(Registry);
        RegistrationPath = Path.Combine(Registry, SessionId + ".json");
        var Temporary = RegistrationPath + ".tmp";
        File.WriteAllText(Temporary, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            SessionId,
            Mode = "game",
            PipeName,
            ProcessId = Environment.ProcessId,
            ProjectManifest = Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST"),
            UiAvailable = true,
        }, JsonOptions));
        File.Move(Temporary, RegistrationPath, true);
        SharpEngine.Logging.SharpLog.Info("MCP", "Game session registered: " + RegistrationPath);
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
            "initialize" => Initialize(),
            "ping" => new JsonObject(),
            "tools/list" => ToolList(),
            "tools/call" => CallTool(Parameters),
            "resources/list" => JsonNode.Parse("""{"resources":[{"uri":"sharpengine://ui","name":"Runtime UI","mimeType":"application/json"},{"uri":"sharpengine://render","name":"Render telemetry","mimeType":"application/json"}]}""")!,
            "resources/templates/list" => JsonNode.Parse("""{"resourceTemplates":[{"uriTemplate":"sharpengine://ui/frames/{frameId}/{hostId}.png","name":"Presented runtime frame","mimeType":"image/png"}]}""")!,
            "resources/read" => ReadResource(Parameters),
            _ => throw new ArgumentException($"Unknown MCP method '{Method}'"),
        };
    }

    private static JsonObject Initialize()
    {
        return JsonNode.Parse("""
            {"protocolVersion":"2025-06-18","capabilities":{"tools":{},"resources":{}},
             "serverInfo":{"name":"sharpengine-game","version":"0.2.0"},
             "instructions":"Discover ui.describe before coordinate input. Read the PNG resource with the same frameId. Input changes transient runtime state only; it does not save assets."}
            """)!.AsObject();
    }

    private static JsonObject ToolList()
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
        foreach (var Tool in Tools["tools"]!.AsArray().Select(Node => Node!.AsObject()))
        {
            var Name = Tool["name"]!.GetValue<string>();
            var Read = Name is "ui.describe" or "render.describe";
            Tool["inputSchema"]!["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            Tool["outputSchema"] = JsonNode.Parse("""{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"frameId":{"type":"integer"},"diagnostics":{"type":"array"}}}""");
            Tool["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = Read,
                ["destructiveHint"] = false,
                ["idempotentHint"] = Read,
                ["openWorldHint"] = false,
            };
            Tool["_meta"] = new JsonObject
            {
                ["sharpengine/capability"] = new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["effect"] = Read ? "read" : "runtime",
                    ["available"] = true,
                    ["state"] = "transient",
                    ["context"] = "runtime",
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
        if (Definition["inputSchema"]?["required"] is JsonArray Required)
        {
            foreach (var Field in Required)
            {
                if (!Arguments.ContainsKey(Field!.GetValue<string>()))
                {
                    throw new ArgumentException($"Missing field '{Field}'");
                }
            }
        }
        if (Name == "render.describe")
        {
            var RenderState = RenderTelemetryService.Describe(Assets());
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
        if (Uri == "sharpengine://render")
        {
            return Resource(Uri, "application/json", "text",
                RenderTelemetryService.Describe(Assets()).ToJsonString());
        }
        const string Prefix = "sharpengine://ui/frames/";
        var Parts = Uri.StartsWith(Prefix, StringComparison.Ordinal) ? Uri[Prefix.Length..].Split('/') : [];
        if (Parts.Length != 2 || Parts[1] != "game.png" || !ulong.TryParse(Parts[0], out var FrameId))
        {
            throw new ArgumentException("Unknown frame resource URI");
        }
        var Table = (NativeEngineApiTable*)Api;
        var Host = Utf8("game");
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
                    throw new InvalidOperationException("Frame readback changed during one Game transaction");
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

    private static JsonArray ListSessions()
    {
        var Sessions = new JsonArray();
        Directory.CreateDirectory(Registry);
        foreach (var FilePath in Directory.EnumerateFiles(Registry, "*.json").Order(StringComparer.Ordinal))
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

    private static JsonObject BootstrapTools()
    {
        return JsonNode.Parse("""
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
            """)!.AsObject();
    }

    // No implicit foreground target. The bridge discovers sessions through MCP
    // until the caller selects one, then proxies that session for its lifetime.
    internal static int RunBridge()
    {
        var Id = Argument("--mcp-session-id");
        NamedPipeClientStream? Pipe = null;
        StreamReader? Reader = null;
        StreamWriter? Writer = null;
        JsonObject Attach(string SessionId)
        {
            if (Pipe is not null || !Guid.TryParseExact(SessionId, "N", out var Parsed))
            {
                throw new ArgumentException("Select one valid Game session ID per connection");
            }
            var Descriptor = ListSessions().FirstOrDefault(Session => Session?["sessionId"]?.GetValue<string>() == Parsed.ToString("N"))
                ?? throw new ArgumentException("Game session is not live");
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
        JsonNode Bootstrap(string Method, JsonObject Parameters)
        {
            if (Method == "initialize")
            {
                var Result = Initialize();
                Result["capabilities"]!["tools"]!["listChanged"] = true;
                Result["capabilities"]!["resources"]!["listChanged"] = true;
                Result["instructions"] = "List Game sessions with game.sessions.list, then attach one using game.session.attach. Discover its UI tools after attachment.";
                return Result;
            }
            if (Method == "ping")
            {
                return new JsonObject();
            }
            if (Method == "tools/list")
            {
                return BootstrapTools();
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
            var ResultState = Name switch
            {
                "game.sessions.list" => new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["sessions"] = ListSessions(),
                },
                "game.session.attach" => Attach(Parameters["arguments"]!["sessionId"]!.GetValue<string>()),
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
}
