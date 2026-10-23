using System.Text.Json.Nodes;

namespace SharpEngine.Editor;

internal sealed record McpCapabilityDescriptor(
    string Id,
    int SchemaVersion,
    string Description,
    string Effect,
    JsonObject InputSchema,
    JsonObject OutputSchema,
    bool RequiresLiveUi = false,
    string? RevisionPrecondition = null)
{
    internal JsonObject ToTool(EditorRuntime runtime)
    {
        var availability = Availability(runtime);
        return new JsonObject
        {
            ["name"] = Id,
            ["description"] = Description,
            ["inputSchema"] = InputSchema.DeepClone(),
            ["outputSchema"] = OutputSchema.DeepClone(),
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = Effect == "read",
                ["destructiveHint"] = Effect == "external",
                ["idempotentHint"] = Effect == "read",
                ["openWorldHint"] = Effect == "external",
            },
            ["_meta"] = new JsonObject
            {
                ["sharpengine/capability"] = ToCapabilityNode(availability),
            },
        };
    }

    internal JsonObject ToCapabilityNode(EditorRuntime runtime) =>
        ToCapabilityNode(Availability(runtime));

    private JsonObject ToCapabilityNode((bool Available, JsonObject? Reason) availability) => new()
    {
        ["id"] = Id,
        ["schemaVersion"] = SchemaVersion,
        ["effect"] = Effect,
        ["requiresLiveUi"] = RequiresLiveUi,
        ["revisionPrecondition"] = RevisionPrecondition,
        ["available"] = availability.Available,
        ["unavailability"] = availability.Reason,
        ["inputSchema"] = InputSchema.DeepClone(),
        ["outputSchema"] = OutputSchema.DeepClone(),
    };

    internal (bool Available, JsonObject? Reason) Availability(EditorRuntime runtime)
    {
        if (Id.StartsWith("plugins.", StringComparison.Ordinal) && runtime.ProjectManifest is null)
        {
            return (false, McpCapabilityCatalog.Unavailable("PROJECT_UNAVAILABLE", "Open a project before managing Plugins"));
        }
        if (Id == "content.create" && (runtime.ProjectManifest is null || runtime.PlayState != EditorPlayState.Stopped))
        {
            return (false, McpCapabilityCatalog.Unavailable("CONTENT_CREATE_UNAVAILABLE", "Open a project and stop Play before creating content"));
        }
        if (Id is "source.create" or "source.modules" && runtime.ProjectManifest is null)
            return (false, McpCapabilityCatalog.Unavailable("PROJECT_UNAVAILABLE", "Open a project before creating C# source"));
        if (RequiresLiveUi && !runtime.HasLiveUi)
            return (false, McpCapabilityCatalog.Unavailable("EDITOR_LIVE_UI_UNAVAILABLE",
                "The selected Editor session has no live RmlUi host"));
        if (Id.StartsWith("scene.", StringComparison.Ordinal) && runtime.World is null)
            return (false, McpCapabilityCatalog.Unavailable("WORLD_UNAVAILABLE",
                "No Authoring World is loaded"));
        return (true, null);
    }
}

