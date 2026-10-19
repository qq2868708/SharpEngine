using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SharpEngine.Assets;

namespace SharpEngine.Editor.Layout;

public static class EditorPaneIds
{
    public const string Viewport = "viewport";
    public const string UiEditor = "ui-editor";
    public const string Hierarchy = "hierarchy";
    public const string Content = "content";
    public const string Content2 = "content-2";
    public const string Content3 = "content-3";
    public const string Content4 = "content-4";
    public const string Inspector = "inspector";
    public const string Console = "console";
    public const string Logs = "logs";

    public static IReadOnlyList<string> BuiltIn { get; } =
        [Viewport, UiEditor, Hierarchy, Content, Content2, Content3, Content4,
            Inspector, Console, Logs];

    public static IReadOnlyList<string> ContentManagers { get; } =
        [Content, Content2, Content3, Content4];

    public static bool IsBuiltIn(string paneId) =>
        BuiltIn.Contains(paneId, StringComparer.Ordinal);

    public static bool IsContentManager(string paneId) =>
        ContentManagers.Contains(paneId, StringComparer.Ordinal);

    public static int ContentManagerNumber(string paneId)
    {
        for (var index = 0; index < ContentManagers.Count; index++)
            if (string.Equals(ContentManagers[index], paneId, StringComparison.Ordinal))
                return index + 1;
        return 0;
    }
}

public enum DockNodeKind { Split, Tabs }
public enum DockSplitOrientation { Horizontal, Vertical }
public enum DockEdge { Left, Right, Top, Bottom }

public sealed class EditorLayoutDocument
{
    public const int CurrentSchemaVersion = 3;

    [JsonRequired, JsonPropertyOrder(0)] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonRequired, JsonPropertyOrder(1)] public DockHostState MainHost { get; set; } = new();
    [JsonRequired, JsonPropertyOrder(2)] public List<string> HiddenPaneIds { get; set; } = [];
    [JsonRequired, JsonPropertyOrder(3)] public string ActiveContentManagerId { get; set; } =
        EditorPaneIds.Content;
    [JsonRequired, JsonPropertyOrder(4)] public List<ContentManagerLayoutState> ContentManagers
        { get; set; } = CreateDefaultContentManagers();

    public static List<ContentManagerLayoutState> CreateDefaultContentManagers() =>
        EditorPaneIds.ContentManagers.Select(paneId => new ContentManagerLayoutState
        {
            PaneId = paneId,
        }).ToList();
}

