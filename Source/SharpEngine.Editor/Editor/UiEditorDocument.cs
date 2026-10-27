using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpEngine.Assets;

namespace SharpEngine.Editor;

internal sealed record UiEditorDiagnostic(string Severity, string Code, string Message);

/// <summary>Source shape of an edited UI document. A package stores the RML inside
/// <c>.sui</c> JSON; a legacy RML asset stores the XML itself.</summary>
internal enum UiDocumentFormat
{
    RmlFile,
    Package,
}

internal sealed record UiEditorSnapshot(string Xml, string? Selection, string Rcss,
    IReadOnlyList<string> GlobalStyleSheets);

internal sealed record UiEditorGestureBase(bool Absolute, float Left, float Top,
    float Width, float Height, float MarginLeft, float MarginTop,
    IReadOnlyDictionary<string, UiLayoutLength>? Lengths = null, string ParentMode = "custom",
    string WidthMode = "fixed", string HeightMode = "fixed", string AlignmentX = "", string AlignmentY = "");

internal sealed class UiEditorGesture
{
    public UiEditorGesture(string Name, string Xml, string? Selection,
        UiEditorGestureBase Base, string? OriginalStyle)
    {
        this.Name = Name;
        this.Xml = Xml;
        this.Selection = Selection;
        this.Base = Base;
        this.Handle = string.Empty;
        this.OriginalStyle = OriginalStyle;
    }

    public string Name { get; }
    public string Xml { get; }
    public string? Selection { get; }
    public UiEditorGestureBase Base { get; }
    public string Handle { get; set; }
    public bool Changed { get; set; }
    public string? OriginalStyle
    {
        get;
    }
}

/// <summary>Authoritative authoring model for one RML document. XML source is the
/// authoritative representation; selection, resolved styles, diagnostics and the
/// active drag transaction are derived or transient state of this editor instance.</summary>
internal sealed partial class UiEditorDocument
{
    public const int DesignWidth = 1920;
    public const int DesignHeight = 1080;

