using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

internal sealed class StyleSheetAssetEditorInstance : IAssetEditorInstance
{
    private const int SourcePageSize = 12;
    private readonly EditorAssetOpenContext Context;
    private readonly TextAssetAuthoringDocument Document;
    private int SourcePage;

    public StyleSheetAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        Document = new TextAssetAuthoringDocument(Context.Asset, Context.Path,
            ValidateStyleSheet);
    }

    public AssetDescriptor Asset => Context.Asset;
    public string EditorId => "stylesheet-editor";
    public string Title => Asset.Name;
    public string PrimaryPaneId => "source";
    public bool IsDirty => Document.IsDirty;
    public bool IsReadOnly => Document.IsReadOnly;
    public IReadOnlyList<AssetEditorPaneDefinition> Panes { get; } =
    [
        new("source", "Source", false),
        new("preview", "Preview"),
        new("details", "Details"),
        new("diagnostics", "Diagnostics"),
    ];
    public IReadOnlyList<AssetEditorCommandDefinition> Commands { get; } =
    [
        new("add-line", "Add Line", "Edit", 50, "add", true),
        new("previous-page", "Previous Source Page", "View", 50, null, true),
        new("next-page", "Next Source Page", "View", 60, null, true),
        new("undo", "Undo", "Edit", 100, "reset", true),
        new("redo", "Redo", "Edit", 110, "reset", true),
    ];

    public EditorLayoutDocument CreateDefaultLayout() =>
        AssetEditorInstanceUtilities.CreateDocumentLayout("source", "details",
            "preview", "diagnostics");

    public bool CanExecuteCommand(string CommandId)
    {
        return CommandId switch
        {
            "add-line" => !IsReadOnly,
            "previous-page" => SourcePage > 0,
            "next-page" => (SourcePage + 1) * SourcePageSize < SourceLines().Length,
            "undo" => !IsReadOnly && Document.CanUndo,
            "redo" => !IsReadOnly && Document.CanRedo,
            _ => false,
        };
    }

    public void ExecuteCommand(string CommandId)
    {
        if (CommandId == "add-line")
        {
            Document.Replace(Document.Source.TrimEnd('\r', '\n') + "\n");
            SourcePage = (SourceLines().Length - 1) / SourcePageSize;
        }
        else if (CommandId == "previous-page" && SourcePage > 0)
        {
            SourcePage--;
        }
        else if (CommandId == "next-page" &&
            (SourcePage + 1) * SourcePageSize < SourceLines().Length)
        {
            SourcePage++;
        }
        else if (CommandId == "undo")
        {
            Document.Undo();
        }
        else if (CommandId == "redo")
        {
            Document.Redo();
        }
    }

    public bool TryHandleAction(string Action)
    {
        const string LinePrefix = "editor.stylesheet-line:";
        if (Action.StartsWith(LinePrefix, StringComparison.Ordinal))
        {
            var Payload = Action[LinePrefix.Length..];
            var Separator = Payload.IndexOf(':');
            if (Separator > 0 && int.TryParse(Payload[..Separator], out var Index))
            {
                ReplaceLine(Index, Payload[(Separator + 1)..]);
            }
            return true;
        }
        const string RemovePrefix = "editor.stylesheet-line-remove:";
        if (Action.StartsWith(RemovePrefix, StringComparison.Ordinal) &&
            int.TryParse(Action[RemovePrefix.Length..], out var RemoveIndex))
        {
            RemoveLine(RemoveIndex);
            return true;
        }
        if (Action != "editor.stylesheet-source")
        {
            return false;
        }
        return true;
    }

    public string BuildPaneMarkup(string PaneId)
    {
        return PaneId switch
        {
            "source" => BuildSourceMarkup(),
            "preview" => BuildPreviewMarkup(),
            "details" => BuildDetailsMarkup(),
            "diagnostics" => AssetEditorInstanceUtilities.Diagnostics(Document.Diagnostics),
            _ => string.Empty,
        };
    }

    public void Save()
    {
        Document.Save();
    }

    public void Update(float DeltaSeconds)
    {
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["assetId"] = Asset.Id,
            ["editorRootId"] = Context.EditorRootId,
            ["dirty"] = IsDirty,
            ["readOnly"] = IsReadOnly,
            ["documentRevision"] = Document.Revision,
            ["savepointRevision"] = Document.SavepointRevision,
            ["diagnosticCount"] = Document.Diagnostics.Count,
            ["referencingDocuments"] = new JsonArray(ReferencingDocuments()
                .Select(Value => (JsonNode)Value.Id).ToArray()),
        };
    }

    public void Dispose()
    {
    }

    private string BuildSourceMarkup()
    {
        var Lines = SourceLines();
        var PageCount = Math.Max(1, (int)Math.Ceiling(
            Lines.Length / (double)SourcePageSize));
        SourcePage = Math.Clamp(SourcePage, 0, PageCount - 1);
        var Start = SourcePage * SourcePageSize;
        var End = Math.Min(Lines.Length, Start + SourcePageSize);
        if (IsReadOnly)
        {
            var ReadOnlyMarkup = new StringBuilder("<div class=\"asset-source-view\">")
                .Append("<div class=\"asset-source-page\">Source Page ")
                .Append(SourcePage + 1).Append(" / ").Append(PageCount)
                .Append(" - Lines ").Append(Start + 1).Append('-').Append(End)
                .Append(" of ").Append(Lines.Length).Append("</div>");
            for (var Index = Start; Index < End; Index++)
            {
                var DisplayLine = Lines[Index].Length <= 180 ? Lines[Index] :
                    Lines[Index][..180] + " ...";
                ReadOnlyMarkup.Append("<div class=\"asset-source-page\">")
                    .Append((Index + 1).ToString().PadLeft(4)).Append("  ")
                    .Append(AssetEditorInstanceUtilities.Escape(DisplayLine))
                    .Append("</div>");
            }
            return ReadOnlyMarkup.Append("</div>").ToString();
        }
        var Markup = new StringBuilder(
            "<div class=\"asset-inspector asset-source-form\">");
        Markup.Append(AssetEditorInstanceUtilities.ReadOnlyField("Source Page",
            $"{SourcePage + 1} / {PageCount} - Lines {Start + 1}-{End} of {Lines.Length}"));
        for (var Index = Start; Index < End; Index++)
        {
            Markup.Append("<div class=\"asset-source-row\">")
                .Append(AssetEditorInstanceUtilities.Field("Line " + (Index + 1),
                    Lines[Index], $"editor.stylesheet-line:{Index}:", false))
                .Append("<button class=\"asset-source-remove\" data-action=\"editor.stylesheet-line-remove:")
                .Append(Index).Append("\"")
                .Append(" title=\"Remove line\">Remove</button></div>");
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }

    private string[] SourceLines() => Document.Source.Replace("\r\n", "\n",
        StringComparison.Ordinal).Split('\n');

    private void ReplaceLine(int Index, string Value)
    {
        var Lines = SourceLines();
        if (Index < 0 || Index >= Lines.Length)
        {
            return;
        }
        Lines[Index] = Value;
        Document.Replace(string.Join('\n', Lines));
    }

    private void RemoveLine(int Index)
    {
        var Lines = SourceLines().ToList();
        if (Index < 0 || Index >= Lines.Count)
        {
            return;
        }
        Lines.RemoveAt(Index);
        Document.Replace(string.Join('\n', Lines));
        SourcePage = Math.Min(SourcePage,
            Math.Max(0, (SourceLines().Length - 1) / SourcePageSize));
    }

    private string BuildDetailsMarkup()
    {
        var Rules = ParseRules(Document.Source).ToArray();
        var References = ReferencingDocuments();
        var Markup = new StringBuilder("<div class=\"asset-inspector\">")
            .Append(AssetEditorInstanceUtilities.ReadOnlyField("Asset ID", Asset.Id))
            .Append(AssetEditorInstanceUtilities.ReadOnlyField("Rules",
                Rules.Length.ToString()))
            .Append(AssetEditorInstanceUtilities.ReadOnlyField("Referenced by",
                References.Count.ToString()));
        foreach (var Reference in References)
        {
            Markup.Append("<div class=\"asset-reference-row\"><strong>")
                .Append(AssetEditorInstanceUtilities.Escape(Reference.Name))
                .Append("</strong><span>")
                .Append(AssetEditorInstanceUtilities.Escape(Reference.Id))
                .Append("</span></div>");
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }

    private string BuildPreviewMarkup()
    {
        var Markup = new StringBuilder("<div class=\"stylesheet-preview\">");
        var Rules = ParseRules(Document.Source);
        foreach (var Rule in Rules.Take(12))
        {
            var Background = Rule.Declarations.TryGetValue("background-color", out var Value) ?
                Value : "#252a32";
            var Color = Rule.Declarations.TryGetValue("color", out Value) ?
                Value : "#d4dae4";
            var Border = Rule.Declarations.TryGetValue("border-color", out Value) ?
                Value : "#424954";
            Markup.Append("<div class=\"stylesheet-rule-preview\" style=\"background-color:")
                .Append(AssetEditorInstanceUtilities.EscapeAttribute(Background))
                .Append(";color:").Append(AssetEditorInstanceUtilities.EscapeAttribute(Color))
                .Append(";border-color:")
                .Append(AssetEditorInstanceUtilities.EscapeAttribute(Border))
                .Append(";\"><strong>")
                .Append(AssetEditorInstanceUtilities.Escape(Rule.Selector))
                .Append("</strong><span>")
                .Append(AssetEditorInstanceUtilities.Escape(string.Join("; ",
                    Rule.Declarations.Select(Pair => $"{Pair.Key}: {Pair.Value}"))))
                .Append("</span></div>");
        }
        if (Rules.Count > 12)
        {
            Markup.Append("<div class=\"asset-source-truncated\">")
                .Append(Rules.Count - 12)
                .Append(" additional rules are omitted from Preview</div>");
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }

    private IReadOnlyList<AssetDescriptor> ReferencingDocuments()
    {
        var Assets = Context.Runtime.Assets;
        if (Assets is null)
        {
            return [];
        }

        var FileName = Path.GetFileName(Context.Path);
        return Assets.Assets.Where(Candidate =>
        {
            if (!Candidate.Type.Equals("RmlDocument", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var PathValue = Assets.ResolvePath(Candidate);
            if (PathValue is null || !File.Exists(PathValue))
            {
                return false;
            }
            return File.ReadAllText(PathValue).Contains(FileName,
                StringComparison.OrdinalIgnoreCase);
        }).ToArray();
    }

    private static IReadOnlyList<AssetDocumentDiagnostic> ValidateStyleSheet(string Source)
    {
        var Diagnostics = new List<AssetDocumentDiagnostic>();
        var OpenBraces = Source.Count(Character => Character == '{');
        var CloseBraces = Source.Count(Character => Character == '}');
        if (OpenBraces != CloseBraces)
        {
            Diagnostics.Add(new("error", "STYLESHEET_BRACE_MISMATCH",
                "Opening and closing brace counts do not match"));
            return Diagnostics;
        }

        foreach (var Rule in ParseRules(Source))
        {
            if (string.IsNullOrWhiteSpace(Rule.Selector))
            {
                Diagnostics.Add(new("error", "STYLESHEET_SELECTOR_REQUIRED",
                    "A style rule has no selector"));
            }
            foreach (var Declaration in Rule.Declarations)
            {
                if (Declaration.Key.Length == 0 || Declaration.Value.Length == 0)
                {
                    Diagnostics.Add(new("error", "STYLESHEET_DECLARATION_INVALID",
                        $"Rule '{Rule.Selector}' contains an invalid declaration"));
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(Source) && ParseRules(Source).Count == 0)
        {
            Diagnostics.Add(new("error", "STYLESHEET_RULE_INVALID",
                "No complete style rule could be parsed"));
        }
        return Diagnostics;
    }

    private static IReadOnlyList<StyleRule> ParseRules(string Source)
    {
        var Clean = Regex.Replace(Source, @"/\*.*?\*/", string.Empty,
            RegexOptions.Singleline);
        var Rules = new List<StyleRule>();
        foreach (Match Match in Regex.Matches(Clean,
            @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}"))
        {
            var Declarations = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var Entry in Match.Groups["body"].Value.Split(';',
                StringSplitOptions.RemoveEmptyEntries))
            {
                var NormalizedEntry = Entry.Trim();
                if (NormalizedEntry.Length == 0)
                {
                    continue;
                }
                var Separator = NormalizedEntry.IndexOf(':');
                if (Separator <= 0)
                {
                    Declarations[string.Empty] = NormalizedEntry;
                    continue;
                }
                Declarations[NormalizedEntry[..Separator].Trim()] =
                    NormalizedEntry[(Separator + 1)..].Trim();
            }
            foreach (var Selector in Match.Groups["selector"].Value.Split(','))
            {
                Rules.Add(new StyleRule(Selector.Trim(), Declarations));
            }
        }
        return Rules;
    }

    private sealed record StyleRule(string Selector,
        IReadOnlyDictionary<string, string> Declarations);
}
