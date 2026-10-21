using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Logging;

namespace SharpEngine.Editor;

internal sealed record McpEditorSessionDescriptor(
    int SchemaVersion,
    string SessionId,
    string Mode,
    string PipeName,
    int ProcessId,
    string EngineRoot,
    string? ProjectManifest,
    string ProjectName,
    DateTimeOffset StartedAtUtc,
    bool UiAvailable);

internal sealed class McpSessionRegistration : IDisposable
{
    private readonly string _path;
    private bool _disposed;

    internal McpSessionRegistration(string path, McpEditorSessionDescriptor descriptor)
    {
        _path = path;
        Descriptor = descriptor;
    }

    internal McpEditorSessionDescriptor Descriptor { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { File.Delete(_path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class McpSessionRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SharpEngine", "McpSessions");

    internal static McpSessionRegistration Register(EditorRuntime runtime)
    {
        Directory.CreateDirectory(DirectoryPath);
        var requestedId = CommandLineValue("--mcp-session-id");
        var sessionId = Guid.TryParseExact(requestedId, "N", out var parsed)
            ? parsed.ToString("N") : Guid.NewGuid().ToString("N");
        var mode = Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(argument, "--mcp-isolated", StringComparison.Ordinal))
            ? "isolated" : "existing";
        var pipeName = $"SharpEngine.Editor.{sessionId}";
        var projectName = runtime.ProjectManifest is null
            ? "Engine workspace"
            : Path.GetFileNameWithoutExtension(runtime.ProjectManifest);
        var descriptor = new McpEditorSessionDescriptor(1, sessionId, mode,
            pipeName, Environment.ProcessId, runtime.EngineRoot,
            runtime.ProjectManifest, projectName, DateTimeOffset.UtcNow,
            runtime.HasLiveUi);
        var path = Path.Combine(DirectoryPath, sessionId + ".json");
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(descriptor, JsonOptions),
            new UTF8Encoding(false));
        File.Move(temporary, path, true);
        return new McpSessionRegistration(path, descriptor);
    }

    internal static IReadOnlyList<McpEditorSessionDescriptor> List()
    {
        Directory.CreateDirectory(DirectoryPath);
        var result = new List<McpEditorSessionDescriptor>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json")
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            try
            {
                var descriptor = JsonSerializer.Deserialize<McpEditorSessionDescriptor>(
                    File.ReadAllText(path), JsonOptions);
                if (descriptor is null || descriptor.SchemaVersion != 1 ||
                    !ProcessIsAlive(descriptor.ProcessId))
                {
                    File.Delete(path);
                    continue;
                }
                result.Add(descriptor);
            }
            catch (JsonException)
            {
                TryDelete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException)
            {
                TryDelete(path);
            }
        }
        return result.OrderBy(session => session.ProjectName, StringComparer.Ordinal)
            .ThenBy(session => session.StartedAtUtc)
            .ThenBy(session => session.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    internal static McpEditorSessionDescriptor? Find(string sessionId) =>
        List().FirstOrDefault(session => string.Equals(session.SessionId,
            sessionId, StringComparison.Ordinal));

    private static bool ProcessIsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static string? CommandLineValue(string name)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 0; index + 1 < arguments.Length; index++)
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
                return arguments[index + 1];
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class EditorMcpSessionHost : IDisposable
{
    private sealed record PendingRequest(string Line,
        TaskCompletionSource<string?> Completion);

    private readonly EditorRuntime _runtime;
    private readonly ConcurrentQueue<PendingRequest> _requests = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly McpSessionRegistration _registration;
    private readonly Task _serverTask;
    private bool _disposed;

    internal EditorMcpSessionHost(EditorRuntime runtime)
    {
        _runtime = runtime;
        _registration = McpSessionRegistry.Register(runtime);
        _serverTask = Task.Run(RunServerAsync);
        SharpLog.Info("MCP", "Session started", new Dictionary<string, string>
        {
            ["sessionId"] = _registration.Descriptor.SessionId,
            ["mode"] = _registration.Descriptor.Mode,
            ["pipe"] = _registration.Descriptor.PipeName,
        });
    }

    internal McpEditorSessionDescriptor Descriptor => _registration.Descriptor;

    internal async Task<string?> RequestAsync(string Line, CancellationToken Token)
    {
        var Completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var Registration = Token.Register(() => Completion.TrySetCanceled(Token));
        _requests.Enqueue(new PendingRequest(Line, Completion));
        return await Completion.Task.ConfigureAwait(false);
    }

    internal void Pump()
    {
        while (_requests.TryDequeue(out var request))
        {
            if (request.Completion.Task.IsCompleted)
            {
                continue;
            }
            try
            {
                request.Completion.TrySetResult(
                    McpEndpoint.ProcessLine(_runtime, request.Line));
            }
            catch (Exception exception)
            {
                request.Completion.TrySetException(exception);
            }
        }
    }

    private async Task RunServerAsync()
    {
        var cancellation = _cancellation.Token;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _registration.Descriptor.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false),
                    false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false),
                    4096, leaveOpen: true)
                { AutoFlush = true };
                while (pipe.IsConnected && !cancellation.IsCancellationRequested &&
                       await reader.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
                {
                    var completion = new TaskCompletionSource<string?>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _requests.Enqueue(new PendingRequest(line, completion));
                    var response = await completion.Task.WaitAsync(cancellation)
                        .ConfigureAwait(false);
                    if (response is not null)
                        await writer.WriteLineAsync(response).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellation.IsCancellationRequested)
            {
                // The bridge disconnected. Accept one fresh connection.
            }
            catch (Exception exception) when (!cancellation.IsCancellationRequested)
            {
                SharpLog.Error("MCP", "Session pipe failed", exception);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration.Dispose();
        _cancellation.Cancel();
        while (_requests.TryDequeue(out var request))
            request.Completion.TrySetCanceled(_cancellation.Token);
        try { _serverTask.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cancellation.Dispose();
    }
}

internal sealed class McpStdioBridge : IDisposable
{
    private const string ProtocolVersion = "2025-06-18";
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private McpEditorSessionDescriptor? _session;
    private bool _everAttached;
    private bool _emitListChanged;

    internal static int Run()
    {
        using var bridge = new McpStdioBridge();
        while (Console.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = bridge.Dispatch(line);
            if (response is not null)
            {
                Console.WriteLine(response);
                Console.Out.Flush();
            }
            if (bridge._emitListChanged)
            {
                bridge._emitListChanged = false;
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}");
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/resources/list_changed\"}");
                Console.Out.Flush();
            }
        }
        return 0;
    }