internal static class McpCapabilityCatalog
{
    internal static readonly IReadOnlyList<McpCapabilityDescriptor> All =
        new McpCapabilityDescriptor[]
        {
            Read("scene.describe", "Return the complete serialized scene, including hierarchy and resolved world matrices."),
            Read("source.modules", "List evaluated project Modules and root namespaces for C# source creation."),
            Read("source.status", "Read project build/reload state, availability and retry diagnostics."),
            Read("plugins.describe", "Read project Plugin analysis, pending confirmation plan, busy state and restart requirement."),
            new("plugins.request", 1,
                "Analyze or preview project Plugin dependency changes; apply requires the exact previewed planId. Writes project configuration immediately and requires a restart; does not edit the World or its Undo history.",
                "external", JsonNode.Parse("""
                    {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,
                     "required":["operation","expectedRevision"],"properties":{
                     "operation":{"enum":["analyze","plan-enable","plan-disable","apply","cancel"]},
                     "expectedRevision":{"type":"integer","minimum":0},"pluginId":{"type":"string"},"expectedPlan":{"type":"string"}}}
                    """)!.AsObject(), ObjectOutput(), RevisionPrecondition: "plugins.describe.revision; apply also requires expectedPlan"),
            new("source.create", 1, "Create one C# class or Runtime component source file and queue a project build. Writes to disk immediately; not part of scene Undo.", "external",
                new JsonObject { ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["type"] = "object",
                    ["properties"] = new JsonObject {
                        ["moduleProject"] = new JsonObject { ["type"] = "string" },
                        ["directory"] = new JsonObject { ["type"] = "string" },
                        ["name"] = new JsonObject { ["type"] = "string" },
                        ["template"] = new JsonObject { ["enum"] = new JsonArray("class", "component") } },
                    ["required"] = new JsonArray("moduleProject", "directory", "name", "template"), ["additionalProperties"] = false }, ObjectOutput()),
            Read("components.describe", "Discover component schemas, defaults, context, revisions, live component data and runtime navigation/animation observations."),
            new("components.edit", 1, "Edit components or create/remove entities through shared transactions.", "edit",
                new JsonObject { ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["type"] = "object",
                    ["properties"] = new JsonObject {
                        ["context"] = new JsonObject { ["enum"] = new JsonArray("authoring", "runtime") },
                        ["expectedRevision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
                        ["operation"] = new JsonObject { ["enum"] = new JsonArray("add", "patch", "reset", "remove", "enabled", "active", "reorder", "create-entity", "destroy-entity") },
                        ["entityId"] = new JsonObject { ["type"] = "string" }, ["componentId"] = new JsonObject { ["type"] = "string" },
                        ["typeId"] = new JsonObject { ["type"] = "string" }, ["state"] = new JsonObject { ["type"] = "object" },
                        ["value"] = new JsonObject { ["type"] = "boolean" }, ["index"] = new JsonObject { ["type"] = "integer" },
                        ["name"] = new JsonObject { ["type"] = "string" }, ["parentId"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                        ["transactionGroupId"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("context", "expectedRevision", "operation"), ["additionalProperties"] = false },
                ObjectOutput(), RevisionPrecondition: "context revision"),
            Read("assets.describe", "Return the engine and project asset registry with source and importer metadata."),
            Read("content.describe", "Return all four independent content managers, their queries, filters, selections, Tile/List modes, and active routing target (schema v2)."),
            new("content.create", 1, "Create a folder or a registered source asset inside project Content. Commits the source and asset manifest to disk; separate from World Undo and startup configuration.", "external",
                JsonNode.Parse("""
                    {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,
                     "required":["schemaVersion","type","folder","name","expectedRevision"],"properties":{
                     "schemaVersion":{"const":1},"type":{"type":"string","minLength":1},"folder":{"type":"string"},
                     "name":{"type":"string","minLength":1},"expectedRevision":{"type":"string","minLength":1}}}
                    """)!.AsObject(), ObjectOutput(), RevisionPrecondition: "content.describe.creation.revision"),
            Read("editor.describe", "Return the committed editor snapshot and links to capability and live UI resources."),
            Read("editor.mcp.describe", "Read the MCP configuration, active HTTP endpoint, revision and diagnostics."),
            new("editor.mcp.configure", 1,
                "Save the MCP port and immediately restart its HTTP endpoint. Disconnects HTTP clients; not part of scene Undo.",
                "external", McpConfigurationInput(), ObjectOutput(), RequiresLiveUi: true,
                RevisionPrecondition: "MCP configuration revision"),
            Read("input.describe", "Return loaded InputActions, mapping contexts, live control state, and recent routed events."),
            Read("render.describe", "Return transient render backend, GPU submission and CPU fallback counts, derived cache statistics, and average/P95 frame timings."),
            new("scene.reparent", 1,
                "Atomically reparent or reorder one World entity through the shared Authoring transaction layer.",
                "edit", ReparentInput(), EditOutput(),
                RevisionPrecondition: "documentRevision"),
            new("viewport.input", 1,
                "Send a viewport-specific semantic navigation event. This does not prove UI operability.",
                "runtime", ViewportInput(), ObjectOutput()),
            new("viewport.key", 1,
                "Send viewport-specific keyboard state for navigation. This does not prove UI focus or operability.",
                "runtime", ViewportKey(), ObjectOutput()),
            new("viewport.tick", 1,
                "Advance deterministic editor navigation by a bounded time step.",
                "runtime", ViewportTick(), ObjectOutput()),
            new("editor.action", 1,
                "Dispatch an opaque legacy editor action. It may write files, launch processes, or show native dialogs; prefer a typed semantic capability when available.",
                "external", EditorAction(), ObjectOutput()),
            new("ui.describe", 1,
                "Return the last coherently presented semantic RmlUi snapshot and pin its matching frame pixels.",
                "read", EmptyInput(), UiSnapshotOutput(), RequiresLiveUi: true),
            new("ui.pointer", 1,
                "Send host-local pointer input through the native RmlUi and Editor interaction path. The resolved control may perform external effects.",
                "external", UiPointerInput(), UiInputOutput(), RequiresLiveUi: true,
                RevisionPrecondition: "frameId"),
            new("ui.key", 1,
                "Send key or text input through the focused native RmlUi host. The focused control may perform external effects.",
                "external", UiKeyInput(), UiInputOutput(), RequiresLiveUi: true,
                RevisionPrecondition: "frameId"),
        };

    internal static McpCapabilityDescriptor Get(string id) =>
        All.FirstOrDefault(capability => string.Equals(capability.Id, id,
            StringComparison.Ordinal)) ??
        throw new KeyNotFoundException($"Unknown tool '{id}'");

    internal static JsonObject Snapshot(EditorRuntime runtime) => new()
    {
        ["schemaVersion"] = 1,
        ["session"] = runtime.McpSessionDescriptor is null ? null :
            System.Text.Json.JsonSerializer.SerializeToNode(
                runtime.McpSessionDescriptor,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web)),
        ["capabilities"] = new JsonArray(All
            .OrderBy(capability => capability.Id, StringComparer.Ordinal)
            .Select(capability => (JsonNode?)capability.ToCapabilityNode(runtime))
            .ToArray()),
    };

    internal static JsonObject Unavailable(string code, string message) => new()
    {
        ["code"] = code,
        ["severity"] = "error",
        ["message"] = message,
        ["retryable"] = true,
    };

    private static McpCapabilityDescriptor Read(string id, string description) =>
        new(id, 1, description, "read", EmptyInput(), ObjectOutput());

    private static JsonObject McpConfigurationInput()
    {
        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["port"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = 65535,
                },
                ["expectedRevision"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 0,
                },
            },
            ["required"] = new JsonArray("port", "expectedRevision"),
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject EmptyInput() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false,
    };

    private static JsonObject ObjectOutput() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
    };

    private static JsonObject EditOutput() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["required"] = new JsonArray("success", "code", "beforeRevision",
            "afterRevision", "isDirty"),
        ["properties"] = new JsonObject
        {
            ["success"] = new JsonObject { ["type"] = "boolean" },
            ["code"] = new JsonObject { ["type"] = "string" },
            ["message"] = new JsonObject { ["type"] = "string" },
            ["beforeRevision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["afterRevision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["isDirty"] = new JsonObject { ["type"] = "boolean" },
            ["transaction"] = new JsonObject
            { ["type"] = new JsonArray("object", "null") },
        },
    };

    private static JsonObject UiSnapshotOutput() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["required"] = new JsonArray("schemaVersion", "stateRevision",
            "layoutRevision", "layoutDirty", "documentRevision", "frameId",
            "hosts", "diagnostics"),
        ["properties"] = new JsonObject
        {
            ["schemaVersion"] = new JsonObject { ["const"] = 1 },
            ["stateRevision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["layoutRevision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["layoutDirty"] = new JsonObject { ["type"] = "boolean" },
            ["documentRevision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["frameId"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["hosts"] = new JsonObject { ["type"] = "array" },
            ["diagnostics"] = new JsonObject { ["type"] = "array" },
        },
    };

    private static JsonObject UiInputOutput() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["required"] = new JsonArray("accepted", "beforeStateRevision",
            "afterStateRevision", "frameId", "eventPath", "diagnostics"),
        ["properties"] = new JsonObject
        {
            ["accepted"] = new JsonObject { ["type"] = "boolean" },
            ["beforeStateRevision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["afterStateRevision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["frameId"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["targetElementId"] = new JsonObject
            { ["type"] = new JsonArray("string", "null") },
            ["eventPath"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" }
            },
            ["diagnostics"] = new JsonObject { ["type"] = "array" },
        },
    };

    private static JsonObject ReparentInput() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["sourceEntityId"] = new JsonObject { ["type"] = "string" },
            ["newParentEntityId"] = new JsonObject
            { ["type"] = new JsonArray("string", "null") },
            ["placement"] = new JsonObject
            { ["type"] = "string", ["enum"] = new JsonArray("root", "inside", "before", "after") },
            ["anchorEntityId"] = new JsonObject
            { ["type"] = new JsonArray("string", "null") },
            ["expectedRevision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["preserveWorldTransform"] = new JsonObject { ["type"] = "boolean", ["default"] = true },
        },
        ["required"] = new JsonArray("sourceEntityId", "placement", "expectedRevision"),
        ["additionalProperties"] = false,
    };

    private static JsonObject ViewportInput() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["x"] = new JsonObject { ["type"] = "integer" },
            ["y"] = new JsonObject { ["type"] = "integer" },
            ["inputEvent"] = new JsonObject
            { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 3 },
            ["buttons"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["wheelDelta"] = new JsonObject { ["type"] = "integer" },
        },
        ["required"] = new JsonArray("x", "y", "inputEvent", "buttons", "wheelDelta"),
        ["additionalProperties"] = false,
    };

    private static JsonObject ViewportKey() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["key"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["pressed"] = new JsonObject { ["type"] = "boolean" },
            ["viewportFocused"] = new JsonObject { ["type"] = "boolean" },
        },
        ["required"] = new JsonArray("key", "pressed", "viewportFocused"),
        ["additionalProperties"] = false,
    };

