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
            var response = ProcessLine(runtime, line);
            if (response is null) continue; // JSON-RPC notification.
            Console.WriteLine(response);
            Console.Out.Flush();
        }
        return 0;
    }

    internal static string? ProcessLine(EditorRuntime runtime, string line) =>
        Dispatch(runtime, line)?.ToJsonString(JsonOptions);

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
                "tools/list" => ToolsList(runtime),
                "resources/list" => ResourcesList(),
                "resources/templates/list" => ResourceTemplatesList(),
                "resources/read" => ReadResource(runtime, request),
                "tools/call" => CallTool(runtime, request),
                "notifications/initialized" => null,
                _ => throw new InvalidOperationException($"Unknown MCP method '{method}'"),
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

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject { ["listChanged"] = false },
            ["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false },
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "sharpengine",
            ["title"] = "SharpEngine native editor",
            ["version"] = "0.2.0",
        },
        ["instructions"] = "Read sharpengine://capabilities before mutation. Direct viewport semantic input is not evidence that a visible UI control is operable.",
    };

    private static JsonObject ToolsList(EditorRuntime runtime) => new()
    {
        ["tools"] = new JsonArray(McpCapabilityCatalog.All
            .OrderBy(capability => capability.Id, StringComparer.Ordinal)
            .Select(capability => (JsonNode?)capability.ToTool(runtime)).ToArray()),
    };

    private static JsonObject ResourcesList() => new()
    {
        ["resources"] = new JsonArray
        {
            Resource("sharpengine://scene", "Current scene", "Complete serialized scene", "application/json"),
            Resource("sharpengine://assets", "Current assets", "Engine and project asset registry", "application/json"),
            Resource("sharpengine://content", "Content manager", "Current content browser state and visible assets", "application/json"),
            Resource("sharpengine://editor", "Editor snapshot", "Combined scene and asset snapshot", "application/json"),
            Resource("sharpengine://input", "Input snapshot", "Unified input assets, contexts, controls, and routed events", "application/json"),
            Resource("sharpengine://capabilities", "Capability index", "Versioned schemas, effects, preconditions, and live availability", "application/json"),
            Resource("sharpengine://ui", "Presented editor UI", "Semantic RmlUi state for the last completed frame", "application/json"),
        },
    };

    private static JsonObject ResourceTemplatesList() => new()
    {
        ["resourceTemplates"] = new JsonArray
        {
            new JsonObject
            {
                ["uriTemplate"] = "sharpengine://ui/frames/{frameId}/{hostId}.png",
                ["name"] = "Presented UI frame",
                ["description"] = "Pinned PNG pixels for one host in a semantic UI snapshot",
                ["mimeType"] = "image/png",
            },
        },
    };

    private static JsonObject ReadResource(EditorRuntime runtime, JsonElement request)
    {
        var uri = RequiredString(request, "params", "uri");
        if (TryParseFrameUri(uri, out var frameId, out var hostId))
        {
            var png = runtime.ReadUiFramePng(frameId, hostId);
            if (png is null)
            {
                var diagnostic = new JsonObject
                {
                    ["code"] = "UI_FRAME_EXPIRED",
                    ["severity"] = "error",
                    ["objectId"] = hostId,
                    ["fieldPath"] = "frameId",
                    ["expected"] = "a pinned or current frame",
                    ["actual"] = frameId,
                    ["stateRevision"] = 0,
                    ["retryable"] = true,
                    ["message"] = "The requested UI frame is no longer retained",
                };
                return new JsonObject
                {
                    ["contents"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["uri"] = uri,
                            ["mimeType"] = "application/json",
                            ["text"] = diagnostic.ToJsonString(JsonOptions),
                        },
                    },
                };
            }
            return new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["uri"] = uri,
                        ["mimeType"] = "image/png",
                        ["blob"] = Convert.ToBase64String(png),
                    },
                },
            };
        }
        var text = uri switch
        {
            "sharpengine://scene" => runtime.DescribeSceneJson(),
            "sharpengine://assets" => runtime.DescribeAssetsJson(),
            "sharpengine://content" => runtime.DescribeContentJson(),
            "sharpengine://editor" => runtime.DescribeEditorJson(),
            "sharpengine://input" => runtime.DescribeInputJson(),
            "sharpengine://capabilities" => McpCapabilityCatalog.Snapshot(runtime).ToJsonString(JsonOptions),
            "sharpengine://ui" => runtime.DescribeUiJson(),
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

    private static ToolCall CreateSource(EditorRuntime runtime, JsonElement request)
    {
        var args = request.GetProperty("params").GetProperty("arguments");
        var result = runtime.CreateCSharpSource(new(args.GetProperty("moduleProject").GetString()!,
            args.GetProperty("directory").GetString()!, args.GetProperty("name").GetString()!, args.GetProperty("template").GetString()!));
        return new(JsonSerializer.Serialize(result, JsonOptions), !result.Success);
    }

    private static ToolCall ComponentEdit(EditorRuntime runtime, JsonElement request)
    {
        var result = runtime.EditComponent(JsonNode.Parse(request.GetProperty("params").GetProperty("arguments").GetRawText())!.AsObject());
        return new(result.ToJsonString(), result["success"]?.GetValue<bool>() != true);
    }

    private static JsonObject CallTool(EditorRuntime runtime, JsonElement request)
    {
        var name = RequiredString(request, "params", "name");
        var descriptor = McpCapabilityCatalog.Get(name);
        var availability = descriptor.Availability(runtime);
        if (!availability.Available)
        {
            var unavailable = new JsonObject
            {
                ["success"] = false,
                ["diagnostics"] = new JsonArray(availability.Reason?.DeepClone()),
            };
            return ToolResult(unavailable, true);
        }
        var call = name switch
        {
            "source.modules" => new ToolCall(runtime.DescribeSourceModulesJson(), false),
            "source.status" => new ToolCall(runtime.DescribeProjectCodeJson(), false),
            "source.create" => CreateSource(runtime, request),
            "scene.describe" => new ToolCall(runtime.DescribeSceneJson(), false),
            "components.describe" => new ToolCall(runtime.DescribeComponentsJson(), false),
            "components.edit" => ComponentEdit(runtime, request),
            "assets.describe" => new ToolCall(runtime.DescribeAssetsJson(), false),
            "content.describe" => new ToolCall(runtime.DescribeContentJson(), false),
            "editor.describe" => new ToolCall(runtime.DescribeEditorJson(), false),
            "input.describe" => new ToolCall(runtime.DescribeInputJson(), false),
            "scene.reparent" => DispatchReparent(runtime, request),
            "viewport.input" => new ToolCall(DispatchViewportInput(runtime, request), false),
            "viewport.key" => new ToolCall(DispatchViewportKey(runtime, request), false),
            "viewport.tick" => new ToolCall(DispatchViewportTick(runtime, request), false),
            "editor.action" => new ToolCall(DispatchAction(runtime, request), false),
            "ui.describe" => new ToolCall(runtime.DescribeUiJson(), false),
            "ui.pointer" => DispatchUiPointer(runtime, request),
            "ui.key" => DispatchUiKey(runtime, request),
            _ => throw new KeyNotFoundException($"Unknown tool '{name}'"),
        };
        var structured = JsonNode.Parse(call.Text) ?? new JsonObject();
        return ToolResult(structured, call.IsError);
    }

    private static ToolCall DispatchReparent(EditorRuntime runtime, JsonElement request)
    {
        var arguments = request.GetProperty("params").GetProperty("arguments");
        var placementText = RequiredString(arguments, "placement");
        var placement = placementText switch
        {
            "root" => WorldHierarchyPlacement.Root,
            "inside" => WorldHierarchyPlacement.Inside,
            "before" => WorldHierarchyPlacement.Before,
            "after" => WorldHierarchyPlacement.After,
            _ => throw new ArgumentException($"Unknown hierarchy placement '{placementText}'"),
        };
        if (!arguments.TryGetProperty("expectedRevision", out var revisionElement) ||
            !revisionElement.TryGetUInt64(out var revision))
            throw new ArgumentException("Missing unsigned integer 'expectedRevision'");
        var preserve = !arguments.TryGetProperty("preserveWorldTransform", out var preserveElement) ||
            preserveElement.GetBoolean();
        var result = runtime.ReparentEntity(new WorldReparentRequest(
            RequiredString(arguments, "sourceEntityId"),
            OptionalString(arguments, "newParentEntityId"), placement,
            OptionalString(arguments, "anchorEntityId"), revision, preserve));
        return new(JsonSerializer.Serialize(result, JsonOptions), !result.Success);
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

    private static ToolCall DispatchUiPointer(EditorRuntime runtime,
                                               JsonElement request)
    {
        var arguments = request.GetProperty("params").GetProperty("arguments");
        var inputEvent = RequiredString(arguments, "event") switch
        {
            "move" => 0u,
            "down" => 1u,
            "up" => 2u,
            "cancel" => 3u,
            "wheel" => 4u,
            var value => throw new ArgumentException($"Unknown pointer event '{value}'"),
        };
        var button = RequiredString(arguments, "button") switch
        {
            "none" => 0u,
            "left" => 1u,
            "right" => 2u,
            "middle" => 3u,
            var value => throw new ArgumentException($"Unknown pointer button '{value}'"),
        };
        var buttons = Flags(arguments, "buttons", new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["left"] = 0x0001,
            ["right"] = 0x0002,
            ["middle"] = 0x0010,
        });
        var modifiers = Flags(arguments, "modifiers", new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["control"] = 1,
            ["shift"] = 2,
            ["alt"] = 4,
            ["meta"] = 8,
        });
        var result = runtime.SendUiPointer(RequiredString(arguments, "hostId"),
            inputEvent, RequiredInt(arguments, "x"), RequiredInt(arguments, "y"),
            button, buttons, modifiers, RequiredUInt(arguments, "pointerId"),
            RequiredULong(arguments, "expectedFrameId"),
            OptionalFloat(arguments, "wheelX"), OptionalFloat(arguments, "wheelY"),
            OptionalString(arguments, "expectedElementId"));
        return new(result, !Accepted(result));
    }

    private static ToolCall DispatchUiKey(EditorRuntime runtime,
                                           JsonElement request)
    {
        var arguments = request.GetProperty("params").GetProperty("arguments");
        var inputEvent = RequiredString(arguments, "event") switch
        {
            "down" => 0u,
            "up" => 1u,
            "text" => 2u,
            var value => throw new ArgumentException($"Unknown key event '{value}'"),
        };
        var modifiers = Flags(arguments, "modifiers", new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["control"] = 1,
            ["shift"] = 2,
            ["alt"] = 4,
            ["meta"] = 8,
        });
        var key = arguments.TryGetProperty("key", out var keyElement) &&
            keyElement.TryGetUInt32(out var parsedKey) ? parsedKey : 0;
        var text = OptionalString(arguments, "text") ?? string.Empty;
        if (inputEvent != 2 && key == 0)
            throw new ArgumentException("Key down/up requires a non-zero 'key'");
        if (inputEvent == 2 && text.Length == 0)
            throw new ArgumentException("Text input requires non-empty 'text'");
        var result = runtime.SendUiKey(RequiredString(arguments, "hostId"),
            inputEvent, key, modifiers, text,
            RequiredULong(arguments, "expectedFrameId"));
        return new(result, !Accepted(result));
    }

    private static bool Accepted(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("accepted", out var accepted) &&
            accepted.ValueKind == JsonValueKind.True;
    }

    private static uint Flags(JsonElement arguments, string property,
                              IReadOnlyDictionary<string, uint> values)
    {
        if (!arguments.TryGetProperty(property, out var array) ||
            array.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"Missing array '{property}'");
        uint result = 0;
        foreach (var item in array.EnumerateArray())
        {
            var name = item.GetString() ??
                throw new ArgumentException($"'{property}' entries must be strings");
            if (!values.TryGetValue(name, out var flag))
                throw new ArgumentException($"Unknown {property} value '{name}'");
            result |= flag;
        }
        return result;
    }

    private static uint RequiredUInt(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) ||
            !element.TryGetUInt32(out var result))
            throw new ArgumentException($"Missing unsigned integer '{property}'");
        return result;
    }

    private static ulong RequiredULong(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) ||
            !element.TryGetUInt64(out var result))
            throw new ArgumentException($"Missing unsigned integer '{property}'");
        return result;
    }

    private static float OptionalFloat(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element)) return 0;
        if (!element.TryGetSingle(out var result) || !float.IsFinite(result))
            throw new ArgumentException($"'{property}' must be finite");
        return result;
    }

    private static bool TryParseFrameUri(string uri, out ulong frameId,
                                         out string hostId)
    {
        frameId = 0;
        hostId = string.Empty;
        const string prefix = "sharpengine://ui/frames/";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var path = uri[prefix.Length..];
        var separator = path.IndexOf('/');
        if (separator <= 0 || !ulong.TryParse(path[..separator], out frameId))
            throw new ArgumentException("UI frame URI contains an invalid frameId");
        var file = path[(separator + 1)..];
        if (!file.EndsWith(".png", StringComparison.Ordinal) || file.Length == 4)
            throw new ArgumentException("UI frame URI must end with a hostId and .png");
        hostId = Uri.UnescapeDataString(file[..^4]);
        return true;
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

    private static string? OptionalString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) ||
            element.ValueKind == JsonValueKind.Null) return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() :
            throw new ArgumentException($"'{property}' must be a string or null");
    }

    private readonly record struct ToolCall(string Text, bool IsError);

    private static JsonObject ToolResult(JsonNode structured, bool isError) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = structured.ToJsonString(JsonOptions) },
        },
        ["structuredContent"] = structured.DeepClone(),
        ["isError"] = isError,
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
