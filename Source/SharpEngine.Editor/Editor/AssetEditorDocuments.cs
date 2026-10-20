using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;

namespace SharpEngine.Editor;

internal sealed record AssetDocumentDiagnostic(string Severity, string Code,
    string Message);

internal sealed class JsonAssetAuthoringDocument
{
    private readonly Stack<JsonObject> UndoStates = [];
    private readonly Stack<JsonObject> RedoStates = [];
    private readonly Action<JsonObject> Validate;
    private JsonObject Document;
    private string SavedState;
    private string SourceHash;

    public JsonAssetAuthoringDocument(AssetDescriptor Asset, string Path,
        Action<JsonObject> Validate)
    {
        this.Asset = Asset;
        this.Path = System.IO.Path.GetFullPath(Path);
        this.Validate = Validate;
        Document = Parse(File.ReadAllText(this.Path));
        Validate(Document);
        SavedState = Canonical(Document);
        SourceHash = HashSource();
    }

    public AssetDescriptor Asset { get; }
    public string Path { get; }
    public ulong Revision { get; private set; } = 1;
    public ulong SavepointRevision { get; private set; } = 1;
    public bool IsReadOnly => Asset.ReadOnly;
    public bool IsDirty => Canonical(Document) != SavedState;
    public bool CanUndo => UndoStates.Count != 0;
    public bool CanRedo => RedoStates.Count != 0;
    public JsonObject Snapshot => Document.DeepClone().AsObject();
    public string Source => Serialize(Document);

    public bool Edit(string Kind, Action<JsonObject> Mutation, out string Message)
    {
        Message = string.Empty;
        if (IsReadOnly)
        {
            Message = "This asset is read only";
            return false;
        }

        var Before = Document.DeepClone().AsObject();
        var Candidate = Document.DeepClone().AsObject();
        try
        {
            Mutation(Candidate);
            Validate(Candidate);
        }
        catch (Exception Exception) when (Exception is InvalidDataException or
            JsonException or InvalidOperationException or FormatException or
            ArgumentException or OverflowException)
        {
            Message = Exception.Message;
            return false;
        }

        if (Canonical(Before) == Canonical(Candidate))
        {
            return false;
        }

        UndoStates.Push(Before);
        RedoStates.Clear();
        Document = Candidate;
        Revision++;
        Message = Kind;
        return true;
    }

    public bool Undo()
    {
        if (UndoStates.Count == 0)
        {
            return false;
        }

        RedoStates.Push(Document.DeepClone().AsObject());
        Document = UndoStates.Pop();
        Revision++;
        return true;
    }

    public bool Redo()
    {
        if (RedoStates.Count == 0)
        {
            return false;
        }

        UndoStates.Push(Document.DeepClone().AsObject());
        Document = RedoStates.Pop();
        Revision++;
        return true;
    }

    public void Save()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("This asset is read only");
        }
        if (!string.Equals(SourceHash, HashSource(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Asset source changed outside this editor; reopen before saving");
        }

        Validate(Document);
        var Serialized = Serialize(Document);
        var Parsed = Parse(Serialized);
        Validate(Parsed);
        AtomicWrite(Serialized);
        Document = Parsed;
        SavedState = Canonical(Document);
        SourceHash = HashSource();
        SavepointRevision = Revision;
    }

    private void AtomicWrite(string Text)
    {
        var DirectoryPath = System.IO.Path.GetDirectoryName(Path) ??
            throw new InvalidDataException("Asset source has no parent directory");
        Directory.CreateDirectory(DirectoryPath);
        var TemporaryPath = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(TemporaryPath, Text, new UTF8Encoding(false));
            File.Move(TemporaryPath, Path, true);
        }
        finally
        {
            if (File.Exists(TemporaryPath))
            {
                File.Delete(TemporaryPath);
            }
        }
    }

    private string HashSource() => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(Path)));

    private static JsonObject Parse(string Text)
    {
        using var Syntax = JsonDocument.Parse(Text);
        EnsureNoDuplicateProperties(Syntax.RootElement);
        return JsonNode.Parse(Text)?.AsObject() ??
            throw new InvalidDataException("Asset root must be a JSON object");
    }

    private static string Serialize(JsonObject Value) =>
        Value.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }) + Environment.NewLine;

    private static string Canonical(JsonObject Value) =>
        Value.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
        });

    private static void EnsureNoDuplicateProperties(JsonElement Element)
    {
        if (Element.ValueKind == JsonValueKind.Object)
        {
            var Names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var Property in Element.EnumerateObject())
            {
                if (!Names.Add(Property.Name))
                {
                    throw new InvalidDataException(
                        $"Duplicate JSON field '{Property.Name}'");
                }
                EnsureNoDuplicateProperties(Property.Value);
            }
        }
        else if (Element.ValueKind == JsonValueKind.Array)
        {
            foreach (var Item in Element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(Item);
            }
        }
    }
}

