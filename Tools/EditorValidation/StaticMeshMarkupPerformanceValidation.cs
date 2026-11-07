using System.Diagnostics;
using System.Text.Json.Nodes;
using SharpEngine.Editor;

internal static class StaticMeshMarkupPerformanceValidation
{
    internal static void Run(EditorRuntime Runtime, string ProjectRoot, Action<bool, string> Check)
    {
        var FilePath = Path.Combine(ProjectRoot, "Content", "Media", "SocketMarkupCache.sstaticmesh");
        var Source = JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectRoot, "Content", "Media", "Validation.sstaticmesh")))!;
        Source["schemaVersion"] = 3;
        Source["sockets"] = new JsonArray(Enumerable.Range(0, 4096).Select(Index => new JsonObject
        {
            ["socketId"] = "cache-" + Index,
            ["name"] = "Cache Node " + Index,
            ["parentSocketId"] = Index == 0 ? null : "cache-" + (Index - 1),
            ["position"] = new JsonArray(0.0001, 0, 0),
            ["rotation"] = new JsonArray(0, 0, 0, 1),
            ["scale"] = new JsonArray(1, 1, 1),
        }).ToArray());
        File.WriteAllText(FilePath, Source.ToJsonString());
        var Asset = Runtime.Assets!.Find(BasicAssetFixture.StaticMeshId)!;
        var Context = new EditorAssetOpenContext(Runtime, Asset, FilePath, "mesh-editor", "socket-markup-cache-validation");
        using var Instance = new MeshAssetEditorInstance(Context);
        Instance.TryHandleAction("editor.mesh-socket:select:cache-2048");
        var Tree = Instance.BuildPaneMarkup("sockets");
        var Details = Instance.BuildPaneMarkup("details");
        var Before = GC.GetAllocatedBytesForCurrentThread();
        var Started = Stopwatch.GetTimestamp();
        var Stable = true;
        for (var Index = 0; Index < 3000; Index++)
        {
            Stable &= Instance.BuildPaneMarkup("sockets") == Tree;
            Stable &= Instance.BuildPaneMarkup("details") == Details;
            Stable &= !Instance.IsDirty;
        }
        var Allocated = GC.GetAllocatedBytesForCurrentThread() - Before;
        var Elapsed = Stopwatch.GetElapsedTime(Started).TotalMilliseconds;
        Console.WriteLine($"4096-Socket unchanged pane reads: 3000, {Allocated} allocated bytes, {Elapsed:0.###} ms");
        Check(Stable && Allocated < 64 * 1024,
            "Unchanged large Socket trees and Parent selectors reuse derived presentation within a 64 KiB allocation budget");
        Instance.TryHandleAction("editor.mesh-socket:field:name:Updated Cached Socket");
        Check(Instance.IsDirty && Instance.BuildPaneMarkup("sockets").Contains("Updated Cached Socket", StringComparison.Ordinal) &&
            Instance.BuildPaneMarkup("details").Contains("Updated Cached Socket", StringComparison.Ordinal),
            "Socket pane caches immediately observe a committed authoring change");
        Instance.ExecuteCommand("undo");
        Check(!Instance.IsDirty && Instance.BuildPaneMarkup("sockets") == Tree && Instance.BuildPaneMarkup("details") == Details,
            "Undo invalidates Socket presentation and returns to the clean large-document savepoint");
    }
}