    private static JsonObject ViewportTick() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["deltaSeconds"] = new JsonObject
            { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 0.1 },
        },
        ["required"] = new JsonArray("deltaSeconds"),
        ["additionalProperties"] = false,
    };

    private static JsonObject EditorAction() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
        },
        ["required"] = new JsonArray("action"),
        ["additionalProperties"] = false,
    };

    private static JsonObject UiPointerInput() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["hostId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
            ["event"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("move", "down", "up", "cancel", "wheel")
            },
            ["x"] = new JsonObject { ["type"] = "integer" },
            ["y"] = new JsonObject { ["type"] = "integer" },
            ["button"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("none", "left", "right", "middle")
            },
            ["buttons"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("left", "right", "middle")
                },
                ["uniqueItems"] = true
            },
            ["modifiers"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("shift", "control", "alt", "meta")
                },
                ["uniqueItems"] = true
            },
            ["pointerId"] = new JsonObject { ["const"] = 0 },
            ["expectedFrameId"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            ["expectedElementId"] = new JsonObject
            { ["type"] = new JsonArray("string", "null") },
            ["wheelX"] = new JsonObject { ["type"] = "number", ["default"] = 0 },
            ["wheelY"] = new JsonObject { ["type"] = "number", ["default"] = 0 },
        },
        ["required"] = new JsonArray("hostId", "event", "x", "y", "button",
            "buttons", "modifiers", "pointerId", "expectedFrameId"),
        ["additionalProperties"] = false,
    };

    private static JsonObject UiKeyInput() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["hostId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
            ["event"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("down", "up", "text")
            },
            ["key"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
            ["text"] = new JsonObject { ["type"] = "string" },
            ["modifiers"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("shift", "control", "alt", "meta")
                },
                ["uniqueItems"] = true
            },
            ["expectedFrameId"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
        },
        ["required"] = new JsonArray("hostId", "event", "modifiers", "expectedFrameId"),
        ["additionalProperties"] = false,
    };
}