public sealed class ContentManagerLayoutState
{
    [JsonRequired, JsonPropertyOrder(0)] public string PaneId { get; set; } = string.Empty;
    [JsonRequired, JsonPropertyOrder(1)] public string Search { get; set; } = string.Empty;
    [JsonRequired, JsonPropertyOrder(2)] public ContentSourceFilter Source { get; set; } =
        ContentSourceFilter.Project;
    [JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }
    [JsonRequired, JsonPropertyOrder(4)] public string CurrentFolder { get; set; } = string.Empty;
    [JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedId { get; set; }
    [JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedPath { get; set; }
    [JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AssetSource? SelectedSource { get; set; }
    [JsonRequired, JsonPropertyOrder(8)] public ContentViewMode ViewMode { get; set; } =
        ContentViewMode.Tile;
}

public sealed class DockHostState
{
    [JsonRequired, JsonPropertyOrder(0)] public string HostId { get; set; } = "main";
    [JsonRequired, JsonPropertyOrder(1)] public DockNodeState Root { get; set; } = new();
}

public sealed class DockNodeState
{
    [JsonRequired, JsonPropertyOrder(0)] public string NodeId { get; set; } = string.Empty;
    [JsonRequired, JsonPropertyOrder(1)] public DockNodeKind Kind { get; set; }
    [JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DockSplitOrientation? Orientation { get; set; }
    [JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Ratio { get; set; }
    [JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DockNodeState? First { get; set; }
    [JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DockNodeState? Second { get; set; }
    [JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DockTabGroupState? TabGroup { get; set; }
}

public sealed class DockTabGroupState
{
    [JsonRequired, JsonPropertyOrder(0)] public string GroupId { get; set; } = string.Empty;
    [JsonRequired, JsonPropertyOrder(1)] public string ActivePaneId { get; set; } = string.Empty;
    [JsonRequired, JsonPropertyOrder(2)] public List<DockPaneState> Panes { get; set; } = [];
}

public sealed class DockPaneState
{
    [JsonRequired, JsonPropertyOrder(0)] public string PaneId { get; set; } = string.Empty;
}

public readonly record struct DockSize(float Width, float Height);
public readonly record struct DockRect(float X, float Y, float Width, float Height);

public sealed record FloatingDockHostState(string HostId, DockRect Bounds,
    DockNodeState Root);

public sealed record EditorLayoutDiagnostic(string Code, string Path, string Message);

public sealed class EditorLayoutException : Exception
{
    public EditorLayoutException(string message, IReadOnlyList<EditorLayoutDiagnostic> diagnostics,
        Exception? innerException = null) : base(message, innerException) =>
        Diagnostics = diagnostics;

    public IReadOnlyList<EditorLayoutDiagnostic> Diagnostics { get; }
}

public static class EditorLayoutJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static EditorLayoutDocument Parse(string json, string source = "<memory>")
    {
        try
        {
            using (var syntax = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            }))
            {
                var duplicate = FindDuplicateProperty(syntax.RootElement, "$", out var duplicatePath);
                if (duplicate is not null)
                    throw new EditorLayoutException($"Editor layout '{source}' is invalid.",
                        [new("layout.json.duplicate-property", duplicatePath,
                            $"Property '{duplicate}' occurs more than once.")]);
            }
            var root = JsonNode.Parse(json)?.AsObject() ??
                throw new JsonException("The root layout document is null.");
            Migrate(root);
            var document = root.Deserialize<EditorLayoutDocument>(Options) ??
                throw new JsonException("The root layout document is null.");
            var diagnostics = EditorLayoutValidator.Validate(document);
            if (diagnostics.Count != 0)
                throw new EditorLayoutException($"Editor layout '{source}' is invalid.", diagnostics);
            return document;
        }
        catch (EditorLayoutException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new EditorLayoutException($"Editor layout '{source}' could not be parsed.",
                [new("layout.json.invalid", source, exception.Message)], exception);
        }
    }

    public static string Serialize(EditorLayoutDocument document)
    {
        var canonical = Clone(document);
        canonical.HiddenPaneIds.Sort(StringComparer.Ordinal);
        var diagnostics = EditorLayoutValidator.Validate(canonical);
        if (diagnostics.Count != 0)
            throw new EditorLayoutException("Editor layout cannot be serialized.", diagnostics);
        return JsonSerializer.Serialize(canonical, Options) + "\n";
    }

    public static EditorLayoutDocument Clone(EditorLayoutDocument document) =>
        JsonSerializer.Deserialize<EditorLayoutDocument>(
            JsonSerializer.Serialize(document, Options), Options) ??
        throw new InvalidOperationException("Could not clone the editor layout.");

    public static DockNodeState Clone(DockNodeState node) =>
        JsonSerializer.Deserialize<DockNodeState>(JsonSerializer.Serialize(node, Options), Options) ??
        throw new InvalidOperationException("Could not clone the dock node.");

    public static JsonNode ToJsonNode(DockHostState host) =>
        JsonSerializer.SerializeToNode(host, Options) ??
        throw new InvalidOperationException("Could not describe the dock host.");

    public static JsonNode ToJsonNode(DockNodeState node) =>
        JsonSerializer.SerializeToNode(node, Options) ??
        throw new InvalidOperationException("Could not describe the dock node.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter<DockNodeKind>(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<DockSplitOrientation>(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ContentSourceFilter>(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<AssetSource>(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ContentViewMode>(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static void Migrate(JsonObject root)
    {
        var version = root["schemaVersion"]?.GetValue<int>();
        if (version == 1)
        {
            if (root.ContainsKey("activeContentManagerId") || root.ContainsKey("contentManagers"))
                throw new JsonException("Schema v1 cannot contain schema v2 content manager fields.");
            var hidden = root["hiddenPaneIds"]?.AsArray() ??
                throw new JsonException("The hiddenPaneIds collection is required.");
            if (hidden.Any(value => EditorPaneIds.ContentManagers.Skip(1).Contains(
                    value?.GetValue<string>(), StringComparer.Ordinal)))
                throw new JsonException("Schema v1 cannot contain additional content manager panes.");
            foreach (var paneId in EditorPaneIds.ContentManagers.Skip(1))
                if (!hidden.Any(value => string.Equals(value?.GetValue<string>(), paneId,
                        StringComparison.Ordinal)))
                    hidden.Add(paneId);
            root["activeContentManagerId"] = EditorPaneIds.Content;
            root["contentManagers"] = JsonSerializer.SerializeToNode(
                EditorLayoutDocument.CreateDefaultContentManagers(), Options);
            version = 2;
        }

        if (version == 2)
        {
            if (!AddLogsToConsoleGroup(root))
                throw new JsonException("Schema v2 layout does not contain a Console tab group.");
            root["schemaVersion"] = EditorLayoutDocument.CurrentSchemaVersion;
        }
    }

    private static bool AddLogsToConsoleGroup(JsonObject root)
    {
        if (root["mainHost"] is not JsonObject mainHost ||
            mainHost["root"] is not JsonObject node)
            return false;
        return AddLogsToConsoleGroupNode(node);
    }

    private static bool AddLogsToConsoleGroupNode(JsonObject node)
    {
        if (string.Equals(node["kind"]?.GetValue<string>(), "tabs", StringComparison.Ordinal))
        {
            if (node["tabGroup"] is not JsonObject tabGroup ||
                tabGroup["panes"] is not JsonArray panes)
                return false;
            var hasConsole = panes.Any(pane => string.Equals(
                (pane as JsonObject)?["paneId"]?.GetValue<string>(),
                EditorPaneIds.Console, StringComparison.Ordinal));
            if (!hasConsole) return false;
            if (!panes.Any(pane => string.Equals(
                    (pane as JsonObject)?["paneId"]?.GetValue<string>(),
                    EditorPaneIds.Logs, StringComparison.Ordinal)))
                panes.Add(new JsonObject { ["paneId"] = EditorPaneIds.Logs });
            return true;
        }

        return (node["first"] is JsonObject first && AddLogsToConsoleGroupNode(first)) ||
            (node["second"] is JsonObject second && AddLogsToConsoleGroupNode(second));
    }

    private static string? FindDuplicateProperty(JsonElement element, string path,
        out string duplicatePath)
    {
        duplicatePath = path;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = $"{path}.{property.Name}";
                if (!names.Add(property.Name))
                {
                    duplicatePath = propertyPath;
                    return property.Name;
                }
                var nested = FindDuplicateProperty(property.Value, propertyPath, out duplicatePath);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindDuplicateProperty(item, $"{path}[{index++}]", out duplicatePath);
                if (nested is not null) return nested;
            }
        }
        return null;
    }
}

public static class EditorLayoutValidator
{
    public const float MinimumStoredRatio = 0.05f;
    public const float MaximumStoredRatio = 0.95f;

    public static IReadOnlyList<EditorLayoutDiagnostic> Validate(EditorLayoutDocument? document,
        bool requireAllPanesInMain = false)
    {
        var result = new List<EditorLayoutDiagnostic>();
        if (document is null)
        {
            result.Add(new("layout.root.required", "$", "The layout document is required."));
            return result;
        }
        if (document.SchemaVersion != EditorLayoutDocument.CurrentSchemaVersion)
            result.Add(new("layout.schema.unsupported", "$.schemaVersion",
                $"Expected schema version {EditorLayoutDocument.CurrentSchemaVersion}, got {document.SchemaVersion}."));
        if (document.MainHost is null)
        {
            result.Add(new("layout.main.required", "$.mainHost", "The main host is required."));
            return result;
        }
        if (!string.Equals(document.MainHost.HostId, "main", StringComparison.Ordinal))
            result.Add(new("layout.main.id", "$.mainHost.hostId", "The persisted host ID must be 'main'."));
        if (document.MainHost.Root is null)
        {
            result.Add(new("layout.root.required", "$.mainHost.root", "The main dock root is required."));
            return result;
        }

        var paneCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        ValidateNode(document.MainHost.Root, "$.mainHost.root", result, paneCounts, nodeIds, groupIds);
        var hidden = document.HiddenPaneIds ?? [];
        if (document.HiddenPaneIds is null)
            result.Add(new("layout.hidden.required", "$.hiddenPaneIds",
                "The hidden pane ID collection is required."));
        for (var index = 0; index < hidden.Count; index++)
        {
            var paneId = hidden[index];
            var path = $"$.hiddenPaneIds[{index}]";
            if (!EditorPaneIds.IsBuiltIn(paneId))
                result.Add(new("layout.pane.unknown", path, $"Unknown pane ID '{paneId}'."));
            else Increment(paneCounts, paneId);
            if (requireAllPanesInMain && !EditorPaneIds.IsContentManager(paneId))
                result.Add(new("layout.default.hidden", path,
                    "The engine default must dock every built-in pane in the main host."));
        }

        foreach (var paneId in EditorPaneIds.BuiltIn)
        {
            paneCounts.TryGetValue(paneId, out var count);
            if (count != 1)
                result.Add(new(count == 0 ? "layout.pane.missing" : "layout.pane.duplicate",
                    "$.mainHost", $"Pane '{paneId}' must occur exactly once; found {count}."));
        }
        if (hidden.Contains(EditorPaneIds.Viewport, StringComparer.Ordinal))
            result.Add(new("layout.viewport.main-only", "$.hiddenPaneIds",
                "The viewport cannot be hidden and must remain in the main host."));
        if (requireAllPanesInMain)
        {
            if (hidden.Contains(EditorPaneIds.Content, StringComparer.Ordinal))
                result.Add(new("layout.default.content-primary", "$.hiddenPaneIds",
                    "Content Manager 1 must be visible in the engine default."));
            foreach (var paneId in EditorPaneIds.ContentManagers.Skip(1))
                if (!hidden.Contains(paneId, StringComparer.Ordinal))
                    result.Add(new("layout.default.content-optional", "$.hiddenPaneIds",
                        $"'{paneId}' must start hidden in the engine default."));
        }
        ValidateContentManagers(document, result);
        return result;
    }

    private static void ValidateContentManagers(EditorLayoutDocument document,
        List<EditorLayoutDiagnostic> result)
    {
        var states = document.ContentManagers ?? [];
        if (document.ContentManagers is null)
            result.Add(new("layout.content.required", "$.contentManagers",
                "The content manager state collection is required."));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < states.Count; index++)
        {
            var state = states[index];
            var path = $"$.contentManagers[{index}]";
            if (state is null)
            {
                result.Add(new("layout.content.state", path,
                    "A content manager state is required."));
                continue;
            }
            if (!EditorPaneIds.IsContentManager(state.PaneId))
                result.Add(new("layout.content.pane", $"{path}.paneId",
                    $"Unknown content manager pane ID '{state.PaneId}'."));
            else
                Increment(counts, state.PaneId);
            if (state.Search is null)
                result.Add(new("layout.content.search", $"{path}.search",
                    "The search value is required."));
            if (!Enum.IsDefined(state.Source) || !Enum.IsDefined(state.ViewMode) ||
                (state.SelectedSource is { } selectedSource && !Enum.IsDefined(selectedSource)))
                result.Add(new("layout.content.enum", path,
                    "Content source, selected source, and view mode must be known values."));
            if (state.CurrentFolder is null || !IsSafeContentPath(state.CurrentFolder))
                result.Add(new("layout.content.folder", $"{path}.currentFolder",
                    "The current folder must be relative to Content."));
            if (state.SelectedPath is not null && !IsSafeContentPath(state.SelectedPath))
                result.Add(new("layout.content.selection", $"{path}.selectedPath",
                    "The selected path must be relative to Content."));
            if ((state.SelectedPath is null) != (state.SelectedSource is null) &&
                state.SelectedId is null)
                result.Add(new("layout.content.selection", path,
                    "A path-only selection requires both selectedPath and selectedSource."));
        }
        foreach (var paneId in EditorPaneIds.ContentManagers)
        {
            counts.TryGetValue(paneId, out var count);
            if (count != 1)
                result.Add(new(count == 0 ? "layout.content.missing" : "layout.content.duplicate",
                    "$.contentManagers",
                    $"Content manager state '{paneId}' must occur exactly once; found {count}."));
        }
        if (!EditorPaneIds.IsContentManager(document.ActiveContentManagerId))
            result.Add(new("layout.content.active", "$.activeContentManagerId",
                $"Unknown active content manager '{document.ActiveContentManagerId}'."));
    }

    private static bool IsSafeContentPath(string path)
    {
        if (path.Length == 0) return true;
        var normalized = path.Replace('\\', '/');
        return !Path.IsPathRooted(normalized) &&
            normalized.Split('/').All(part => part is not ("" or "." or "..") &&
                part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    public static IReadOnlyList<EditorLayoutDiagnostic> ValidateSession(
        EditorLayoutDocument document, IReadOnlyList<FloatingDockHostState> floatingHosts)
    {
        var projection = EditorLayoutJson.Clone(document);
        var floatingPaneIds = new List<string>();
        var hostIds = new HashSet<string>(StringComparer.Ordinal) { "main" };
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        CollectIdentities(document.MainHost.Root, nodeIds, groupIds);
        var sessionDiagnostics = new List<EditorLayoutDiagnostic>();
        for (var hostIndex = 0; hostIndex < floatingHosts.Count; hostIndex++)
        {
            var host = floatingHosts[hostIndex];
            var path = $"$.floatingHosts[{hostIndex}]";
            if (string.IsNullOrWhiteSpace(host.HostId) || !hostIds.Add(host.HostId))
                sessionDiagnostics.Add(new("layout.float.id", path,
                    "Floating host IDs must be non-empty and unique."));
            if (!float.IsFinite(host.Bounds.X) || !float.IsFinite(host.Bounds.Y) ||
                !float.IsFinite(host.Bounds.Width) || !float.IsFinite(host.Bounds.Height) ||
                host.Bounds.Width <= 0 || host.Bounds.Height <= 0)
                sessionDiagnostics.Add(new("layout.float.bounds", $"{path}.bounds",
                    "Floating host bounds must be finite and have positive size."));
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            ValidateNode(host.Root, $"{path}.root", sessionDiagnostics, counts, nodeIds, groupIds);
            if (EnumeratePanes(host.Root).Contains(EditorPaneIds.Viewport, StringComparer.Ordinal))
                sessionDiagnostics.Add(new("layout.viewport.main-only", path,
                    "The viewport cannot be placed in a floating host."));
            floatingPaneIds.AddRange(EnumeratePanes(host.Root));
        }
        projection.HiddenPaneIds.AddRange(floatingPaneIds);
        sessionDiagnostics.AddRange(Validate(projection));
        return sessionDiagnostics;
    }

    public static IEnumerable<string> EnumeratePanes(DockNodeState node)
    {
        if (node.Kind == DockNodeKind.Tabs)
        {
            foreach (var pane in node.TabGroup?.Panes ?? []) yield return pane.PaneId;
            yield break;
        }
        if (node.First is not null)
            foreach (var paneId in EnumeratePanes(node.First)) yield return paneId;
        if (node.Second is not null)
            foreach (var paneId in EnumeratePanes(node.Second)) yield return paneId;
    }

    private static void ValidateNode(DockNodeState node, string path,
        List<EditorLayoutDiagnostic> result, Dictionary<string, int> paneCounts,
        HashSet<string> nodeIds, HashSet<string> groupIds)
    {
        if (string.IsNullOrWhiteSpace(node.NodeId) || !nodeIds.Add(node.NodeId))
            result.Add(new("layout.node.id", $"{path}.nodeId",
                "Dock node IDs must be non-empty and unique."));
        if (node.Kind == DockNodeKind.Split)
        {
            if (node.Orientation is null || node.Ratio is null || node.First is null || node.Second is null)
                result.Add(new("layout.split.incomplete", path,
                    "A split requires orientation, ratio, first, and second nodes."));
            if (node.TabGroup is not null)
                result.Add(new("layout.split.tabs", $"{path}.tabGroup",
                    "A split node cannot contain a tab group."));
            if (node.Ratio is not null && (!float.IsFinite(node.Ratio.Value) ||
                node.Ratio < MinimumStoredRatio || node.Ratio > MaximumStoredRatio))
                result.Add(new("layout.split.ratio", $"{path}.ratio",
                    $"Split ratio must be between {MinimumStoredRatio} and {MaximumStoredRatio}."));
            if (node.First is not null)
                ValidateNode(node.First, $"{path}.first", result, paneCounts, nodeIds, groupIds);
            if (node.Second is not null)
                ValidateNode(node.Second, $"{path}.second", result, paneCounts, nodeIds, groupIds);
            return;
        }

        if (node.Orientation is not null || node.Ratio is not null || node.First is not null ||
            node.Second is not null)
            result.Add(new("layout.tabs.split-fields", path,
                "A tab node cannot contain split fields."));
        var group = node.TabGroup;
        if (group is null)
        {
            result.Add(new("layout.tabs.required", $"{path}.tabGroup", "A tab group is required."));
            return;
        }
        if (string.IsNullOrWhiteSpace(group.GroupId) || !groupIds.Add(group.GroupId))
            result.Add(new("layout.group.id", $"{path}.tabGroup.groupId",
                "Tab group IDs must be non-empty and unique."));
        if (group.Panes is null || group.Panes.Count == 0)
        {
            result.Add(new("layout.group.empty", $"{path}.tabGroup.panes",
                "A persisted tab group cannot be empty."));
            return;
        }
        for (var index = 0; index < group.Panes.Count; index++)
        {
            var paneId = group.Panes[index]?.PaneId ?? string.Empty;
            var panePath = $"{path}.tabGroup.panes[{index}].paneId";
            if (!EditorPaneIds.IsBuiltIn(paneId))
                result.Add(new("layout.pane.unknown", panePath, $"Unknown pane ID '{paneId}'."));
            else Increment(paneCounts, paneId);
        }
        if (!group.Panes.Any(pane => string.Equals(pane.PaneId, group.ActivePaneId,
                StringComparison.Ordinal)))
            result.Add(new("layout.group.active", $"{path}.tabGroup.activePaneId",
                "The active pane must belong to its tab group."));
    }

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;

    private static void CollectIdentities(DockNodeState node, HashSet<string> nodeIds,
        HashSet<string> groupIds)
    {
        nodeIds.Add(node.NodeId);
        if (node.TabGroup is not null) groupIds.Add(node.TabGroup.GroupId);
        if (node.First is not null) CollectIdentities(node.First, nodeIds, groupIds);
        if (node.Second is not null) CollectIdentities(node.Second, nodeIds, groupIds);
    }
}

public sealed class EditorLayoutStore
{
    public const string DefaultRelativePath = "Config/DefaultEditorLayout.json";
    public const string UserRelativePath = "Saved/Config/EditorLayout.json";

    public EditorLayoutDocument LoadDefault(string engineRoot)
    {
        var path = Path.Combine(engineRoot, DefaultRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var document = Read(path);
        var diagnostics = EditorLayoutValidator.Validate(document, requireAllPanesInMain: true);
        if (diagnostics.Count != 0)
            throw new EditorLayoutException($"Engine default layout '{path}' is invalid.", diagnostics);
        return document;
    }

    public EditorLayoutDocument LoadSavedOrDefault(string engineRoot, string? projectRoot)
    {
        var path = GetUserPath(engineRoot, projectRoot);
        return File.Exists(path) ? Read(path) : LoadDefault(engineRoot);
    }

    public void Save(string engineRoot, string? projectRoot, EditorLayoutDocument document)
    {
        var serialized = EditorLayoutJson.Serialize(document);
        _ = EditorLayoutJson.Parse(serialized, "serialized editor layout");
        var path = GetUserPath(engineRoot, projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(serialized);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static string GetUserPath(string engineRoot, string? projectRoot) =>
        Path.Combine(projectRoot ?? engineRoot,
            UserRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static EditorLayoutDocument Read(string path)
    {
        if (!File.Exists(path))
            throw new EditorLayoutException($"Editor layout '{path}' does not exist.",
                [new("layout.file.missing", path, "The editor layout file does not exist.")]);
        return EditorLayoutJson.Parse(File.ReadAllText(path), path);
    }
}

public sealed record DockArrangementNode(string NodeId, DockRect Bounds,
    float? EffectiveRatio, bool RatioWasClamped);

public sealed record DockArrangement(DockSize MinimumSize,
    IReadOnlyList<DockArrangementNode> Nodes);

public static class DockLayoutCalculator
{
    public const float SplitterSize = 4f;

    public static DockSize MeasureMinimum(DockNodeState node)
    {
        if (node.Kind == DockNodeKind.Tabs)
        {
            var minimum = new DockSize(0, 0);
            foreach (var pane in node.TabGroup?.Panes ?? [])
            {
                var size = PaneMinimum(pane.PaneId);
                minimum = new DockSize(Math.Max(minimum.Width, size.Width),
                    Math.Max(minimum.Height, size.Height));
            }
            return minimum;
        }
        if (node.First is null || node.Second is null || node.Orientation is null)
            return default;
        var first = MeasureMinimum(node.First);
        var second = MeasureMinimum(node.Second);
        return node.Orientation == DockSplitOrientation.Horizontal
            ? new DockSize(first.Width + SplitterSize + second.Width,
                Math.Max(first.Height, second.Height))
            : new DockSize(Math.Max(first.Width, second.Width),
                first.Height + SplitterSize + second.Height);
    }

    public static DockArrangement Arrange(DockNodeState root, DockRect bounds)
    {
        var nodes = new List<DockArrangementNode>();
        ArrangeNode(root, bounds, nodes);
        return new DockArrangement(MeasureMinimum(root), nodes);
    }

    public static float ClampRatio(DockNodeState split, float availableAxis)
    {
        if (split.Kind != DockNodeKind.Split || split.First is null || split.Second is null ||
            split.Orientation is null)
            throw new ArgumentException("A complete split node is required.", nameof(split));
        var usable = Math.Max(0, availableAxis - SplitterSize);
        var first = MeasureMinimum(split.First);
        var second = MeasureMinimum(split.Second);
        var firstMinimum = split.Orientation == DockSplitOrientation.Horizontal
            ? first.Width : first.Height;
        var secondMinimum = split.Orientation == DockSplitOrientation.Horizontal
            ? second.Width : second.Height;
        var requested = Math.Clamp(split.Ratio ?? 0.5f,
            EditorLayoutValidator.MinimumStoredRatio, EditorLayoutValidator.MaximumStoredRatio);
        if (usable <= 0) return requested;
        if (usable < firstMinimum + secondMinimum)
            return firstMinimum + secondMinimum <= 0 ? requested :
                Math.Clamp(firstMinimum / (firstMinimum + secondMinimum), 0, 1);
        return Math.Clamp(requested, firstMinimum / usable, 1 - secondMinimum / usable);
    }

    private static void ArrangeNode(DockNodeState node, DockRect bounds,
        List<DockArrangementNode> result)
    {
        if (node.Kind == DockNodeKind.Tabs || node.First is null || node.Second is null ||
            node.Orientation is null)
        {
            result.Add(new(node.NodeId, bounds, null, false));
            return;
        }
        var axis = node.Orientation == DockSplitOrientation.Horizontal ? bounds.Width : bounds.Height;
        var ratio = ClampRatio(node, axis);
        result.Add(new(node.NodeId, bounds, ratio,
            Math.Abs(ratio - (node.Ratio ?? 0.5f)) > 0.0001f));
        var usable = Math.Max(0, axis - SplitterSize);
        var firstAxis = usable * ratio;
        DockRect firstBounds;
        DockRect secondBounds;
        if (node.Orientation == DockSplitOrientation.Horizontal)
        {
            firstBounds = new(bounds.X, bounds.Y, firstAxis, bounds.Height);
            secondBounds = new(bounds.X + firstAxis + SplitterSize, bounds.Y,
                Math.Max(0, usable - firstAxis), bounds.Height);
        }
        else
        {
            firstBounds = new(bounds.X, bounds.Y, bounds.Width, firstAxis);
            secondBounds = new(bounds.X, bounds.Y + firstAxis + SplitterSize,
                bounds.Width, Math.Max(0, usable - firstAxis));
        }
        ArrangeNode(node.First, firstBounds, result);
        ArrangeNode(node.Second, secondBounds, result);
    }

    private static DockSize PaneMinimum(string paneId) =>
        EditorPaneIds.IsContentManager(paneId) ? new(220, 180) : paneId switch
        {
            EditorPaneIds.Viewport => new(320, 240),
            EditorPaneIds.Hierarchy => new(180, 140),
            EditorPaneIds.Inspector => new(240, 180),
            EditorPaneIds.Console => new(300, 100),
            EditorPaneIds.Logs => new(420, 160),
            _ => new(160, 100),
        };
}

public enum EditorLayoutCommandKind
{
    ActivateTab,
    ReorderTab,
    MergeIntoCenter,
    SplitAtEdge,
    ResizeSplit,
    HidePane,
    ReopenAsFloating,
    DetachToFloating,
    CloseFloatingHost,
}

public sealed record EditorLayoutCommand(EditorLayoutCommandKind Kind, string PaneId = "",
    string? TargetGroupId = null, string? TargetNodeId = null, int Index = -1,
    DockEdge Edge = DockEdge.Right, float Ratio = 0.25f, string? TargetHostId = null,
    IReadOnlyList<EditorSplitRatio>? SplitRatios = null);

public sealed record EditorSplitRatio(string NodeId, float Ratio);

public sealed record EditorLayoutMutationResult(bool Success, string Code,
    string Message, string? TransactionId, long BeforeRevision, long AfterRevision,
    IReadOnlyList<EditorLayoutDiagnostic> Diagnostics);

public sealed class EditorLayoutSession
{
    private sealed record Snapshot(EditorLayoutDocument Document,
        List<FloatingDockHostState> FloatingHosts);

    private EditorLayoutDocument _document;
    private List<FloatingDockHostState> _floatingHosts = [];

    public EditorLayoutSession(EditorLayoutDocument initialDocument)
    {
        var diagnostics = EditorLayoutValidator.Validate(initialDocument);
        if (diagnostics.Count != 0)
            throw new EditorLayoutException("Cannot start an editor layout session.", diagnostics);
        _document = EditorLayoutJson.Clone(initialDocument);
    }

    public EditorLayoutDocument Document => EditorLayoutJson.Clone(_document);
    public IReadOnlyList<FloatingDockHostState> FloatingHosts => _floatingHosts
        .Select(CloneFloatingHost).ToArray();
    public long Revision { get; private set; }
    public bool IsDirty { get; private set; }

    public void UpdateContentManagers(IReadOnlyList<ContentManagerLayoutState> states,
        string activePaneId)
    {
        ArgumentNullException.ThrowIfNull(states);
        var candidate = EditorLayoutJson.Clone(_document);
        candidate.ActiveContentManagerId = activePaneId;
        candidate.ContentManagers = states.Select(CloneContentManager).ToList();
        var diagnostics = EditorLayoutValidator.ValidateSession(candidate, _floatingHosts);
        if (diagnostics.Count != 0)
            throw new EditorLayoutException("Cannot update content manager layout state.", diagnostics);
        if (candidate.ActiveContentManagerId == _document.ActiveContentManagerId &&
            JsonSerializer.Serialize(candidate.ContentManagers) ==
            JsonSerializer.Serialize(_document.ContentManagers)) return;
        _document = candidate;
        Revision++;
        IsDirty = true;
    }

    public EditorLayoutMutationResult Apply(EditorLayoutCommand command)
    {
        var beforeRevision = Revision;
        if (command.Kind is not (EditorLayoutCommandKind.CloseFloatingHost or
                EditorLayoutCommandKind.ResizeSplit) &&
            !EditorPaneIds.IsBuiltIn(command.PaneId))
            return Failure("layout.command.unknown-pane", $"Unknown pane '{command.PaneId}'.", beforeRevision);
        var candidate = Capture();
        var error = ApplyCore(candidate, command);
        if (error is not null) return Failure(error.Value.Code, error.Value.Message, beforeRevision);
        var diagnostics = EditorLayoutValidator.ValidateSession(candidate.Document, candidate.FloatingHosts);
        if (diagnostics.Count != 0)
            return new(false, "layout.command.invalid-result", "The layout command produced an invalid state.",
                null, beforeRevision, beforeRevision, diagnostics);
        Commit(candidate);
        return new(true, "layout.command.applied", "The layout command was applied.",
            Guid.NewGuid().ToString("N"), beforeRevision, Revision, []);
    }

    public void ResetToEngineDefault(EditorLayoutDocument engineDefault)
    {
        var diagnostics = EditorLayoutValidator.Validate(engineDefault, requireAllPanesInMain: true);
        if (diagnostics.Count != 0)
            throw new EditorLayoutException("Cannot reset to an invalid engine default layout.", diagnostics);
        _document = EditorLayoutJson.Clone(engineDefault);
        _floatingHosts.Clear();
        Revision++;
        IsDirty = true;
    }

    public EditorLayoutDocument CreatePersistentSnapshot()
    {
        var snapshot = EditorLayoutJson.Clone(_document);
        snapshot.HiddenPaneIds.AddRange(_floatingHosts.SelectMany(host =>
            EditorLayoutValidator.EnumeratePanes(host.Root)));
        snapshot.HiddenPaneIds = snapshot.HiddenPaneIds.Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal).ToList();
        var diagnostics = EditorLayoutValidator.Validate(snapshot);
        if (diagnostics.Count != 0)
            throw new EditorLayoutException("Cannot persist the current editor layout.", diagnostics);
        return snapshot;
    }

    public void Save(EditorLayoutStore store, string engineRoot, string? projectRoot)
    {
        store.Save(engineRoot, projectRoot, CreatePersistentSnapshot());
        IsDirty = false;
    }

    private (string Code, string Message)? ApplyCore(Snapshot snapshot, EditorLayoutCommand command)
    {
        if (command.PaneId == EditorPaneIds.Viewport &&
            command.Kind is EditorLayoutCommandKind.HidePane or
                EditorLayoutCommandKind.ReopenAsFloating or
                EditorLayoutCommandKind.DetachToFloating)
            return ("layout.viewport.main-only", "The viewport must remain in the main host.");

        return command.Kind switch
        {
            EditorLayoutCommandKind.ActivateTab => ActivateTab(snapshot, command),
            EditorLayoutCommandKind.ReorderTab => ReorderTab(snapshot, command),
            EditorLayoutCommandKind.MergeIntoCenter => MergeIntoCenter(snapshot, command),
            EditorLayoutCommandKind.SplitAtEdge => SplitAtEdge(snapshot, command),
            EditorLayoutCommandKind.ResizeSplit => ResizeSplit(snapshot, command),
            EditorLayoutCommandKind.HidePane => HidePane(snapshot, command.PaneId),
            EditorLayoutCommandKind.ReopenAsFloating => ReopenAsFloating(snapshot, command.PaneId),
            EditorLayoutCommandKind.DetachToFloating => DetachToFloating(snapshot, command.PaneId),
            EditorLayoutCommandKind.CloseFloatingHost => CloseFloatingHost(snapshot, command.TargetHostId),
            _ => ("layout.command.unsupported", "The layout command is not supported."),
        };
    }

    private static (string, string)? ActivateTab(Snapshot snapshot,
        EditorLayoutCommand command)
    {
        var group = FindGroup(snapshot, command.TargetGroupId, command.TargetHostId);
        if (group is null)
            return ("layout.command.group", "The target tab group was not found.");
        if (!group.Panes.Any(pane => pane.PaneId == command.PaneId))
            return ("layout.command.pane", "The pane does not belong to the target tab group.");
        group.ActivePaneId = command.PaneId;
        return null;
    }

    private static (string, string)? ReorderTab(Snapshot snapshot, EditorLayoutCommand command)
    {
        var group = FindGroup(snapshot, command.TargetGroupId, command.TargetHostId);
        if (group is null || !group.Panes.Any(pane => pane.PaneId == command.PaneId))
            return ("layout.command.group", "The pane does not belong to the requested tab group.");
        var pane = group.Panes.Single(item => item.PaneId == command.PaneId);
        group.Panes.Remove(pane);
        group.Panes.Insert(InsertionIndex(command.Index, group.Panes.Count), pane);
        group.ActivePaneId = command.PaneId;
        return null;
    }

    private static (string, string)? MergeIntoCenter(Snapshot snapshot, EditorLayoutCommand command)
    {
        var targetHostId = FindGroupHostId(snapshot, command.TargetGroupId, command.TargetHostId);
        if (targetHostId is null)
            return ("layout.command.target", "The target tab group was not found.");
        if (command.PaneId == EditorPaneIds.Viewport && targetHostId != "main")
            return ("layout.viewport.main-only", "The viewport cannot move to a floating host.");
        var target = FindGroup(snapshot, command.TargetGroupId, targetHostId);
        if (target is null) return ("layout.command.target", "The target tab group was not found.");
        RemovePane(snapshot, command.PaneId);
        target = FindGroup(snapshot, command.TargetGroupId, targetHostId);
        if (target is null) return ("layout.command.target", "The target group was removed with the source pane.");
        target.Panes.Insert(InsertionIndex(command.Index, target.Panes.Count),
            new DockPaneState { PaneId = command.PaneId });
        target.ActivePaneId = command.PaneId;
        return null;
    }

    private static (string, string)? SplitAtEdge(Snapshot snapshot, EditorLayoutCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.TargetNodeId))
            return ("layout.command.target", "A target node ID is required.");
        if (command.Ratio is < EditorLayoutValidator.MinimumStoredRatio or
            > EditorLayoutValidator.MaximumStoredRatio || !float.IsFinite(command.Ratio))
            return ("layout.command.ratio", "The requested split ratio is invalid.");
        var targetHostId = FindNodeHostId(snapshot, command.TargetNodeId, command.TargetHostId);
        if (targetHostId is null)
            return ("layout.command.target", "The target node was not found.");
        if (command.PaneId == EditorPaneIds.Viewport && targetHostId != "main")
            return ("layout.viewport.main-only", "The viewport cannot move to a floating host.");
        RemovePane(snapshot, command.PaneId);
        var target = FindNode(snapshot, command.TargetNodeId, targetHostId);
        if (target is null) return ("layout.command.target", "The target node was not found after removing the source pane.");
        if (command.PaneId != EditorPaneIds.Viewport &&
            EditorLayoutValidator.EnumeratePanes(target).Contains(EditorPaneIds.Viewport, StringComparer.Ordinal) &&
            target.Kind == DockNodeKind.Tabs)
        {
            // Edge splitting is allowed around the viewport, while center merge is not.
        }
        var existing = EditorLayoutJson.Clone(target);
        existing.NodeId = NextId(snapshot, $"node.existing.{command.PaneId}");
        var paneNode = new DockNodeState
        {
            NodeId = NextId(snapshot, $"node.{command.PaneId}"),
            Kind = DockNodeKind.Tabs,
            TabGroup = new DockTabGroupState
            {
                GroupId = NextId(snapshot, $"group.{command.PaneId}"),
                ActivePaneId = command.PaneId,
                Panes = [new DockPaneState { PaneId = command.PaneId }],
            },
        };
        var paneFirst = command.Edge is DockEdge.Left or DockEdge.Top;
        target.Kind = DockNodeKind.Split;
        target.Orientation = command.Edge is DockEdge.Left or DockEdge.Right
            ? DockSplitOrientation.Horizontal : DockSplitOrientation.Vertical;
        target.Ratio = paneFirst ? command.Ratio : 1 - command.Ratio;
        target.First = paneFirst ? paneNode : existing;
        target.Second = paneFirst ? existing : paneNode;
        target.TabGroup = null;
        return null;
    }

    private static (string, string)? ResizeSplit(Snapshot snapshot,
        EditorLayoutCommand command)
    {
        var requested = command.SplitRatios is { Count: > 0 }
            ? command.SplitRatios
            : string.IsNullOrWhiteSpace(command.TargetNodeId)
                ? null
                : [new EditorSplitRatio(command.TargetNodeId, command.Ratio)];
        if (requested is null)
            return ("layout.command.target", "At least one target node ID is required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resize in requested)
        {
            if (!seen.Add(resize.NodeId))
                return ("layout.command.target", $"Split node '{resize.NodeId}' was requested more than once.");
            if (!float.IsFinite(resize.Ratio))
                return ("layout.command.ratio", "Every requested split ratio must be finite.");
            var targetHostId = FindNodeHostId(snapshot, resize.NodeId,
                command.TargetHostId);
            if (targetHostId is null)
                return ("layout.command.target", $"Split node '{resize.NodeId}' was not found.");
            var target = FindNode(snapshot, resize.NodeId, targetHostId);
            if (target is null || target.Kind != DockNodeKind.Split)
                return ("layout.command.not-split", $"Node '{resize.NodeId}' is not a split node.");
            target.Ratio = Math.Clamp(resize.Ratio,
                EditorLayoutValidator.MinimumStoredRatio,
                EditorLayoutValidator.MaximumStoredRatio);
        }
        return null;
    }

    private static (string, string)? HidePane(Snapshot snapshot, string paneId)
    {
        if (!RemovePane(snapshot, paneId))
            return ("layout.command.pane", "The pane is already hidden or was not found.");
        snapshot.Document.HiddenPaneIds.Add(paneId);
        snapshot.Document.HiddenPaneIds.Sort(StringComparer.Ordinal);
        return null;
    }

    private static (string, string)? ReopenAsFloating(Snapshot snapshot, string paneId)
    {
        if (!snapshot.Document.HiddenPaneIds.Remove(paneId))
            return ("layout.command.pane", "Only a hidden pane can be reopened as floating.");
        AddFloatingHost(snapshot, paneId);
        return null;
    }

    private static (string, string)? DetachToFloating(Snapshot snapshot, string paneId)
    {
        if (!IsPaneDocked(snapshot, paneId))
            return ("layout.command.pane", "Only a pane currently docked in a host can be detached.");
        if (!RemovePane(snapshot, paneId))
            return ("layout.command.pane", "The pane could not be removed from its current host.");
        AddFloatingHost(snapshot, paneId);
        return null;
    }

    private static (string, string)? CloseFloatingHost(Snapshot snapshot, string? hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId) || hostId == "main")
            return ("layout.command.host", "A floating host ID is required.");
        var index = snapshot.FloatingHosts.FindIndex(host => host.HostId == hostId);
        if (index < 0)
            return ("layout.command.host", $"Floating host '{hostId}' was not found.");
        var paneIds = EditorLayoutValidator.EnumeratePanes(
            snapshot.FloatingHosts[index].Root).ToArray();
        snapshot.FloatingHosts.RemoveAt(index);
        snapshot.Document.HiddenPaneIds.AddRange(paneIds);
        snapshot.Document.HiddenPaneIds = snapshot.Document.HiddenPaneIds
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        return null;
    }

    private static void AddFloatingHost(Snapshot snapshot, string paneId)
    {
        var hostId = NextId(snapshot, $"float.{paneId}");
        snapshot.FloatingHosts.Add(new(hostId, new DockRect(120, 120, 480, 360),
            new DockNodeState
            {
                NodeId = NextId(snapshot, $"node.{paneId}"),
                Kind = DockNodeKind.Tabs,
                TabGroup = new DockTabGroupState
                {
                    GroupId = NextId(snapshot, $"group.{paneId}"),
                    ActivePaneId = paneId,
                    Panes = [new DockPaneState { PaneId = paneId }],
                },
            }));
    }

    private static bool RemovePane(Snapshot snapshot, string paneId)
    {
        snapshot.Document.HiddenPaneIds.Remove(paneId);
        var root = RemovePane(snapshot.Document.MainHost.Root, paneId, out var removed);
        if (root is not null) snapshot.Document.MainHost.Root = root;
        for (var index = snapshot.FloatingHosts.Count - 1; index >= 0; index--)
        {
            var host = snapshot.FloatingHosts[index];
            var floatRoot = RemovePane(host.Root, paneId, out var removedFromFloat);
            removed |= removedFromFloat;
            if (floatRoot is null) snapshot.FloatingHosts.RemoveAt(index);
            else snapshot.FloatingHosts[index] = host with { Root = floatRoot };
        }
        return removed;
    }

    private static DockNodeState? RemovePane(DockNodeState node, string paneId, out bool removed)
    {
        removed = false;
        if (node.Kind == DockNodeKind.Tabs)
        {
            var group = node.TabGroup;
            if (group is null) return node;
            var index = group.Panes.FindIndex(pane => pane.PaneId == paneId);
            if (index < 0) return node;
            group.Panes.RemoveAt(index);
            removed = true;
            if (group.Panes.Count == 0) return null;
            if (group.ActivePaneId == paneId) group.ActivePaneId = group.Panes[0].PaneId;
            return node;
        }
        if (node.First is not null)
        {
            node.First = RemovePane(node.First, paneId, out var firstRemoved);
            removed |= firstRemoved;
        }
        if (node.Second is not null)
        {
            node.Second = RemovePane(node.Second, paneId, out var secondRemoved);
            removed |= secondRemoved;
        }
        if (node.First is null) return node.Second;
        if (node.Second is null) return node.First;
        return node;
    }

    private static bool IsPaneDocked(Snapshot snapshot, string paneId) =>
        EditorLayoutValidator.EnumeratePanes(snapshot.Document.MainHost.Root)
            .Contains(paneId, StringComparer.Ordinal) ||
        snapshot.FloatingHosts.Any(host => EditorLayoutValidator.EnumeratePanes(host.Root)
            .Contains(paneId, StringComparer.Ordinal));

    private static DockTabGroupState? FindGroup(Snapshot snapshot, string? groupId,
        string? hostId = null)
    {
        if (hostId is null or "main")
        {
            var mainGroup = FindGroup(snapshot.Document.MainHost.Root, groupId);
            if (mainGroup is not null || hostId == "main") return mainGroup;
        }
        foreach (var host in snapshot.FloatingHosts)
        {
            if (hostId is not null && host.HostId != hostId) continue;
            var group = FindGroup(host.Root, groupId);
            if (group is not null) return group;
        }
        return null;
    }

    private static string? FindGroupHostId(Snapshot snapshot, string? groupId,
        string? requestedHostId)
    {
        if (requestedHostId is not null)
            return FindGroup(snapshot, groupId, requestedHostId) is null ? null : requestedHostId;
        if (FindGroup(snapshot.Document.MainHost.Root, groupId) is not null) return "main";
        return snapshot.FloatingHosts.FirstOrDefault(host =>
            FindGroup(host.Root, groupId) is not null)?.HostId;
    }

    private static DockTabGroupState? FindGroup(DockNodeState node, string? groupId)
    {
        if (node.Kind == DockNodeKind.Tabs)
            return node.TabGroup?.GroupId == groupId ? node.TabGroup : null;
        return node.First is null ? null : FindGroup(node.First, groupId) ??
            (node.Second is null ? null : FindGroup(node.Second, groupId));
    }

    private static DockNodeState? FindNode(DockNodeState node, string nodeId)
    {
        if (node.NodeId == nodeId) return node;
        return node.First is null ? null : FindNode(node.First, nodeId) ??
            (node.Second is null ? null : FindNode(node.Second, nodeId));
    }

    private static DockNodeState? FindNode(Snapshot snapshot, string nodeId, string hostId)
    {
        if (hostId == "main") return FindNode(snapshot.Document.MainHost.Root, nodeId);
        var host = snapshot.FloatingHosts.FirstOrDefault(item => item.HostId == hostId);
        return host is null ? null : FindNode(host.Root, nodeId);
    }

    private static string? FindNodeHostId(Snapshot snapshot, string nodeId,
        string? requestedHostId)
    {
        if (requestedHostId is not null)
            return FindNode(snapshot, nodeId, requestedHostId) is null ? null : requestedHostId;
        if (FindNode(snapshot.Document.MainHost.Root, nodeId) is not null) return "main";
        return snapshot.FloatingHosts.FirstOrDefault(host =>
            FindNode(host.Root, nodeId) is not null)?.HostId;
    }

    private static string NextId(Snapshot snapshot, string prefix)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        CollectIds(snapshot.Document.MainHost.Root, used);
        foreach (var host in snapshot.FloatingHosts)
        {
            used.Add(host.HostId);
            CollectIds(host.Root, used);
        }
        for (var suffix = 1; ; suffix++)
        {
            var candidate = $"{prefix}.{suffix}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private static void CollectIds(DockNodeState node, HashSet<string> used)
    {
        used.Add(node.NodeId);
        if (node.TabGroup is not null) used.Add(node.TabGroup.GroupId);
        if (node.First is not null) CollectIds(node.First, used);
        if (node.Second is not null) CollectIds(node.Second, used);
    }

    private static int InsertionIndex(int requested, int count) =>
        requested < 0 ? count : Math.Clamp(requested, 0, count);

    private Snapshot Capture() => new(EditorLayoutJson.Clone(_document),
        _floatingHosts.Select(CloneFloatingHost).ToList());

    private void Commit(Snapshot candidate)
    {
        Restore(candidate);
        Revision++;
        IsDirty = true;
    }

    private void Restore(Snapshot snapshot)
    {
        _document = EditorLayoutJson.Clone(snapshot.Document);
        _floatingHosts = snapshot.FloatingHosts.Select(CloneFloatingHost).ToList();
    }

    private static FloatingDockHostState CloneFloatingHost(FloatingDockHostState host) =>
        host with { Root = EditorLayoutJson.Clone(host.Root) };

    private static ContentManagerLayoutState CloneContentManager(ContentManagerLayoutState state) =>
        new()
        {
            PaneId = state.PaneId,
            Search = state.Search,
            Source = state.Source,
            Type = state.Type,
            CurrentFolder = state.CurrentFolder,
            SelectedId = state.SelectedId,
            SelectedPath = state.SelectedPath,
            SelectedSource = state.SelectedSource,
            ViewMode = state.ViewMode,
        };

    private static EditorLayoutMutationResult Failure(string code, string message, long revision) =>
        new(false, code, message, null, revision, revision, []);
}
