using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

internal static class McpUiValidation
{
    internal static string CreateConfiguration(string Root)
    {
        var DirectoryPath = Path.Combine(Root, "Saved", "Validation", "Mcp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        var ConfigurationPath = Path.Combine(DirectoryPath, "Mcp.json");
        File.WriteAllText(ConfigurationPath, Configuration(FreePort()));
        return ConfigurationPath;
    }

    private static int FreePort()
    {
        using var Listener = new TcpListener(IPAddress.Loopback, 0);
        Listener.Start();
        return ((IPEndPoint)Listener.LocalEndpoint).Port;
    }

    private static string Configuration(int Port)
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["port"] = Port,
        }.ToJsonString();
    }

    internal static void Run(string Root, Action<bool, string> Require,
        Func<int, string, JsonObject, JsonObject> Tool,
        Func<int, string, JsonObject?, JsonObject> Call)
    {
        var Sequence = 3000;
        var Evidence = Path.Combine(Root, "Saved", "Validation", "Mcp");
        Directory.CreateDirectory(Evidence);
        JsonObject Invoke(string Name, JsonObject? Arguments = null)
        {
            var Response = Tool(Sequence++, Name, Arguments ?? new JsonObject());
            return Response["result"]?["structuredContent"]?.AsObject() ??
                throw new InvalidDataException(Response.ToJsonString());
        }
        JsonObject State() { return Invoke("editor.mcp.describe"); }
        JsonObject Read() { return Invoke("ui.describe"); }
        JsonObject Host(JsonObject Snapshot, string HostId)
        {
            return Snapshot["hosts"]!.AsArray().Select(Node => Node!.AsObject())
                .Single(Node => Node["hostId"]!.GetValue<string>() == HostId);
        }
        JsonObject Element(JsonObject Snapshot, string HostId, string Id)
        {
            return Host(Snapshot, HostId)["elements"]!.AsArray().Select(Node => Node!.AsObject())
                .Single(Node => Node["semanticId"]?.GetValue<string>() == Id);
        }
        void AwaitWindow(bool Present)
        {
            for (var Attempt = 0; Attempt < 40; Attempt++)
            {
                var Exists = Read()["hosts"]!.AsArray().Any(Node => Node!["hostId"]!.GetValue<string>() == "settings.mcp");
                if (Exists == Present)
                {
                    return;
                }
                Thread.Sleep(25);
            }
            throw new InvalidOperationException($"MCP settings window did not reach present={Present}");
        }
        void Click(string HostId, string Id)
        {
            var Snapshot = Read();
            var Target = Element(Snapshot, HostId, Id);
            Require(Target["visible"]!.GetValue<bool>() && Target["hitTarget"]!.GetValue<bool>(),
                $"{Id} must be visible and hit-testable");
            var Bounds = Target["bounds"]!;
            var Frame = Snapshot["frameId"]!.GetValue<ulong>();
            foreach (var Event in new[] { "down", "up" })
            {
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
                    ["expectedFrameId"] = Frame,
                    ["expectedElementId"] = Target["elementId"]!.GetValue<string>(),
                });
                Require(Result["accepted"]?.GetValue<bool>() == true, $"Coordinate {Event} must reach {Id}: {Result}");
                Frame = Result["frameId"]!.GetValue<ulong>();
            }
        }
        void Key(string Event, uint KeyCode, string Text, bool Control = false)
        {
            JsonObject? Result = null;
            for (var Attempt = 0; Attempt < 6; Attempt++)
            {
                var Snapshot = Read();
                Require(Element(Snapshot, "settings.mcp", "mcp-port")["focused"]!.GetValue<bool>(),
                    "Port typing must retain real input focus");
                Result = Invoke("ui.key", new JsonObject
                {
                    ["hostId"] = "settings.mcp",
                    ["event"] = Event,
                    ["key"] = KeyCode,
                    ["text"] = Text,
                    ["modifiers"] = Control ? new JsonArray("control") : new JsonArray(),
                    ["expectedFrameId"] = Snapshot["frameId"]!.GetValue<ulong>(),
                });
                if (Result["diagnostics"]?.AsArray().FirstOrDefault()?["code"]?.GetValue<string>() != "UI_FRAME_STALE")
                {
                    break;
                }
            }
            Require(Result?["accepted"]?.GetValue<bool>() == true, $"Port key input must be accepted: {Result}");
        }
        void TypePort(string Text)
        {
            Click("settings.mcp", "mcp-port");
            Key("down", 0x41, "", Control: true);
            Key("up", 0x41, "", Control: true);
            foreach (var Character in Text)
            {
                Key("text", 0, Character.ToString());
            }
            Require(Element(Read(), "settings.mcp", "mcp-port")["value"]!.GetValue<string>() == Text,
                "Port field must preserve every typed character");
        }
        void Capture(string Name)
        {
            var Snapshot = Read();
            File.WriteAllText(Path.Combine(Evidence, Name + ".json"), Snapshot.ToJsonString());
            foreach (var HostNode in Snapshot["hosts"]!.AsArray())
            {
                var HostId = HostNode!["hostId"]!.GetValue<string>();
                var Frame = Call(Sequence++, "resources/read", new JsonObject
                {
                    ["uri"] = $"sharpengine://ui/frames/{Snapshot["frameId"]}/{HostId}.png",
                });
                var Blob = Frame["result"]?["contents"]?[0]?["blob"]?.GetValue<string>() ??
                    throw new InvalidDataException(Frame.ToJsonString());
                File.WriteAllBytes(Path.Combine(Evidence, Name + "-" + HostId + ".png"), Convert.FromBase64String(Blob));
            }
        }
        using var Client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        JsonObject HttpCall(int Port, string Method)
        {
            using var Content = new StringContent(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Sequence++,
                ["method"] = Method,
            }.ToJsonString(), Encoding.UTF8, "application/json");
            using var Response = Client.PostAsync($"http://127.0.0.1:{Port}/mcp", Content).GetAwaiter().GetResult();
            Require(Response.IsSuccessStatusCode, $"HTTP MCP {Method} must succeed: {Response.StatusCode}");
            return JsonNode.Parse(Response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
        }
        void RequireEndpointClosed(int Port)
        {
            try
            {
                using var Response = Client.GetAsync($"http://127.0.0.1:{Port}/mcp").GetAwaiter().GetResult();
                Require(Response.StatusCode != HttpStatusCode.MethodNotAllowed,
                    "The previous MCP endpoint must stop listening after restart");
            }
            catch (HttpRequestException)
            {
                Require(true, "The previous MCP endpoint refused connections");
            }
        }
        var Initial = State();
        var InitialPort = Initial["activePort"]?.GetValue<int>() ?? throw new InvalidDataException(Initial.ToJsonString());
        var ConfigurationPath = Initial["configurationPath"]!.GetValue<string>();
        var InitialText = File.ReadAllText(ConfigurationPath);
        var InitialRevision = Initial["revision"]!.GetValue<ulong>();
        Require(HttpCall(InitialPort, "initialize")["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "sharpengine",
            "The toolbar port must serve the engine MCP protocol");
        Require(HttpCall(InitialPort, "tools/list")["result"]?["tools"]?.AsArray().Any(Node =>
            Node?["name"]?.GetValue<string>() == "editor.mcp.configure") == true,
            "HTTP must share typed Editor capabilities with the stdio bridge");
        var Toolbar = Element(Read(), "main", "toolbar-tool-mcp");
        Require(Toolbar["text"]!.GetValue<string>().Contains(InitialPort.ToString(), StringComparison.Ordinal),
            "The visible toolbar button must display the actual port");
        Capture("toolbar");
        Click("main", "toolbar-tool-mcp");
        AwaitWindow(true);
        Capture("settings");
        TypePort("12345");
        Click("settings.mcp", "mcp-cancel");
        AwaitWindow(false);
        Require(File.ReadAllText(ConfigurationPath) == InitialText && State()["revision"]!.GetValue<ulong>() == InitialRevision,
            "Cancel must discard draft input without saving or restarting MCP");

        Click("main", "toolbar-tool-mcp");
        AwaitWindow(true);
        TypePort("0");
        Click("settings.mcp", "mcp-confirm");
        Require(State()["settingsOpen"]!.GetValue<bool>() && State()["activePort"]!.GetValue<int>() == InitialPort &&
            File.ReadAllText(ConfigurationPath) == InitialText,
            "Invalid port confirmation must retain the running endpoint and saved configuration");

        using (var Occupied = new HttpListener())
        {
            var OccupiedPort = FreePort();
            Occupied.Prefixes.Add($"http://127.0.0.1:{OccupiedPort}/");
            Occupied.Start();
            TypePort(OccupiedPort.ToString());
            Click("settings.mcp", "mcp-confirm");
            Require(State()["activePort"]!.GetValue<int>() == InitialPort && File.ReadAllText(ConfigurationPath) == InitialText,
                "An occupied port must preserve the previous running server and savepoint");
            Require(HttpCall(InitialPort, "ping")["result"] is JsonObject, "Old MCP service must survive a failed port change");
            Capture("occupied-port");
        }

        var NextPort = FreePort();
        TypePort(NextPort.ToString());
        Click("settings.mcp", "mcp-confirm");
        AwaitWindow(false);
        Require(State()["activePort"]!.GetValue<int>() == NextPort &&
            JsonNode.Parse(File.ReadAllText(ConfigurationPath))?["port"]?.GetValue<int>() == NextPort,
            "Visible confirmation must save the port and restart MCP immediately");
        Require(HttpCall(NextPort, "ping")["result"] is JsonObject, "New endpoint must accept MCP requests immediately");
        RequireEndpointClosed(InitialPort);
        Require(Element(Read(), "main", "toolbar-tool-mcp")["text"]!.GetValue<string>().Contains(NextPort.ToString(), StringComparison.Ordinal),
            "Toolbar must update to the new active port");
        Capture("restarted");

        var Stale = Invoke("editor.mcp.configure", new JsonObject
        {
            ["port"] = InitialPort,
            ["expectedRevision"] = InitialRevision,
        });
        Require(Stale["success"]?.GetValue<bool>() == false && Stale["diagnostics"]?[0]?["code"]?.GetValue<string>() == "MCP_REVISION_CONFLICT",
            "Typed MCP configuration must reject stale revisions");
        var BeforeRestart = State()["revision"]!.GetValue<ulong>();
        Click("main", "toolbar-tool-mcp");
        AwaitWindow(true);
        Click("settings.mcp", "mcp-confirm");
        AwaitWindow(false);
        Require(State()["revision"]!.GetValue<ulong>() > BeforeRestart && HttpCall(NextPort, "ping")["result"] is JsonObject,
            "Confirming the existing port must also restart and rebind successfully");

        var Attributes = File.GetAttributes(ConfigurationPath);
        var BeforeFailedSave = File.ReadAllText(ConfigurationPath);
        try
        {
            File.SetAttributes(ConfigurationPath, Attributes | FileAttributes.ReadOnly);
            var FailedSave = Invoke("editor.mcp.configure", new JsonObject
            {
                ["port"] = FreePort(),
                ["expectedRevision"] = State()["revision"]!.GetValue<ulong>(),
            });
            Require(FailedSave["success"]?.GetValue<bool>() == false && State()["activePort"]!.GetValue<int>() == NextPort &&
                File.ReadAllText(ConfigurationPath) == BeforeFailedSave,
                "A failed configuration save must retain the running endpoint and persisted port");
            Require(HttpCall(NextPort, "ping")["result"] is JsonObject, "MCP must remain available after a save failure");
        }
        finally
        {
            File.SetAttributes(ConfigurationPath, Attributes);
        }

        using (var Notification = new StringContent("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", Encoding.UTF8, "application/json"))
        using (var Response = Client.PostAsync($"http://127.0.0.1:{NextPort}/mcp", Notification).GetAwaiter().GetResult())
        {
            Require(Response.StatusCode == HttpStatusCode.Accepted, "HTTP notifications must return 202 without a JSON-RPC response");
        }
        using (var Response = Client.GetAsync($"http://127.0.0.1:{NextPort}/mcp").GetAwaiter().GetResult())
        {
            Require(Response.StatusCode == HttpStatusCode.MethodNotAllowed, "Stateless HTTP transport must explicitly reject SSE GET");
        }
        using (var Request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{NextPort}/mcp"))
        {
            Request.Headers.Add("Origin", "https://untrusted.example");
            Request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var Response = Client.Send(Request);
            Require(Response.StatusCode == HttpStatusCode.Forbidden, "HTTP MCP must reject foreign browser origins");
        }

        void WaitState(Func<JsonObject, bool> Predicate)
        {
            for (var Attempt = 0; Attempt < 100; Attempt++)
            {
                if (Predicate(State()))
                {
                    return;
                }
                Thread.Sleep(25);
            }
            throw new InvalidOperationException("MCP configuration reload did not commit the expected state: " + State());
        }
        var Saved = File.ReadAllText(ConfigurationPath);
        File.WriteAllText(ConfigurationPath, Saved + "\n");
        WaitState(Value => Value["diagnostic"] is null);
        File.WriteAllText(ConfigurationPath, "{\"schemaVersion\":99,\"port\":1}");
        WaitState(Value => Value["diagnostic"] is not null);
        Require(State()["activePort"]!.GetValue<int>() == NextPort && HttpCall(NextPort, "ping")["result"] is JsonObject,
            "Invalid external configuration must keep the last valid live endpoint");
        var ReloadPort = FreePort();
        File.WriteAllText(ConfigurationPath, Configuration(ReloadPort));
        WaitState(Value => Value["activePort"]?.GetValue<int>() == ReloadPort);
        Require(HttpCall(ReloadPort, "ping")["result"] is JsonObject,
            "A valid external configuration must atomically reload and restart MCP");
        RequireEndpointClosed(NextPort);
        File.WriteAllText(ConfigurationPath, Saved);
        WaitState(Value => Value["activePort"]?.GetValue<int>() == NextPort);
        File.WriteAllText(Path.Combine(Evidence, "final-state.json"), State().ToJsonString());
    }
}
