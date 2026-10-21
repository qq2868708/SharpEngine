using System.Diagnostics;
using System.Text.Json.Nodes;

internal sealed class McpClient : IDisposable
{
    private readonly Process Process;
    private int Sequence;
    private JsonArray Tools = new();
    private string? TraceFile;

    internal void TraceTo(string PathValue)
    {
        TraceFile = PathValue;
    }

    internal McpClient(string Executable, params string[] Arguments)
    {
        var Start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var Argument in Arguments)
        {
            Start.ArgumentList.Add(Argument);
        }
        Process = System.Diagnostics.Process.Start(Start)!;
        _ = Process.StandardError.ReadToEndAsync();
    }

    internal JsonObject Call(string Method, JsonObject? Parameters = null)
    {
        var Id = ++Sequence;
        var Request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Id,
            ["method"] = Method,
            ["params"] = Parameters,
        };
        Trace("request", Request);
        Process.StandardInput.WriteLine(Request.ToJsonString());
        Process.StandardInput.Flush();
        var Deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < Deadline)
        {
            var Line = Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(180)).GetAwaiter().GetResult();
            if (Line is null)
            {
                throw new IOException("MCP connection ended before its response");
            }
            var Response = JsonNode.Parse(Line)!.AsObject();
            Trace("response", Response);
            if (Response["id"]?.GetValue<int>() != Id)
            {
                continue;
            }
            if (Response["error"] is not null)
            {
                throw new InvalidOperationException(Response.ToJsonString());
            }
            return Response["result"]!.AsObject();
        }
        throw new TimeoutException("MCP response timed out");
    }

    internal JsonArray DiscoverTools()
    {
        Tools = Call("tools/list")["tools"]!.AsArray();
        return Tools;
    }

    internal JsonObject Tool(string Name, JsonObject? Arguments = null)
    {
        var Result = Call("tools/call", new JsonObject
        {
            ["name"] = Name,
            ["arguments"] = Arguments ?? new JsonObject(),
        });
        if (Result["isError"]?.GetValue<bool>() == true)
        {
            throw new InvalidOperationException(Result.ToJsonString());
        }
        return Result["structuredContent"]!.AsObject();
    }

    internal JsonObject Ui() { return Tool("ui.describe"); }

    internal JsonObject Element(JsonObject Snapshot, string HostId, string SemanticId)
    {
        return Snapshot["hosts"]!.AsArray().Single(Host => Host!["hostId"]!.GetValue<string>() == HostId)!["elements"]!
            .AsArray().Single(Element => Element!["semanticId"]?.GetValue<string>() == SemanticId)!.AsObject();
    }

    internal void Click(string HostId, string SemanticId)
    {
        var Snapshot = Ui();
        var Target = Element(Snapshot, HostId, SemanticId);
        if (Target["visible"]?.GetValue<bool>() != true || Target["hitTarget"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException("Target is not visible and hit-testable: " + SemanticId);
        }
        var Bounds = Target["bounds"]!;
        var Frame = Snapshot["frameId"]!.GetValue<ulong>();
        var Properties = Tools.Single(Tool => Tool!["name"]!.GetValue<string>() == "ui.pointer")!["inputSchema"]!["properties"]!;
        string[] Events = ["down", "up"];
        foreach (var Event in Events)
        {
            var Arguments = new JsonObject
            {
                ["hostId"] = HostId,
                ["event"] = Event,
                ["button"] = "left",
                ["x"] = (int)(Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>() / 2),
                ["y"] = (int)(Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>() / 2),
                ["expectedFrameId"] = Frame,
                ["expectedElementId"] = Target["elementId"]!.GetValue<string>(),
                ["pointerId"] = 0,
                ["modifiers"] = Properties["modifiers"]?["type"]?.GetValue<string>() == "array" ? new JsonArray() : JsonValue.Create(0),
                ["wheelX"] = 0,
                ["wheelY"] = 0,
            };
            if (Properties["buttons"] is not null)
            {
                Arguments["buttons"] = new JsonArray();
            }
            var Result = Tool("ui.pointer", Arguments);
            if (Result["accepted"]?.GetValue<bool>() != true)
            {
                throw new InvalidOperationException("Pointer was rejected: " + Result.ToJsonString());
            }
            Frame = Result["frameId"]!.GetValue<ulong>();
        }
    }

    internal void Type(string HostId, string SemanticId, string Value)
    {
        Click(HostId, SemanticId);
        void Key(string Event, int Code, string Text, bool Control)
        {
            var Snapshot = Ui();
            var Target = Element(Snapshot, HostId, SemanticId);
            if (Target["focused"]?.GetValue<bool>() != true)
            {
                throw new InvalidOperationException("Text entry requires observed focus on " + SemanticId);
            }
            var Properties = Tools.Single(Tool => Tool!["name"]!.GetValue<string>() == "ui.key")!["inputSchema"]!["properties"]!;
            var Result = Tool("ui.key", new JsonObject
            {
                ["hostId"] = HostId,
                ["event"] = Event,
                ["key"] = Code,
                ["text"] = Text,
                ["expectedFrameId"] = Snapshot["frameId"]!.GetValue<ulong>(),
                ["modifiers"] = Properties["modifiers"]?["type"]?.GetValue<string>() == "array"
                    ? Control ? new JsonArray("control") : new JsonArray() :
                    JsonValue.Create(Control ? Properties["modifiers"]!["x-sharpengine-modifiers"]!["control"]!.GetValue<int>() : 0),
            });
            if (Result["accepted"]?.GetValue<bool>() != true)
            {
                throw new InvalidOperationException("Key was rejected: " + Result.ToJsonString());
            }
        }
        Key("down", 65, "", true);
        Key("up", 65, "", true);
        Key("text", 0, Value, false);
        var Actual = Element(Ui(), HostId, SemanticId)["value"]?.GetValue<string>();
        if (Actual != Value)
        {
            throw new InvalidOperationException($"Input value differs for {SemanticId}: expected '{Value}', actual '{Actual}'");
        }
    }

    internal void Capture(string DirectoryPath, string Name, string HostId)
    {
        var Snapshot = Ui();
        var Uri = $"sharpengine://ui/frames/{Snapshot["frameId"]}/{HostId}.png";
        var Resource = Call("resources/read", new JsonObject
        {
            ["uri"] = Uri,
        });
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, Name + ".json"), Snapshot.ToJsonString());
        File.WriteAllBytes(Path.Combine(DirectoryPath, Name + ".png"), Convert.FromBase64String(
            Resource["contents"]![0]!["blob"]!.GetValue<string>()));
    }

    public void Dispose()
    {
        Process.StandardInput.Close();
        if (!Process.WaitForExit(5000))
        {
            Process.Kill(true);
        }
        Process.Dispose();
    }

    private void Trace(string Direction, JsonObject Packet)
    {
        if (TraceFile is null)
        {
            return;
        }
        File.AppendAllText(TraceFile, new JsonObject
        {
            ["direction"] = Direction,
            ["packet"] = Packet.DeepClone(),
        }.ToJsonString() + Environment.NewLine);
    }
}