    private static readonly Regex IdPattern = new("^[A-Za-z_][A-Za-z0-9._-]*$",
        RegexOptions.CultureInvariant);
    /// <summary>Tags whose content is text only. <c>button</c> and <c>input</c> are leaf
    /// controls for nesting purposes but may legitimately contain icons, so they are not
    /// reported for holding children.</summary>
    private static readonly HashSet<string> TextOnlyTags = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "span", "label", "p", "h1", "h2", "h3", "h4", "h5", "h6",
    };
    private static readonly HashSet<string> LeafTags = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "span", "label", "p", "h1", "h2", "h3", "h4", "h5", "h6", "button", "input",
        "img", "text", "progress", "br", "hr",
    };
    /// <summary>Inline content tags. RmlUi lays <c>br</c> out as a forced line break, so a
    /// text tag holding one is ordinary markup rather than a nesting mistake.</summary>
    private static readonly HashSet<string> InlineContentTags = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "br", "hr", "img", "span", "label", "a", "em", "strong", "small", "code", "b", "i",
    };
    private static readonly HashSet<string> ContainerTags = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "div", "panel", "section", "ul", "ol", "li", "form",
    };
    /// <summary>Standard markup tags RmlUi instances through its <c>*</c> fallback and
    /// lays out normally. They are not RML-specific controls, but warning about them is
    /// noise: <c>pre</c>, <c>br</c> and friends are ordinary authoring vocabulary.</summary>
    private static readonly HashSet<string> StandardContentTags = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "pre", "code", "blockquote", "a", "em", "strong", "small", "b", "i", "u", "s",
        "del", "ins", "sup", "sub", "dl", "dt", "dd", "table", "thead", "tbody", "tr",
        "td", "th", "header", "footer", "nav", "main", "aside", "article", "figure",
        "figcaption", "hgroup", "br", "hr", "img", "progress",
    };
    /// <summary>Composition directives are valid template content even though RmlUi never
    /// sees them: they are resolved before the document is presented.</summary>
    private static readonly HashSet<string> DirectiveTags = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "ui-slot", "ui-if", "ui-repeat", "ui-use",
    };

    private readonly string OwnerRoot;
    private readonly Stack<UiEditorSnapshot> UndoStack = [];
    private readonly Stack<UiEditorSnapshot> RedoStack = [];
    private XDocument Document;
    private string SavedState;
    private string SourceHash;
    private string? SelectionKey;
    private UiStyleSheet StyleSheet = UiStyleSheet.Empty;
    private List<UiEditorDiagnostic> DiagnosticList = [];
    private UiEditorGesture? Gesture;

    private UiEditorDocument(AssetDescriptor Asset, string Path, string OwnerRoot,
        XDocument Document, UiDocumentFormat Format, string PackageName,
        IReadOnlyList<string> StyleSheets, string Rcss = "",
        IReadOnlyList<string>? GlobalStyleSheets = null)
    {
        this.Asset = Asset;
        this.Path = Path;
        this.OwnerRoot = System.IO.Path.GetFullPath(OwnerRoot);
        this.Document = Document;
        this.Format = Format;
        this.PackageName = PackageName;
        this.PackageStyleSheets = StyleSheets;
        PrivateRcss = Rcss;
        this.GlobalStyleSheets = GlobalStyleSheets?.ToArray() ?? [];
        this.IsTemplate = Document.Root?.Name.LocalName == "template";
        this.SavedState = AuthoringState();
        this.SourceHash = HashSource();
        var First = EnumerateBody().FirstOrDefault();
        this.SelectionKey = First is null ? null : KeyOf(First);
        RefreshDerivedState();
    }

    public AssetDescriptor Asset { get; }
    public string Path { get; }
    public UiDocumentFormat Format { get; }
    /// <summary>Package name for a <c>.sui</c> asset; the asset name for a bare RML file.</summary>
    public string PackageName { get; }
    /// <summary>Stylesheets declared by a <c>.sui</c> package rather than by a link.</summary>
    public IReadOnlyList<string> PackageStyleSheets { get; }
    internal string PrivateRcss
    {
        get;
        private set;
    }
    internal IReadOnlyList<string> GlobalStyleSheets
    {
        get;
        private set;
    }
    /// <summary>True when this asset is a reusable <c>&lt;template&gt;</c> definition. The
    /// editor presents its content with placeholder values; it has no screen size or body.</summary>
    public bool IsTemplate { get; }
    public string? SelectedKey => SelectionKey;
    public long Revision { get; private set; }
    public bool IsReadOnly => Asset.ReadOnly || Asset.Source == AssetSource.Engine;
    public bool IsDirty => AuthoringState() != SavedState;
    public bool CanUndo => UndoStack.Count != 0;
    public bool CanRedo => RedoStack.Count != 0;
    public bool IsGesturing => Gesture is not null;
    public IReadOnlyList<UiEditorDiagnostic> Diagnostics => DiagnosticList;
    public string Title => Asset.Name + (IsDirty ? "*" : string.Empty);
    public string SelectedTag => SelectedElement()?.Name.LocalName ?? string.Empty;
    public string ViewModelContract =>
        ((string?)Document.Root?.Attribute("data-view-model") ?? string.Empty).Trim();
    public int ElementCount => EnumerateBody().Count();
    public string ReadOnlyMessage => "Engine-owned UI documents are read only";

    internal XDocument Source => Document;
    internal UiStyleSheet Sheet => StyleSheet;
    internal string CanonicalSource => Canonical(Document);

    public static UiEditorDocument Load(AssetDescriptor Asset, string Path,
        string OwnerRoot)
    {
        ArgumentNullException.ThrowIfNull(Asset);
        if (!File.Exists(Path))
        {
            throw new FileNotFoundException("UI document was not found", Path);
        }
        var Full = System.IO.Path.GetFullPath(Path);
        if (!IsPackage(Asset, Full))
        {
            var Rml = XDocument.Load(Full,
                LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            return new UiEditorDocument(Asset, Full, OwnerRoot, Rml,
                UiDocumentFormat.RmlFile, Asset.Name, []);
        }
        var Package = UiDocumentAsset.Load(Full);
        var Document = XDocument.Parse(Package.Rml,
            LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        return new UiEditorDocument(Asset, Full, OwnerRoot, Document,
            UiDocumentFormat.Package, Package.Name, Package.StyleSheets, Package.Rcss, Package.GlobalStyleSheets);
    }

    /// <summary>A package is recognized by its asset type or, for unregistered sources,
    /// by the suffix. A bare RML file keeps the earlier single-document behavior.</summary>
    private static bool IsPackage(AssetDescriptor Asset, string Path) =>
        Asset.Type.Equals("UiDocument", StringComparison.OrdinalIgnoreCase) ||
        Path.EndsWith(UiDocumentAsset.FileSuffix, StringComparison.OrdinalIgnoreCase);

    public bool Select(string Key)
    {
        var Resolved = ResolveKey(Key);
        if (Resolved is null)
        {
            return false;
        }
        SelectionKey = KeyOf(Resolved);
        RevealInTree(Resolved);
        return true;
    }

    /// <summary>Accepts a full selection key, a bare element id, or a body path key so
    /// restored editor state and external automation can address elements either way.</summary>
    private XElement? ResolveKey(string Key)
    {
        var Trimmed = (Key ?? string.Empty).Trim();
        if (Trimmed.Length == 0)
        {
            return null;
        }
        var Direct = Find(Trimmed);
        if (Direct is not null)
        {
            return Direct;
        }
        if (Trimmed.StartsWith('#'))
        {
            return Find(Trimmed[1..]);
        }
        return Find("#" + Trimmed);
    }

    public bool SelectParent()
    {
        var Element = SelectedElement();
        var Parent = Element?.Parent;
        if (Parent is null || Parent.Name.LocalName is "body" or "rml")
        {
            return false;
        }
        SelectionKey = KeyOf(Parent);
        return true;
    }

    public bool SelectSibling(int Delta)
    {
        var Element = SelectedElement();
        var Parent = Element?.Parent;
        if (Element is null || Parent is null)
        {
            return false;
        }
        var Siblings = Parent.Elements().ToArray();
        var Index = Array.IndexOf(Siblings, Element) + Delta;
        if (Index < 0 || Index >= Siblings.Length)
        {
            return false;
        }
        SelectionKey = KeyOf(Siblings[Index]);
        return true;
    }

    public bool SetViewModel(string Value, out string Message)
    {
        Message = string.Empty;
        return Edit("view model", () =>
        {
            var Root = Document.Root;
            if (Root is null)
            {
                return;
            }
            SetOrRemove(Root, "data-view-model", Value);
        }, out Message);
    }

    public bool SetAttribute(string Name, string Value, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element before editing attributes";
            return false;
        }
        switch (Name)
        {
            case "id":
                return RenameId(Value, out Message);
            case "class":
            case "data-bind":
            case "data-command":
            case "data-ui-name":
                return Edit("attribute", () => SetOrRemove(Element, Name, Value),
                    out Message);
            default:
                Message = $"Unknown UI attribute '{Name}'";
                return false;
        }
    }

    public bool AddClass(string ClassName, out string Message)
    {
        Message = string.Empty;
        return ChangeClass(ClassName, true, out Message);
    }

    public bool RemoveClass(string ClassName, out string Message)
    {
        Message = string.Empty;
        return ChangeClass(ClassName, false, out Message);
    }

    public bool ToggleClass(string ClassName, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element before editing classes";
            return false;
        }
        var Present = UiStyleText.ClassList(Element).Contains(ClassName);
        return ChangeClass(ClassName, !Present, out Message);
    }

    private bool ChangeClass(string ClassName, bool Add, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element before editing classes";
            return false;
        }
        var Trimmed = ClassName.Trim();
        if (!UiStyleProperties.ValidClassName(Trimmed, out Message))
        {
            return false;
        }
        var Classes = UiStyleText.ClassList(Element);
        if (Add ? !Classes.Add(Trimmed) : !Classes.Remove(Trimmed))
        {
            return false;
        }
        var Value = string.Join(' ', Classes.OrderBy(Name => Name, StringComparer.Ordinal));
        return Edit("class", () => SetOrRemove(Element, "class", Value), out Message);
    }

    public bool SetText(string Value, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element before editing text";
            return false;
        }
        if (Element.HasElements)
        {
            Message = "Container text cannot replace child elements";
            return false;
        }
        if (Element.Name.LocalName == "ui-use")
        {
            Message = "Edit the reusable control's parameters or open its shared definition";
            return false;
        }
        return Edit("text", () => Element.Value = Value, out Message);
    }

    public bool SetStyle(string Name, string Value, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element before editing styles";
            return false;
        }
        var Trimmed = Value.Trim();
        if (Trimmed.Length == 0)
        {
            return RemoveStyle(Name, out Message);
        }
        if (!UiStyleProperties.Validate(Name, Trimmed, out Message))
        {
            return false;
        }
        return Edit("style:" + Name, () => WriteDeclaration(Element, Name, Trimmed),
            out Message);
    }

    public bool RemoveStyle(string Name, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element before editing styles";
            return false;
        }
        return Edit("style:" + Name, () => WriteDeclaration(Element, Name, null),
            out Message);
    }

    public bool SetPositionMode(bool Absolute, int RectX, int RectY, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element before changing layout mode";
            return false;
        }
        return Edit(Absolute ? "absolute layout" : "flex layout", () =>
        {
            if (!Absolute)
            {
                WriteDeclaration(Element, "position", null);
                WriteDeclaration(Element, "left", null);
                WriteDeclaration(Element, "top", null);
                WriteDeclaration(Element, "right", null);
                WriteDeclaration(Element, "bottom", null);
                return;
            }
            WriteDeclaration(Element, "position", "absolute");
            WriteDeclaration(Element, "left", FormatLength(RectX));
            WriteDeclaration(Element, "top", FormatLength(RectY));
        }, out Message);
    }

    public bool AddElement(string Tag, out string Message)
    {
        var ControlId = Tag.Trim().ToLowerInvariant() switch
        {
            "div" => "container",
            "span" => "text",
            "input" => "text-input",
            "img" => "image",
            var Id => Id,
        };
        return AddControl(ControlId, out Message);
    }

    public bool DuplicateSelected(out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element before duplicating";
            return false;
        }
        if (Element.Parent is null)
        {
            return false;
        }
        var Clone = new XElement(Element);
        var UsedIds = EnumerateBody().Select(ElementId).Where(Value => Value.Length != 0)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var Candidate in Clone.DescendantsAndSelf())
        {
            var Id = ElementId(Candidate);
            if (Id.Length == 0)
            {
                continue;
            }
            var Unique = UniqueId(Id, UsedIds);
            UsedIds.Add(Unique);
            Candidate.SetAttributeValue("id", Unique);
        }
        return Edit("duplicate", () =>
        {
            Element.AddAfterSelf(Clone);
            SelectionKey = KeyOf(Clone);
        }, out Message);
    }

    public bool DeleteSelected(out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element before deleting";
            return false;
        }
        var Parent = Element.Parent;
        if (Parent is null)
        {
            Message = "The document root cannot be deleted";
            return false;
        }
        var Siblings = Parent.Elements().ToArray();
        var Index = Array.IndexOf(Siblings, Element);
        var Next = Siblings.Length > 1
            ? Siblings[Index == Siblings.Length - 1 ? Index - 1 : Index + 1]
            : null;
        return Edit("delete", () =>
        {
            Element.Remove();
            SelectionKey = Next is null ? null : KeyOf(Next);
        }, out Message);
    }

    public bool MoveSelected(int Delta, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        var Parent = Element?.Parent;
        if (Element is null || Parent is null)
        {
            Message = "Select an element before moving it";
            return false;
        }
        var Siblings = Parent.Elements().ToList();
        var Index = Siblings.IndexOf(Element);
        var Target = Index + Delta;
        if (Target < 0 || Target >= Siblings.Count)
        {
            Message = Delta < 0 ? "The element is already first" : "The element is already last";
            return false;
        }
        var Anchor = Siblings[Target];
        return Edit("move", () =>
        {
            Element.Remove();
            if (Delta < 0)
            {
                Anchor.AddBeforeSelf(Element);
            }
            else
            {
                Anchor.AddAfterSelf(Element);
            }
        }, out Message);
    }

    public bool IndentSelected(out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element before indenting it";
            return false;
        }
        var Parent = Element.Parent;
        var Previous = Element.ElementsBeforeSelf().LastOrDefault();
        if (Previous is null || !CanContain(Previous))
        {
            Message = Parent is null || Parent.Name.LocalName == "body"
                ? "The element has no previous sibling container"
                : "The previous sibling cannot contain elements";
            return false;
        }
        return Edit("indent", () =>
        {
            Element.Remove();
            Previous.Add(Element);
        }, out Message);
    }

    public bool OutdentSelected(out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        var Parent = Element?.Parent;
        if (Element is null || Parent is null)
        {
            Message = "Select an element before outdenting it";
            return false;
        }
        if (Parent.Name.LocalName == "body" || Parent.Parent is null ||
            Parent.Parent.Name.LocalName == "rml")
        {
            Message = "The element is already a screen-level element";
            return false;
        }
        return Edit("outdent", () =>
        {
            Element.Remove();
            Parent.AddAfterSelf(Element);
        }, out Message);
    }

    public bool RenameId(string NewId, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element before renaming it";
            return false;
        }
        var Trimmed = NewId.Trim();
        if (Trimmed.Length == 0)
        {
            return Edit("id", () =>
            {
                Element.SetAttributeValue("id", null);
                SelectionKey = KeyOf(Element);
            }, out Message);
        }
        if (!IdPattern.IsMatch(Trimmed))
        {
            Message = "Element ids must start with a letter or '_' and contain only " +
                "letters, digits, '.', '_' or '-'";
            return false;
        }
        var Duplicate = EnumerateBody().Any(Candidate => Candidate != Element &&
            IdPattern.IsMatch(Trimmed) &&
            string.Equals(ElementId(Candidate), Trimmed, StringComparison.Ordinal));
        if (Duplicate)
        {
            Message = $"Element id '{Trimmed}' is already used in this document";
            return false;
        }
        if (string.Equals(ElementId(Element), Trimmed, StringComparison.Ordinal))
        {
            return false;
        }
        return Edit("id", () =>
        {
            Element.SetAttributeValue("id", Trimmed);
            SelectionKey = "#" + Trimmed;
        }, out Message);
    }

    public void Undo()
    {
        if (Gesture is not null)
        {
            CommitGesture();
        }
        if (UndoStack.Count == 0)
        {
            return;
        }
        RedoStack.Push(CaptureSnapshot());
        Restore(UndoStack.Pop());
    }

    public void Redo()
    {
        if (Gesture is not null)
        {
            CommitGesture();
        }
        if (RedoStack.Count == 0)
        {
            return;
        }
        UndoStack.Push(CaptureSnapshot());
        Restore(RedoStack.Pop());
    }

    public bool BeginGesture(string Name, int RectX, int RectY, int RectWidth,
        int RectHeight, out string Message, UiLayoutMetrics? Metrics = null)
    {
        Message = string.Empty;
        if (IsReadOnly)
        {
            Message = ReadOnlyMessage;
            return false;
        }
        if (Gesture is not null)
        {
            CommitGesture();
        }
        var Element = SelectedElement();
        if (Element is null)
        {
            Message = "Select an element before dragging it";
            return false;
        }
        if (!TryBuildGestureBase(Element, RectX, RectY, RectWidth, RectHeight,
                out var Base, out Message, Metrics))
        {
            return false;
        }
        Gesture = new UiEditorGesture(Name, Canonical(Document), SelectedKey, Base, (string?)Element.Attribute("style"));
        return true;
    }

    public bool ApplyGestureMove(string Handle, int DeltaX, int DeltaY,
        bool SnapGrid, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Gesture is null || Element is null || Handle != "move")
        {
            Message = "No active move gesture";
            return false;
        }
        var Changes = new Dictionary<string, string>(StringComparer.Ordinal);
        BuildMoveAxis(Changes, Gesture.Base, true, Snap(DeltaX, SnapGrid));
        BuildMoveAxis(Changes, Gesture.Base, false, Snap(DeltaY, SnapGrid));
        return ApplyGestureChanges(Element, Handle, Changes, out Message);
    }

    public bool ApplyGestureResize(string Handle, int DeltaX, int DeltaY,
        bool SnapGrid, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Gesture is null || Element is null || !Handle.StartsWith("resize-", StringComparison.Ordinal))
        {
            Message = "No active resize gesture";
            return false;
        }
        var Changes = new Dictionary<string, string>(StringComparer.Ordinal);
        var Edges = Handle["resize-".Length..];
        var Horizontal = Edges.Contains('e') || Edges.Contains('w');
        var Vertical = Edges.Contains('n') || Edges.Contains('s');
        var Valid = !Horizontal || BuildResizeAxis(Changes, Gesture.Base, true,
            Edges.Contains('w'), Snap(DeltaX, SnapGrid), out Message);
        if (!Valid)
        {
            return false;
        }
        Valid = !Vertical || BuildResizeAxis(Changes, Gesture.Base, false,
            Edges.Contains('n'), Snap(DeltaY, SnapGrid), out Message);
        if (!Valid)
        {
            return false;
        }
        return ApplyGestureChanges(Element, Handle, Changes, out Message);
    }

    /// <summary>Inline declarations a live gesture is currently driving. The host sends
    /// these straight to the native element so the drag stays smooth without replacing
    /// the markup that owns the captured element.</summary>
    public IReadOnlyList<(string Name, string Value)> LiveStyleProperties()
    {
        var Element = SelectedElement();
        if (Element is null)
        {
            return Array.Empty<(string, string)>();
        }
        var Computed = Sheet.Compute(Element);
        var Result = new List<(string, string)>(6);
        foreach (var Name in new[]
                 {
                     "left", "top", "right", "bottom", "width", "height",
                     "margin-left", "margin-top", "margin-right", "margin-bottom",
                 })
        {
            var Value = Name.StartsWith("margin-", StringComparison.Ordinal)
                ? LayoutStyleValue(Element, Name) : Computed.GetValueOrDefault(Name)?.Value ?? "auto";
            Result.Add((Name, Value));
        }
        return Result;
    }

    public void CommitGesture()
    {
        if (Gesture is null)
        {
            return;
        }
        var Active = Gesture;
        Gesture = null;
        if (Active.Changed && Canonical(Document) != Active.Xml)
        {
            UndoStack.Push(new(Active.Xml, Active.Selection, PrivateRcss, GlobalStyleSheets.ToArray()));
            RedoStack.Clear();
        }
        RefreshDerivedState();
    }

    public void CancelGesture()
    {
        if (Gesture is null)
        {
            return;
        }
        var Active = Gesture;
        Gesture = null;
        Document = XDocument.Parse(Active.Xml,
            LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        SelectionKey = Active.Selection;
        Revision++;
        RefreshDerivedState();
    }

    public void Save()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException(
                "Engine-owned UI documents cannot be saved");
        }
        if (Gesture is not null)
        {
            CommitGesture();
        }
        RefreshDerivedState();
        if (Diagnostics.Any(Value => Value.Severity == "error"))
        {
            throw new InvalidDataException("Resolve UI document errors before saving");
        }
        if (!string.Equals(SourceHash, HashSource(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "UI source changed outside this editor; reopen before saving");
        }
        var DirectoryPath = System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
        Directory.CreateDirectory(DirectoryPath);
        var Temporary = System.IO.Path.Combine(DirectoryPath,
            "." + System.IO.Path.GetFileName(Path) + "." + Guid.NewGuid().ToString("N") +
            ".tmp");
        var Text = SerializeForSave();
        try
        {
            File.WriteAllText(Temporary, Text, new UTF8Encoding(false));
            // Re-read the exact bytes that will replace the source, so an invalid
            // document can never reach the authored file.
            VerifySerializedFile(Temporary);
            File.Move(Temporary, Path, true);
            SavedState = AuthoringState();
            SourceHash = HashSource();
        }
        finally
        {
            if (File.Exists(Temporary))
            {
                File.Delete(Temporary);
            }
        }
    }

    /// <summary>Current file text for this document. A package stores the RML inside
    /// JSON; a bare RML asset stores the XML itself.</summary>
    public string SerializeForSave()
    {
        if (Format == UiDocumentFormat.Package)
        {
            return UiDocumentAsset.Create(PackageName, Canonical(Document), PackageStyleSheets,
                    PrivateRcss, GlobalStyleSheets)
                .Serialize() + Environment.NewLine;
        }
        return Document.ToString() + Environment.NewLine;
    }

    private void VerifySerializedFile(string Temporary)
    {
        if (Format == UiDocumentFormat.Package)
        {
            var Reparsed = UiDocumentAsset.Load(Temporary);
            var Rml = XDocument.Parse(Reparsed.Rml,
                LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            if (Canonical(Rml) != Canonical(Document) || Reparsed.Rcss != PrivateRcss ||
                !Reparsed.GlobalStyleSheets.SequenceEqual(GlobalStyleSheets))
            {
                throw new InvalidDataException(
                    "The serialized UI package does not round-trip its RML document");
            }
            return;
        }
        _ = XDocument.Load(Temporary, LoadOptions.SetLineInfo);
    }

    public JsonObject Describe()
    {
        var Element = SelectedElement();
        var Elements = new JsonArray();
        foreach (var Candidate in EnumerateBody())
        {
            var Style = UiStyleText.ParseDeclarations(
                UiStyleText.Attribute(Candidate, "style"));
            var Attributes = new JsonObject();
            foreach (var Attribute in Candidate.Attributes())
            {
                var Name = Attribute.Name.LocalName;
                if (Name is "style" or "id")
                {
                    continue;
                }
                Attributes[Name] = Attribute.Value;
            }
            Elements.Add(new JsonObject
            {
                ["key"] = KeyOf(Candidate),
                ["elementId"] = ElementId(Candidate),
                ["tag"] = Candidate.Name.LocalName,
                ["displayName"] = DisplayName(Candidate),
                ["controlType"] = ControlOf(Candidate)?.Id ?? Candidate.Name.LocalName,
                ["controlLabel"] = ControlLabel(Candidate),
                ["expanded"] = TreeExpanded(Candidate),
                ["previewElementId"] = PreviewNodeId(Candidate),
                ["depth"] = DepthOf(Candidate),
                ["children"] = Candidate.Elements().Count(),
                ["attributes"] = Attributes,
                ["style"] = new JsonArray(Style.Select(Value =>
                    (JsonNode)new JsonObject
                    {
                        ["name"] = Value.Name,
                        ["value"] = Value.Value,
                        ["important"] = Value.Important,
                    }).ToArray()),
            });
        }
        var DocumentState = new JsonObject
        {
            ["schemaVersion"] = 3,
            ["assetId"] = Asset.Id,
            ["type"] = Asset.Type,
            ["format"] = Format == UiDocumentFormat.Package ? "package" : "rml",
            ["packageName"] = PackageName,
            ["path"] = Asset.RelativePath,
            ["source"] = Asset.Source.ToString(),
            ["readOnly"] = IsReadOnly,
            ["dirty"] = IsDirty,
            ["revision"] = Revision,
            ["canUndo"] = CanUndo,
            ["canRedo"] = CanRedo,
            ["gesturing"] = IsGesturing,
            ["selectedKey"] = SelectedKey,
            ["selectedPreviewElementId"] = Element is null ? null : PreviewNodeId(Element),
            ["viewModelContract"] = ViewModelContract,
            ["elementCount"] = Elements.Count,
            ["design"] = new JsonObject
            {
                ["width"] = PreviewWidth,
                ["height"] = PreviewHeight,
                ["zoom"] = PreviewZoom,
            },
            ["elements"] = Elements,
            ["tree"] = DescribeTree(),
            ["controls"] = new JsonArray(UiControlCatalog.All.Select(Control => (JsonNode)new JsonObject
            {
                ["id"] = Control.Id,
                ["label"] = Control.Label,
                ["group"] = Control.Group,
                ["properties"] = new JsonArray(Control.Properties.Select(Property => (JsonNode)new JsonObject
                {
                    ["name"] = Property.Name,
                    ["label"] = Property.Label,
                    ["kind"] = Property.Kind.ToString(),
                    ["defaultValue"] = Property.DefaultValue,
                }).ToArray()),
            }).ToArray()),
            ["styleSheet"] = new JsonObject
            {
                ["files"] = new JsonArray(StyleSheet.Files.Select(File =>
                    (JsonNode)File).ToArray()),
                ["inlineBlocks"] = StyleSheet.InlineBlocks,
                ["privateRcss"] = PrivateRcss,
                ["globalStyleSheets"] = new JsonArray(GlobalStyleSheets.Select(Value => (JsonNode)Value).ToArray()),
                ["ruleCount"] = StyleSheet.Rules.Count,
                ["classes"] = new JsonArray(StyleSheet.Classes.Select(Class =>
                    (JsonNode)Class).ToArray()),
                ["tags"] = new JsonArray(StyleSheet.Tags.Select(Tag =>
                    (JsonNode)Tag).ToArray()),
            },
            ["diagnostics"] = new JsonArray(Diagnostics.Select(Value =>
                (JsonNode)new JsonObject
                {
                    ["severity"] = Value.Severity,
                    ["code"] = Value.Code,
                    ["message"] = Value.Message,
                }).ToArray()),
        };
        if (Element is not null)
        {
            var Computed = StyleSheet.Compute(Element);
            DocumentState["selected"] = new JsonObject
            {
                ["key"] = KeyOf(Element),
                ["displayName"] = DisplayName(Element),
                ["controlProperties"] = DescribeControlProperties(Element),
                ["tag"] = Element.Name.LocalName,
                ["elementId"] = ElementId(Element),
                ["classes"] = new JsonArray(UiStyleText.ClassList(Element)
                    .OrderBy(Name => Name, StringComparer.Ordinal)
                    .Select(Name => (JsonNode)Name).ToArray()),
                ["text"] = Element.HasElements ? null : Element.Value,
                ["computed"] = new JsonObject(Computed.OrderBy(Pair => Pair.Key,
                        StringComparer.Ordinal)
                    .Select(Pair => new KeyValuePair<string, JsonNode?>(Pair.Key,
                        new JsonObject
                        {
                            ["value"] = Pair.Value.Value,
                            ["source"] = Pair.Value.Source,
                        })).ToArray()),
            };
        }
        return DocumentState;
    }

    private bool Edit(string Name, Action Mutation, out string Message)
    {
        Message = string.Empty;
        if (IsReadOnly)
        {
            Message = ReadOnlyMessage;
            return false;
        }
        if (Gesture is not null)
        {
            CommitGesture();
        }
        var Before = CaptureSnapshot();
        var BeforeState = AuthoringState();
        Mutation();
        if (AuthoringState() == BeforeState)
        {
            return false;
        }
        UndoStack.Push(Before);
        RedoStack.Clear();
        Revision++;
        RefreshDerivedState();
        return true;
    }

    private void Restore(UiEditorSnapshot Snapshot)
    {
        PrivateRcss = Snapshot.Rcss;
        GlobalStyleSheets = Snapshot.GlobalStyleSheets.ToArray();
        Document = XDocument.Parse(Snapshot.Xml,
            LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        SelectionKey = Snapshot.Selection;
        Revision++;
        RefreshDerivedState();
    }

    private void RefreshDerivedState()
    {
        ControlTypes.Clear();
        CollapsedTreeKeys.IntersectWith(EnumerateBody().Select(KeyOf));
        var Diagnostics = new List<UiEditorDiagnostic>();
        var Root = Document.Root;
        if (IsTemplate)
        {
            // A template is a reusable definition, not a screen: it has no <body> and no
            // fixed size. It is only reachable through a screen that binds its slots.
            if (Root is null || Root.Name.LocalName != "template")
            {
                Diagnostics.Add(new("error", "UI_TEMPLATE_ROOT",
                    "The template document root must be <template>"));
            }
            foreach (var Slot in Document.Descendants().Where(Element =>
                         Element.Name.LocalName == "ui-slot"))
            {
                if (Slot.Attribute("name") is null)
                {
                    Diagnostics.Add(new("error", "UI_TEMPLATE_SLOT_NAME",
                        "A <ui-slot> requires a non-empty 'name' attribute"));
                }
                if (Slot.HasElements)
                {
                    Diagnostics.Add(new("error", "UI_TEMPLATE_SLOT_CONTENT",
                        $"<ui-slot name=\"{Slot.Attribute("name")?.Value}\"> cannot contain " +
                        "elements; the bound part supplies them"));
                }
            }
        }
        else if (Root is null || Root.Name.LocalName != "rml")
        {
            Diagnostics.Add(new("error", "UI_RML_ROOT", "The document root must be <rml>"));
        }
        var BodyElement = Body();
        if (BodyElement is null && !IsTemplate)
        {
            Diagnostics.Add(new("error", "UI_RML_BODY", "The document requires one <body>"));
        }
        var Elements = EnumerateBody().ToArray();
        if (!IsTemplate && Elements.Any(Element => Element.Name.LocalName == "ui-use"))
        {
            Diagnostics.Add(new("error", "UI_USE_TEMPLATE_REQUIRED", "Fixed UI composition requires a <template> document"));
        }
        foreach (var Element in Elements.Where(Value => ElementId(Value).Length == 0 &&
                     !IsTemplate))
        {
            Diagnostics.Add(new("warning", "UI_ELEMENT_ID_MISSING",
                $"<{Element.Name.LocalName}> has no id; it is selected by its tree position"));
        }
        var Duplicate = Elements.Select(ElementId).Where(Value => Value.Length != 0)
            .GroupBy(Value => Value, StringComparer.Ordinal)
            .FirstOrDefault(Group => Group.Count() > 1);
        if (Duplicate is not null)
        {
            Diagnostics.Add(new("error", "UI_ELEMENT_ID_DUPLICATE",
                $"Element id '{Duplicate.Key}' occurs more than once"));
        }
        foreach (var Leaf in Elements.Where(Value => Value.HasElements &&
                     TextOnlyTags.Contains(Value.Name.LocalName) &&
                     Value.Elements().Any(Child => !InlineContentTags.Contains(
                         Child.Name.LocalName))))
        {
            Diagnostics.Add(new("warning", "UI_ELEMENT_LEAF_HAS_CHILDREN",
                $"<{Leaf.Name.LocalName}> is a text element but contains block children"));
        }
        foreach (var Unknown in Elements.Where(Value => !LeafTags.Contains(
                     Value.Name.LocalName) && !ContainerTags.Contains(
                     Value.Name.LocalName) && !StandardContentTags.Contains(
                     Value.Name.LocalName) && !DirectiveTags.Contains(
                     Value.Name.LocalName)))
        {
            Diagnostics.Add(new("warning", "UI_ELEMENT_TAG_UNKNOWN",
                $"<{Unknown.Name.LocalName}> is not a known RML layout element"));
        }
        BuildDesignComposition(Diagnostics);
        var GlobalFiles = ResolvedGlobalStyles(Diagnostics);
        var PrivateStyles = new[] { new SharpEngine.UI.UiPrivateStyle(Path, PrivateRcss) }.Concat(ChildPrivateStyles).ToArray();
        StyleSheet = UiStyleSheet.Load(Document, Path, OwnerRoot, Diagnostics,
            PackageStyleSheets, GlobalFiles.Concat(ReferencedStyleSheets).ToArray(), PrivateStyles);
        RefreshBasicControlStyles();
        foreach (var Candidate in Elements)
        {
            var Control = ControlOf(Candidate);
            if (Control is null)
            {
                continue;
            }
            var ValidControl = UiControlCatalog.ValidateNumbers(Candidate, Control.Id, out var ControlMessage);
            if (!ValidControl)
            {
                Diagnostics.Add(new("error", "UI_CONTROL_VALUE_INVALID", KeyOf(Candidate) + ": " + ControlMessage));
            }
        }
        TrackDependencies();
        DiagnosticList = Diagnostics;
        if (SelectedKey is not null && Find(SelectedKey) is null)
        {
            SelectionKey = Elements.Select(Element => KeyOf(Element))
                .FirstOrDefault(Value => Value.Length != 0);
        }
        if (SelectedKey is null && Elements.Length != 0)
        {
            SelectionKey = KeyOf(Elements[0]);
        }
        var Selected = SelectedElement();
        if (Selected is not null)
        {
            RevealInTree(Selected);
        }
    }

    private bool TryBuildGestureBase(XElement Element, int RectX, int RectY,
        int RectWidth, int RectHeight, out UiEditorGestureBase Base, out string Message,
        UiLayoutMetrics? Metrics = null)
    {
        Message = string.Empty;
        Metrics ??= new UiLayoutMetrics(RectX, RectY, RectWidth, RectHeight,
            PreviewWidth, PreviewHeight, PreviewWidth, PreviewHeight);
        if (HasCustomTransform(Element))
        {
            Base = new(false, 0, 0, 0, 0, 0, 0);
            Message = "This control has a custom transform; edit its source properties explicitly";
            return false;
        }
        var Style = Sheet.Compute(Element).ToDictionary(Pair => Pair.Key, Pair => Pair.Value.Value,
            StringComparer.OrdinalIgnoreCase);
        var Absolute = Style.GetValueOrDefault("position") is "absolute" or "fixed";
        var Lengths = new Dictionary<string, UiLayoutLength>(StringComparer.Ordinal);
        foreach (var Name in new[] { "left", "top", "right", "bottom", "width", "height",
            "margin-left", "margin-right", "margin-top", "margin-bottom" })
        {
            var Text = Style.GetValueOrDefault(Name);
            if (Text is null && Name.StartsWith("margin-", StringComparison.Ordinal))
            {
                var Parts = (Style.GetValueOrDefault("margin") ?? "0px").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var Index = Name switch
                {
                    "margin-top" => 0,
                    "margin-right" => Parts.Length > 1 ? 1 : 0,
                    "margin-bottom" => Parts.Length > 2 ? 2 : 0,
                    _ => Parts.Length > 3 ? 3 : Parts.Length > 1 ? 1 : 0,
                };
                Text = Parts.Length > Index ? Parts[Index] : "0px";
            }
            var Length = Text is null ? null : UiLayoutLength.Parse(Text, Name, Metrics);
            if (Length is not null)
            {
                Lengths[Name] = Length.Value;
            }
            else if (Text is not null && Text != "auto")
            {
                Base = new(false, 0, 0, 0, 0, 0, 0);
                Message = "Custom layout value requires explicit editing: " + Name + " = " + Text;
                return false;
            }
        }
        // Auto-sized controls become fixed only on the axis explicitly resized.
        foreach (var Name in new[] { "width", "height" })
        {
            if (!Lengths.ContainsKey(Name))
            {
                var ContentBox = Style.GetValueOrDefault("box-sizing") != "border-box";
                var Width = ContentBox && Metrics.ContentWidth > 0 ? Metrics.ContentWidth : Metrics.Width;
                var Height = ContentBox && Metrics.ContentHeight > 0 ? Metrics.ContentHeight : Metrics.Height;
                Lengths[Name] = new(Name == "width" ? Width : Height, "px", 1);
            }
        }
        Base = new(Absolute, Metrics.X, Metrics.Y, Metrics.Width, Metrics.Height,
            Lengths.GetValueOrDefault("margin-left").Pixels, Lengths.GetValueOrDefault("margin-top").Pixels,
            Lengths, SlotLayoutOf(Element), AxisMode(Element, true), AxisMode(Element, false),
            AlignmentOf(Element, true), AlignmentOf(Element, false));
        return true;
    }

    private static int Snap(int Value, bool SnapGrid)
    {
        if (!SnapGrid)
        {
            return Value;
        }
        var Grid = 10;
        return (int)Math.Round(Value / (double)Grid) * Grid;
    }

    private static string FormatLength(float Value) =>
        Math.Round(Value, 2).ToString("0.##", CultureInfo.InvariantCulture) + "px";

    private static string FormatLength(int Value) =>
        Value.ToString(CultureInfo.InvariantCulture) + "px";

    private string UniqueId(string Prefix)
    {
        var Ids = EnumerateBody().Select(ElementId).Where(Value => Value.Length != 0)
            .ToHashSet(StringComparer.Ordinal);
        return UniqueId(Prefix, Ids);
    }

    private static string UniqueId(string Prefix, HashSet<string> Used)
    {
        if (!Used.Contains(Prefix))
        {
            return Prefix;
        }
        for (var Index = 2; ; Index++)
        {
            var Candidate = Prefix + Index.ToString(CultureInfo.InvariantCulture);
            if (!Used.Contains(Candidate))
            {
                return Candidate;
            }
        }
    }

    private static bool CanContain(XElement Element) =>
        !LeafTags.Contains(Element.Name.LocalName) && Element.Name.LocalName is not ("ui-use" or "ui-slot");



    private Dictionary<string, string> Declarations(XElement Element)
    {
        var Result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var Declaration in UiStyleText.ParseDeclarations(
                     UiStyleText.Attribute(Element, "style")))
        {
            Result[Declaration.Name] = Declaration.Value;
        }
        return Result;
    }

    private static void WriteDeclaration(XElement Element, string Name, string? Value)
    {
        var Declarations = UiStyleText.ParseDeclarations(
            UiStyleText.Attribute(Element, "style"));
        var Found = false;
        var Result = new List<UiStyleDeclaration>();
        foreach (var Declaration in Declarations)
        {
            if (!Declaration.Name.Equals(Name, StringComparison.OrdinalIgnoreCase))
            {
                Result.Add(Declaration);
                continue;
            }
            Found = true;
            if (Value is not null)
            {
                Result.Add(Declaration with { Value = Value });
            }
        }
        if (!Found && Value is not null)
        {
            Result.Add(new(Name, Value, false));
        }
        var Text = UiStyleText.SerializeDeclarations(Result);
        Element.SetAttributeValue("style", Text.Length == 0 ? null : Text);
    }

    private static void SetOrRemove(XElement Element, string Name, string? Value)
    {
        var Normalized = Value?.Trim() ?? string.Empty;
        Element.SetAttributeValue(Name, Normalized.Length == 0 ? null : Normalized);
    }

    private string HashSource()
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path)));
        }
        catch (Exception Exception) when (Exception is IOException or
            UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string Canonical(XDocument Document) =>
        Document.ToString(SaveOptions.DisableFormatting);
}
