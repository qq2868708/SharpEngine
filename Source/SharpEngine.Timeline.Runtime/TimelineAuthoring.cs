namespace SharpEngine.Timeline;

public sealed record TimelineTransactionRecord(
    Guid TransactionId,
    string Kind,
    ulong BeforeRevision,
    ulong AfterRevision);

public sealed record TimelineEditResult(
    bool Success,
    string Code,
    string Message,
    ulong BeforeRevision,
    ulong AfterRevision,
    bool IsDirty,
    TimelineTransactionRecord? Transaction = null)
{
    public static TimelineEditResult Failure(string Code, string Message,
        ulong Revision, bool IsDirty)
    {
        return new TimelineEditResult(false, Code, Message, Revision, Revision, IsDirty);
    }
}

/// <summary>
/// Owns authoring edits for one Timeline asset. Playback and selection remain
/// transient in the editor; this session owns the serialized TimelineDocument,
/// revision, dirty state and reversible edit history.
/// </summary>
public sealed class TimelineAuthoringSession
{
    private sealed record HistoryEntry(TimelineDocument Before, TimelineDocument After,
        string Kind);

    private readonly int OwnerThreadId = Environment.CurrentManagedThreadId;
    private readonly List<HistoryEntry> History = new();
    private string Savepoint;
    private int HistoryCursor;

    public TimelineAuthoringSession(TimelineDocument Document, ulong InitialRevision = 1)
    {
        this.Document = Document ?? throw new ArgumentNullException(nameof(Document));
        DocumentRevision = InitialRevision;
        SavepointRevision = InitialRevision;
        Savepoint = Document.Serialize(false);
    }

    public TimelineDocument Document { get; private set; }
    public ulong DocumentRevision { get; private set; }
    public ulong SavepointRevision { get; private set; }
    public bool IsDirty => !string.Equals(Savepoint, Document.Serialize(false), StringComparison.Ordinal);
    public bool CanUndo => HistoryCursor > 0;
    public bool CanRedo => HistoryCursor < History.Count;
    public string? UndoDescription => CanUndo ? History[HistoryCursor - 1].Kind : null;
    public string? RedoDescription => CanRedo ? History[HistoryCursor].Kind : null;

    public TimelineEditResult Replace(TimelineDocument Replacement, string Kind,
        ulong ExpectedRevision)
    {
        ArgumentNullException.ThrowIfNull(Replacement);
        EnsureOwnerThread();
        if (ExpectedRevision != DocumentRevision)
        {
            return TimelineEditResult.Failure("REVISION_CONFLICT",
                $"Expected Timeline revision {ExpectedRevision}, current revision is {DocumentRevision}",
                DocumentRevision, IsDirty);
        }

        if (string.Equals(Document.Serialize(false), Replacement.Serialize(false),
            StringComparison.Ordinal))
        {
            return TimelineEditResult.Failure("NO_CHANGE", "Timeline already has this state",
                DocumentRevision, IsDirty);
        }

        var Before = Document;
        Document = Replacement;
        if (HistoryCursor < History.Count)
        {
            History.RemoveRange(HistoryCursor, History.Count - HistoryCursor);
        }

        History.Add(new HistoryEntry(Before, Replacement, RequireKind(Kind)));
        HistoryCursor++;
        var BeforeRevision = DocumentRevision;
        DocumentRevision++;
        return Success("TIMELINE_EDIT_APPLIED", "Timeline edit applied", BeforeRevision,
            RequireKind(Kind));
    }

    public TimelineEditResult Undo(ulong ExpectedRevision)
    {
        EnsureOwnerThread();
        if (ExpectedRevision != DocumentRevision)
        {
            return TimelineEditResult.Failure("REVISION_CONFLICT",
                $"Expected Timeline revision {ExpectedRevision}, current revision is {DocumentRevision}",
                DocumentRevision, IsDirty);
        }
        if (!CanUndo)
        {
            return TimelineEditResult.Failure("NOTHING_TO_UNDO", "There is no Timeline edit to undo",
                DocumentRevision, IsDirty);
        }

        var Entry = History[HistoryCursor - 1];
        Document = Entry.Before;
        HistoryCursor--;
        var BeforeRevision = DocumentRevision;
        DocumentRevision++;
        return Success("TIMELINE_UNDO_APPLIED", "Timeline edit undone", BeforeRevision, "undo");
    }

    public TimelineEditResult Redo(ulong ExpectedRevision)
    {
        EnsureOwnerThread();
        if (ExpectedRevision != DocumentRevision)
        {
            return TimelineEditResult.Failure("REVISION_CONFLICT",
                $"Expected Timeline revision {ExpectedRevision}, current revision is {DocumentRevision}",
                DocumentRevision, IsDirty);
        }
        if (!CanRedo)
        {
            return TimelineEditResult.Failure("NOTHING_TO_REDO", "There is no Timeline edit to redo",
                DocumentRevision, IsDirty);
        }

        var Entry = History[HistoryCursor];
        Document = Entry.After;
        HistoryCursor++;
        var BeforeRevision = DocumentRevision;
        DocumentRevision++;
        return Success("TIMELINE_REDO_APPLIED", "Timeline edit redone", BeforeRevision, "redo");
    }

    public void Save(string Path)
    {
        EnsureOwnerThread();
        var FullPath = System.IO.Path.GetFullPath(Path ?? throw new ArgumentNullException(nameof(Path)));
        var Directory = System.IO.Path.GetDirectoryName(FullPath) ??
            throw new InvalidOperationException("Timeline save path has no directory");
        System.IO.Directory.CreateDirectory(Directory);
        var TemporaryPath = FullPath + $".tmp-{Guid.NewGuid():N}";
        var Serialized = Document.Serialize(true) + Environment.NewLine;
        TimelineDocument.Parse(Serialized, FullPath);
        try
        {
            File.WriteAllText(TemporaryPath, Serialized, new System.Text.UTF8Encoding(false));
            File.Move(TemporaryPath, FullPath, true);
        }
        finally
        {
            if (File.Exists(TemporaryPath))
            {
                File.Delete(TemporaryPath);
            }
        }

        MarkSavepoint();
    }

    public void MarkSavepoint()
    {
        EnsureOwnerThread();
        Savepoint = Document.Serialize(false);
        SavepointRevision = DocumentRevision;
    }

    private TimelineEditResult Success(string Code, string Message, ulong BeforeRevision,
        string Kind)
    {
        return new TimelineEditResult(true, Code, Message, BeforeRevision, DocumentRevision,
            IsDirty, new TimelineTransactionRecord(Guid.NewGuid(), Kind, BeforeRevision,
                DocumentRevision));
    }

    private static string RequireKind(string Value)
    {
        return string.IsNullOrWhiteSpace(Value) ? throw new ArgumentException(
            "Timeline edit kind is required", nameof(Value)) : Value.Trim();
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != OwnerThreadId)
        {
            throw new InvalidOperationException("Timeline authoring edits must run on the editor thread");
        }
    }
}