    private string? Dispatch(string line)
    {
        using var document = JsonDocument.Parse(line);
        var request = document.RootElement;
        var hasId = request.TryGetProperty("id", out var id);
        var method = request.TryGetProperty("method", out var methodElement)
            ? methodElement.GetString() : null;
        if (method is null)
            return hasId ? Error(id, -32600, "Request method is required") : null;

        if (_pipe is not null && !IsBootstrapRequest(request, method))
            return Proxy(line, hasId);

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => InitializeResult(),
                "ping" => new JsonObject(),
                "tools/list" => BootstrapToolsList(),
                "resources/list" => BootstrapResourcesList(),
                "resources/read" => ReadBootstrapResource(request),
                "tools/call" => CallBootstrapTool(request),
                "notifications/initialized" => null,
                _ => throw new InvalidOperationException(
                    _session is null
                        ? $"Select an Editor session before calling '{method}'"
                        : $"Unknown MCP method '{method}'"),
            };
            return hasId ? Success(id, result ?? new JsonObject()) : null;
        }
        catch (ArgumentException exception)
        {
            return hasId ? Error(id, -32602, exception.Message) : null;
        }
        catch (Exception exception)
        {
            return hasId ? Error(id, -32603, exception.Message) : null;
        }
    }

    private static bool IsBootstrapRequest(JsonElement request, string method)
    {
        if (method == "resources/read" && request.TryGetProperty("params", out var parameters) &&
            parameters.TryGetProperty("uri", out var uri))
            return uri.GetString() is "sharpengine://sessions";
        if (method != "tools/call" || !request.TryGetProperty("params", out parameters) ||
            !parameters.TryGetProperty("name", out var name)) return false;
        return name.GetString() is "editor.sessions.list" or
            "editor.session.attach" or "editor.session.launchIsolated";
    }

    private string? Proxy(string line, bool hasId)
    {
        try
        {
            _writer!.WriteLine(line);
            return hasId ? _reader!.ReadLine() ??
                throw new IOException("The selected Editor session disconnected") : null;
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    private JsonObject CallBootstrapTool(JsonElement request)
    {
        var parameters = request.GetProperty("params");
        var name = RequiredString(parameters, "name");
        var arguments = parameters.TryGetProperty("arguments", out var value)
            ? value : default;
        JsonNode result = name switch
        {
            "editor.sessions.list" => SessionListResult(),
            "editor.session.attach" => Attach(RequiredString(arguments, "sessionId")),
            "editor.session.launchIsolated" => LaunchIsolated(arguments),
            _ => throw new KeyNotFoundException($"Unknown bootstrap tool '{name}'"),
        };
        return ToolResult(result, false);
    }

    private JsonObject Attach(string sessionId)
    {
        if (_everAttached)
            throw new InvalidOperationException(
                "This MCP connection is already attached; open a new connection to select another Editor");
        var session = McpSessionRegistry.Find(sessionId) ??
            throw new ArgumentException($"Editor session '{sessionId}' is not available");
        var pipe = new NamedPipeClientStream(".", session.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(5000);
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        { AutoFlush = true };
        _session = session;
        _everAttached = true;
        _emitListChanged = true;
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["attached"] = true,
            ["session"] = JsonSerializer.SerializeToNode(session, _jsonOptions),
        };
    }

    private JsonObject LaunchIsolated(JsonElement arguments)
    {
        var projectManifest = OptionalString(arguments, "projectManifest");
        if (string.IsNullOrWhiteSpace(projectManifest))
        {
            throw new ArgumentException("EDITOR_PROJECT_REQUIRED: launchIsolated requires projectManifest");
        }
        if (projectManifest is not null)
        {
            projectManifest = Path.GetFullPath(projectManifest);
            if (!File.Exists(projectManifest))
                throw new ArgumentException($"Project manifest was not found: {projectManifest}");
        }
        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException("The Editor executable path is unavailable");
        var sessionId = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--mcp-isolated");
        start.ArgumentList.Add("--mcp-session-id");
        start.ArgumentList.Add(sessionId);
        if (projectManifest is not null)
        {
            start.ArgumentList.Add("--project");
            start.ArgumentList.Add(projectManifest);
        }
        var Launched = Process.Start(start) ??
            throw new IOException("Could not start the isolated Editor");
        _ = SharpEngine.Interop.UiMcpSessionHost.DrainLaunchedProcess(Launched);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        McpEditorSessionDescriptor? session;
        do
        {
            Thread.Sleep(50);
            session = McpSessionRegistry.Find(sessionId);
        } while (session is null && DateTime.UtcNow < deadline);
        if (session is null)
            throw new TimeoutException("The isolated Editor did not publish an MCP session");
        return Attach(sessionId);
    }

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject { ["listChanged"] = true },
            ["resources"] = new JsonObject
            { ["subscribe"] = false, ["listChanged"] = true },
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "sharpengine-session-bridge",
            ["title"] = "SharpEngine Editor session selector",
            ["version"] = "0.2.0",
        },
        ["instructions"] = "List Editor sessions, ask the user to select one, then attach or launch an isolated Editor. This connection cannot switch sessions after attachment.",
    };

    private static JsonObject BootstrapToolsList() => new()
    {
        ["tools"] = new JsonArray
        {
            BootstrapTool("editor.sessions.list", "List live Editor sessions available to this user.",
                "read", EmptyObjectSchema(), new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("schemaVersion", "sessions"),
                    ["properties"] = new JsonObject
                    {
                        ["schemaVersion"] = new JsonObject { ["const"] = 1 },
                        ["sessions"] = new JsonObject { ["type"] = "array" },
                    },
                }),
            BootstrapTool("editor.session.attach", "Attach this MCP connection to one user-selected live Editor session.",
                "runtime", new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("sessionId"),
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["sessionId"] = new JsonObject
                            { ["type"] = "string", ["pattern"] = "^[0-9a-f]{32}$" },
                    },
                }, AttachOutputSchema()),
            BootstrapTool("editor.session.launchIsolated", "Launch and attach a separate fully rendered Editor session.",
                "external", new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("projectManifest"),
                    ["properties"] = new JsonObject
                    {
                        ["projectManifest"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["minLength"] = 1,
                        },
                    },
                }, AttachOutputSchema()),
        },
    };

    private static JsonObject BootstrapTool(string name, string description,
        string effect, JsonObject inputSchema, JsonObject outputSchema) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema,
            ["outputSchema"] = outputSchema,
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = effect == "read",
                ["destructiveHint"] = false,
                ["idempotentHint"] = effect is "read" or "runtime",
                ["openWorldHint"] = effect == "external",
            },
            ["_meta"] = new JsonObject
            {
                ["sharpengine/capability"] = new JsonObject
                {
                    ["id"] = name,
                    ["schemaVersion"] = 1,
                    ["effect"] = effect,
                    ["available"] = true,
                },
            },
        };

    private static JsonObject BootstrapResourcesList() => new()
    {
        ["resources"] = new JsonArray
        {
            new JsonObject
            {
                ["uri"] = "sharpengine://sessions",
                ["name"] = "Editor sessions",
                ["description"] = "Live Editor sessions available for explicit user selection",
                ["mimeType"] = "application/json",
            },
        },
    };

    private static JsonObject ReadBootstrapResource(JsonElement request)
    {
        var uri = RequiredString(request.GetProperty("params"), "uri");
        if (uri != "sharpengine://sessions")
            throw new KeyNotFoundException($"Unknown bootstrap resource '{uri}'");
        return new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = uri,
                    ["mimeType"] = "application/json",
                    ["text"] = SessionListResult().ToJsonString(),
                },
            },
        };
    }

    private static JsonObject SessionListResult() => new()
    {
        ["schemaVersion"] = 1,
        ["sessions"] = JsonSerializer.SerializeToNode(McpSessionRegistry.List(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    private static JsonObject ToolResult(JsonNode structured, bool isError) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = structured.ToJsonString() },
        },
        ["structuredContent"] = structured.DeepClone(),
        ["isError"] = isError,
    };

    private static JsonObject EmptyObjectSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false,
    };

    private static JsonObject AttachOutputSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("schemaVersion", "attached", "session"),
        ["properties"] = new JsonObject
        {
            ["schemaVersion"] = new JsonObject { ["const"] = 1 },
            ["attached"] = new JsonObject { ["const"] = true },
            ["session"] = new JsonObject { ["type"] = "object" },
        },
    };

    private static string RequiredString(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Missing string '{property}'");
        return element.GetString()!;
    }

    private static string? OptionalString(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(property, out var element) ||
            element.ValueKind == JsonValueKind.Null) return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() :
            throw new ArgumentException($"'{property}' must be a string or null");
    }

    private static string Success(JsonElement id, JsonNode result) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = JsonNode.Parse(id.GetRawText()),
        ["result"] = result,
    }.ToJsonString();

    private static string Error(JsonElement id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = JsonNode.Parse(id.GetRawText()),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    }.ToJsonString();

    private void Disconnect()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        _reader = null;
        _writer = null;
        _pipe = null;
        _session = null;
    }

    public void Dispose() => Disconnect();
}
