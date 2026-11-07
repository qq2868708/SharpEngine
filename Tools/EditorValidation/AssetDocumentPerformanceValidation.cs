using System.Diagnostics;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor;

internal static class AssetDocumentPerformanceValidation
{
    internal static void Run(string ProjectRoot, Action<bool, string> Check)
    {
        var Folder = Directory.CreateDirectory(Path.Combine(ProjectRoot, "AssetDocumentPerformance"));
        var PathValue = Path.Combine(Folder.FullName, "Large.json");
        var Original = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["name"] = "Original",
            ["payload"] = new string('x', 1024 * 1024),
        };
        File.WriteAllText(PathValue, Original.ToJsonString());
        var Asset = new AssetDescriptor
        {
            Id = "project://validation/large-document",
            Source = AssetSource.Project,
        };
        var Document = new JsonAssetAuthoringDocument(Asset, PathValue, Value =>
        {
            if (string.IsNullOrWhiteSpace(Value["name"]!.GetValue<string>()))
            {
                throw new InvalidDataException("Name is required");
            }
        });
        var OriginalSource = Document.Source;
        var Markup = AssetEditorInstanceUtilities.Source(OriginalSource);
        Check(!Document.IsDirty, "A newly opened large asset starts at its clean savepoint");

        var AllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var Started = Stopwatch.GetTimestamp();
        var Stable = true;
        for (var Index = 0; Index < 3000; Index++)
        {
            Stable &= !Document.IsDirty;
            Stable &= AssetEditorInstanceUtilities.Source(Document.Source) == Markup;
        }
        var Elapsed = Stopwatch.GetElapsedTime(Started).TotalMilliseconds;
        var Allocated = GC.GetAllocatedBytesForCurrentThread() - AllocatedBefore;
        Console.WriteLine($"Large asset unchanged UI reads: 3000, {Allocated} allocated bytes, {Elapsed:0.###} ms");
        Check(Stable && Allocated < 64 * 1024,
            $"3000 unchanged large-asset UI reads allocate under 64 KiB: {Allocated} bytes, {Elapsed:0.###} ms");

        var Revision = Document.Revision;
        Check(!Document.Edit("same-value", Value => Value["name"] = "Original", out _) &&
            Document.Revision == Revision && !Document.IsDirty,
            "A no-op edit preserves revision and the clean savepoint");
        Check(Document.Edit("rename", Value => Value["name"] = "Changed", out _) &&
            Document.IsDirty && JsonNode.Parse(Document.Source)!["name"]!.GetValue<string>() == "Changed",
            "A committed edit immediately invalidates the derived source and dirty state");
        var ChangedSource = Document.Source;
        Check(Document.Undo() && !Document.IsDirty && Document.Source == OriginalSource,
            "Undo restores source content and clean state at the original savepoint");
        Check(Document.Redo() && Document.IsDirty && Document.Source == ChangedSource,
            "Redo restores the edited source and dirty state");
        Document.Save();
        Check(!Document.IsDirty && Document.SavepointRevision == Document.Revision &&
            File.ReadAllText(PathValue) == Document.Source,
            "Save commits the current source and moves the clean savepoint");
        Check(Document.Undo() && Document.IsDirty && Document.Source == OriginalSource,
            "Undo past a saved edit is dirty even when the revision is newer");
        Check(Document.Redo() && !Document.IsDirty && Document.Source == ChangedSource,
            "Redo to saved content is clean even when its revision differs from the savepoint revision");
        Check(!Document.Edit("invalid", Value => Value["name"] = "", out _) &&
            !Document.IsDirty && Document.Source == ChangedSource,
            "Rejected edits retain the previous committed caches");
        var Snapshot = Document.Snapshot;
        Snapshot["name"] = "Uncommitted snapshot";
        Check(!Document.IsDirty && Document.Source == ChangedSource,
            "Mutating an observation copy cannot invalidate or change authoritative state");

        File.WriteAllText(PathValue, Original.ToJsonString());
        var Rejected = false;
        try
        {
            Document.Save();
        }
        catch (InvalidOperationException)
        {
            Rejected = true;
        }
        Check(Rejected && !Document.IsDirty && Document.Source == ChangedSource,
            "External source conflicts preserve the latest committed live state");

        var Lines = string.Join("\r\n", Enumerable.Repeat("<tag>" + new string('y', 200), 140));
        var Preview = AssetEditorInstanceUtilities.Source(Lines);
        Check(Preview.Contains("&lt;tag&gt;", StringComparison.Ordinal) &&
            Preview.Contains("20 additional lines", StringComparison.Ordinal) &&
            !Preview.Contains(new string('y', 200), StringComparison.Ordinal),
            "Cached source previews preserve escaping and existing line/column limits");
    }
}