internal sealed class TextAssetAuthoringDocument
{
    private readonly Stack<string> UndoStates = [];
    private readonly Stack<string> RedoStates = [];
    private readonly Func<string, IReadOnlyList<AssetDocumentDiagnostic>> Validate;
    private string Text;
    private string SavedText;
    private string SourceHash;

    public TextAssetAuthoringDocument(AssetDescriptor Asset, string Path,
        Func<string, IReadOnlyList<AssetDocumentDiagnostic>> Validate)
    {
        this.Asset = Asset;
        this.Path = System.IO.Path.GetFullPath(Path);
        this.Validate = Validate;
        Text = File.ReadAllText(this.Path);
        SavedText = Text;
        SourceHash = HashSource();
        Diagnostics = Validate(Text);
    }

    public AssetDescriptor Asset { get; }
    public string Path { get; }
    public ulong Revision { get; private set; } = 1;
    public ulong SavepointRevision { get; private set; } = 1;
    public bool IsReadOnly => Asset.ReadOnly;
    public bool IsDirty => Text != SavedText;
    public bool CanUndo => UndoStates.Count != 0;
    public bool CanRedo => RedoStates.Count != 0;
    public string Source => Text;
    public IReadOnlyList<AssetDocumentDiagnostic> Diagnostics { get; private set; }

    public bool Replace(string Value)
    {
        if (IsReadOnly || Value == Text)
        {
            return false;
        }

        UndoStates.Push(Text);
        RedoStates.Clear();
        Text = Value;
        Revision++;
        Diagnostics = Validate(Text);
        return true;
    }

    public bool Undo()
    {
        if (UndoStates.Count == 0)
        {
            return false;
        }

        RedoStates.Push(Text);
        Text = UndoStates.Pop();
        Revision++;
        Diagnostics = Validate(Text);
        return true;
    }

    public bool Redo()
    {
        if (RedoStates.Count == 0)
        {
            return false;
        }

        UndoStates.Push(Text);
        Text = RedoStates.Pop();
        Revision++;
        Diagnostics = Validate(Text);
        return true;
    }

    public void Save()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("This asset is read only");
        }
        if (Diagnostics.Any(Diagnostic => Diagnostic.Severity == "error"))
        {
            throw new InvalidDataException("Resolve asset diagnostics before saving");
        }
        if (!string.Equals(SourceHash, HashSource(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Asset source changed outside this editor; reopen before saving");
        }

        var TemporaryPath = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(TemporaryPath, Text, new UTF8Encoding(false));
            File.Move(TemporaryPath, Path, true);
        }
        finally
        {
            if (File.Exists(TemporaryPath))
            {
                File.Delete(TemporaryPath);
            }
        }

        SavedText = Text;
        SourceHash = HashSource();
        SavepointRevision = Revision;
    }

    private string HashSource() => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(Path)));
}
