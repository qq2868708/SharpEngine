using System.Text.Json;
using System.Text.Json.Nodes;
namespace SharpEngine.Editor;

/// <summary>
/// The engine's embedded MCP transport. It speaks newline-delimited JSON-RPC
/// on stdin/stdout so an MCP client can inspect and edit the live serialized
/// editor state without driving the desktop window.
/// </summary>
public static class McpEndpoint
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static int Run(EditorRuntime runtime)
    {
        while (Console.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = Dispatch(runtime, line);
            if (response is null) continue; // JSON-RPC notification.
            Console.WriteLine(response.ToJsonString(JsonOptions));
            Console.Out.Flush();
        }
        return 0;
    }

    private static JsonObject? Dispatch(EditorRuntime runtime, string line)
    {
        using var document = JsonDocument.Parse(line);
        var request = document.RootElement;
        var hasId = request.TryGetProperty("id", out var id);
        var method = request.TryGetProperty("method", out var methodElement)
            ? methodElement.GetString() : null;
        if (method is null)
            return hasId ? Error(id, -32600, "Request method is required") : null;

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => InitializeResult(),
                "ping" => new JsonObject(),
                "tools/list" => ToolsList(),
                "resources/list" => ResourcesList(),
                "resources/read" => ReadResource(runtime, request),
                "tools/call" => CallTool(runtime, request),
                "notifications/initialized" => null,
                _ => throw new InvalidOperationException($"Unknown MCP method '{method}'"),
            };
            return hasId ? Success(id, result ?? new JsonObject()) : null;
        }
        catch (Exception exception)
        {
            return hasId ? Error(id, -32603, exception.Message) : null;
        }
    }

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject(),
            ["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false },
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "sharpengine",
            ["title"] = "SharpEngine native editor",
            ["version"] = "0.1.0",
        },
    };

    private static JsonObject ToolsList() => new()
    {
        ["tools"] = new JsonArray
        {
            Tool("scene.describe", "Return the complete serialized scene, including hierarchy and resolved world matrices."),
            Tool("assets.describe", "Return the engine and project asset registry with source and importer metadata."),
            Tool("content.describe", "Return the content manager query, filters, selection, and visible assets."),
            Tool("editor.describe", "Return the combined editor snapshot containing scene and assets."),
            Tool("viewport.input", "Send a deterministic viewport navigation event through the engine input path.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["x"] = new JsonObject { ["type"] = "integer" },
                        ["y"] = new JsonObject { ["type"] = "integer" },
                        ["inputEvent"] = new JsonObject { ["type"] = "integer", ["description"] = "0 move, 1 button down, 2 button up, 3 wheel" },
                        ["buttons"] = new JsonObject { ["type"] = "integer", ["description"] = "Win32 MK_* button mask; RMB=2, MMB=16" },
                        ["wheelDelta"] = new JsonObject { ["type"] = "integer" },
                    },
                    ["required"] = new JsonArray("x", "y", "inputEvent", "buttons", "wheelDelta"),
                    ["additionalProperties"] = false,
                }),
            Tool("viewport.key", "Send a deterministic keyboard state through the viewport focus path (WASD/QE/Shift).",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["key"] = new JsonObject { ["type"] = "integer", ["description"] = "Win32 virtual-key code, for example W=87" },
                        ["pressed"] = new JsonObject { ["type"] = "boolean" },
                        ["viewportFocused"] = new JsonObject { ["type"] = "boolean" },
                    },
                    ["required"] = new JsonArray("key", "pressed", "viewportFocused"),
                    ["additionalProperties"] = false,
                }),
            Tool("viewport.tick", "Advance deterministic editor navigation by a small time step.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["deltaSeconds"] = new JsonObject { ["type"] = "number" },
                    },
                    ["required"] = new JsonArray("deltaSeconds"),
                    ["additionalProperties"] = false,
                }),
            Tool("editor.action", "Dispatch a stable editor action such as editor.select-entity:<id>, editor.save, or editor.save-scene-json.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["action"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("action"),
                    ["additionalProperties"] = false,
                }),
        },
    };

    private static JsonObject ResourcesList() => new()
    {
        ["resources"] = new JsonArray
        {
            Resource("sharpengine://scene", "Current scene", "Complete serialized scene", "application/json"),
            Resource("sharpengine://assets", "Current assets", "Engine and project asset registry", "application/json"),
            Resource("sharpengine://content", "Content manager", "Current content browser state and visible assets", "application/json"),
            Resource("sharpengine://editor", "Editor snapshot", "Combined scene and asset snapshot", "application/json"),
        },
    };

    private static JsonObject ReadResource(EditorRuntime runtime, JsonElement request)
    {
        var uri = RequiredString(request, "params", "uri");
        var text = uri switch
        {
            "sharpengine://scene" => runtime.DescribeSceneJson(),
            "sharpengine://assets" => runtime.DescribeAssetsJson(),
            "sharpengine://content" => runtime.DescribeContentJson(),
            "sharpengine://editor" => runtime.DescribeEditorJson(),
            _ => throw new KeyNotFoundException($"Unknown resource '{uri}'"),
        };
        return new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject { ["uri"] = uri, ["mimeType"] = "application/json", ["text"] = text },
            },
        };
    }

    private static JsonObject CallTool(EditorRuntime runtime, JsonElement request)
    {
        var name = RequiredString(request, "params", "name");
        var text = name switch
        {
            "scene.describe" => runtime.DescribeSceneJson(),
            "assets.describe" => runtime.DescribeAssetsJson(),
            "content.describe" => runtime.DescribeContentJson(),
            "editor.describe" => runtime.DescribeEditorJson(),
            "viewport.input" => DispatchViewportInput(runtime, request),
            "viewport.key" => DispatchViewportKey(runtime, request),
            "viewport.tick" => DispatchViewportTick(runtime, request),
            "editor.action" => DispatchAction(runtime, request),
            _ => throw new KeyNotFoundException($"Unknown tool '{name}'"),
        };
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text },
            },
            ["isError"] = false,
        };
    }

    private static string DispatchAction(EditorRuntime runtime, JsonElement request)
    {
        var action = RequiredString(request, "params", "arguments", "action");
        runtime.InvokeUiAction(action);
        return runtime.DescribeEditorJson();
    }

    private static string DispatchViewportInput(EditorRuntime runtime, JsonElement request)
    {
        var arguments = request.GetProperty("params").GetProperty("arguments");
        runtime.ViewportInput(0,
            RequiredInt(arguments, "x"), RequiredInt(arguments, "y"),
            unchecked((uint)RequiredInt(arguments, "inputEvent")),
            unchecked((uint)RequiredInt(arguments, "buttons")),
            RequiredInt(arguments, "wheelDelta"));
        return runtime.DescribeSceneJson();
    }

    private static int RequiredInt(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) || !element.TryGetInt32(out var result))
            throw new ArgumentException($"Missing integer '{property}'");
        return result;
    }

    private static string DispatchViewportKey(EditorRuntime runtime, JsonElement request)
    {
        var arguments = request.GetProperty("params").GetProperty("arguments");
        var key = RequiredInt(arguments, "key");
        var pressed = arguments.GetProperty("pressed").GetBoolean();
        var focused = arguments.GetProperty("viewportFocused").GetBoolean();
        runtime.ViewportKey(unchecked((uint)key), pressed, focused);
        return runtime.DescribeSceneJson();
    }

    private static string DispatchViewportTick(EditorRuntime runtime, JsonElement request)
    {
        var arguments = request.GetProperty("params").GetProperty("arguments");
        if (!arguments.TryGetProperty("deltaSeconds", out var delta) || !delta.TryGetSingle(out var seconds))
            throw new ArgumentException("Missing number 'deltaSeconds'");
        runtime.Update(seconds);
        return runtime.DescribeSceneJson();
    }

    private static string RequiredString(JsonElement request, params string[] path)
    {
        var value = request;
        foreach (var segment in path)
        {
            if (!value.TryGetProperty(segment, out value))
                throw new ArgumentException($"Missing '{string.Join('.', path)}'");
        }
        return value.GetString() ?? throw new ArgumentException($"'{string.Join('.', path)}' must be a string");
    }

    private static JsonObject Tool(string name, string description, JsonObject? schema = null) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema ?? new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["additionalProperties"] = false,
        },
    };

    private static JsonObject Resource(string uri, string name, string description, string mimeType) => new()
    {
        ["uri"] = uri,
        ["name"] = name,
        ["description"] = description,
        ["mimeType"] = mimeType,
    };

    private static JsonObject Success(JsonElement id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = JsonNode.Parse(id.GetRawText()),
        ["result"] = result,
    };

    private static JsonObject Error(JsonElement id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = JsonNode.Parse(id.GetRawText()),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
