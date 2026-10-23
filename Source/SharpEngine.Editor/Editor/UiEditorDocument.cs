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

internal sealed record UiEditorSnapshot(string Xml, string? Selection);

internal sealed record UiEditorGestureBase(bool Absolute, float Left, float Top,
    float Width, float Height, float MarginLeft, float MarginTop);

internal sealed class UiEditorGesture
{
    public UiEditorGesture(string Name, string Xml, string? Selection,
        UiEditorGestureBase Base)
    {
        this.Name = Name;
        this.Xml = Xml;
        this.Selection = Selection;
        this.Base = Base;
        this.Handle = string.Empty;
    }

    public string Name { get; }
    public string Xml { get; }
    public string? Selection { get; }
    public UiEditorGestureBase Base { get; }
    public string Handle { get; set; }
    public bool Changed { get; set; }
}

/// <summary>Authoritative authoring model for one RML document. XML source is the
/// authoritative representation; selection, resolved styles, diagnostics and the
/// active drag transaction are derived or transient state of this editor instance.</summary>
internal sealed partial class UiEditorDocument
{
    public const int DesignWidth = 1280;
    public const int DesignHeight = 720;

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
        "img", "text",
    };
    private static readonly HashSet<string> ContainerTags = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "div", "panel", "section", "ul", "ol", "li", "form",
    };
    /// <summary>Composition directives are valid template content even though RmlUi never
    /// sees them: they are resolved before the document is presented.</summary>
    private static readonly HashSet<string> DirectiveTags = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "ui-slot", "ui-if", "ui-repeat",
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
        IReadOnlyList<string> StyleSheets)
    {
        this.Asset = Asset;
        this.Path = Path;
        this.OwnerRoot = System.IO.Path.GetFullPath(OwnerRoot);
        this.Document = Document;
        this.Format = Format;
        this.PackageName = PackageName;
        this.PackageStyleSheets = StyleSheets;
        this.IsTemplate = Document.Root?.Name.LocalName == "template";
        this.SavedState = Canonical(Document);
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
    /// <summary>True when this asset is a reusable <c>&lt;template&gt;</c> definition. The
    /// editor presents its content with placeholder values; it has no screen size or body.</summary>
    public bool IsTemplate { get; }
    public string? SelectedKey => SelectionKey;
    public long Revision { get; private set; }
    public bool IsReadOnly => Asset.ReadOnly || Asset.Source == AssetSource.Engine;
    public bool IsDirty => Canonical(Document) != SavedState;
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
            UiDocumentFormat.Package, Package.Name, Package.StyleSheets);
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
        Message = string.Empty;
        var Normalized = Tag.Trim().ToLowerInvariant();
        if (Normalized is not ("div" or "span" or "button" or "input" or "img"))
        {
            Message = $"Unsupported UI element '{Tag}'";
            return false;
        }
        var BodyElement = Body();
        if (BodyElement is null)
        {
            Message = "RML document has no body";
            return false;
        }
        var Selected = SelectedElement();
        var Parent = BodyElement;
        if (Selected is not null)
        {
            Parent = CanContain(Selected) ? Selected : Selected.Parent ?? BodyElement;
        }
        var Id = UniqueId(DefaultPrefix(Normalized));
        var Created = new XElement(Normalized, new XAttribute("id", Id));
        switch (Normalized)
        {
            case "div":
                Created.SetAttributeValue("style",
                    "display: flex; flex-direction: column; gap: 8px;");
                break;
            case "span":
                Created.Value = "Text";
                break;
            case "button":
                Created.Value = "Button";
                Created.SetAttributeValue("data-command", Id);
                break;
            case "input":
                Created.SetAttributeValue("type", "text");
                Created.SetAttributeValue("placeholder", "Input");
                break;
            case "img":
                Created.SetAttributeValue("src", "sharpengine-resource://engine/Resources/Editor/Icons/image.tga");
                break;
        }
        return Edit("add " + Normalized, () =>
        {
            Parent.Add(Created);
            SelectionKey = KeyOf(Created);
        }, out Message);
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
        RedoStack.Push(new(Canonical(Document), SelectedKey));
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
        UndoStack.Push(new(Canonical(Document), SelectedKey));
        Restore(RedoStack.Pop());
    }

    public bool BeginGesture(string Name, int RectX, int RectY, int RectWidth,
        int RectHeight, out string Message)
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
                out var Base, out Message))
        {
            return false;
        }
        Gesture = new UiEditorGesture(Name, Canonical(Document), SelectedKey, Base);
        return true;
    }

    public bool ApplyGestureMove(string Handle, int DeltaX, int DeltaY,
        bool SnapGrid, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Gesture is null || Element is null)
        {
            Message = "No active element gesture";
            return false;
        }
        if (!string.Equals(Handle, "move", StringComparison.Ordinal))
        {
            Message = "Unknown element gesture";
            return false;
        }
        int X = Snap(DeltaX, SnapGrid);
        int Y = Snap(DeltaY, SnapGrid);
        var Base = Gesture.Base;
        if (Base.Absolute)
        {
            WriteDeclaration(Element, "left", FormatLength(Base.Left + X));
            WriteDeclaration(Element, "top", FormatLength(Base.Top + Y));
        }
        else
        {
            WriteDeclaration(Element, "margin-left", FormatLength(Base.MarginLeft + X));
            WriteDeclaration(Element, "margin-top", FormatLength(Base.MarginTop + Y));
        }
        Gesture.Handle = "move";
        Gesture.Changed = true;
        Revision++;
        return true;
    }

    public bool ApplyGestureResize(string Handle, int DeltaX, int DeltaY,
        bool SnapGrid, out string Message)
    {
        Message = string.Empty;
        var Element = SelectedElement();
        if (Gesture is null || Element is null)
        {
            Message = "No active element gesture";
            return false;
        }
        var Base = Gesture.Base;
        int X = Snap(DeltaX, SnapGrid);
        int Y = Snap(DeltaY, SnapGrid);
        if (!Base.Absolute)
        {
            var Horizontal = Handle.Contains('e', StringComparison.Ordinal) ||
                Handle.Contains('w', StringComparison.Ordinal);
            var Vertical = Handle.Contains('n', StringComparison.Ordinal) ||
                Handle.Contains('s', StringComparison.Ordinal);
            if (Horizontal && Handle.Contains('w', StringComparison.Ordinal) ||
                Vertical && Handle.Contains('n', StringComparison.Ordinal))
            {
                Message = "Flow elements resize from the right or bottom edge";
                return false;
            }
            var Width = Math.Max(8, Base.Width + (Horizontal ? X : 0));
            var Height = Math.Max(8, Base.Height + (Vertical ? Y : 0));
            WriteDeclaration(Element, "width", FormatLength(Width));
            WriteDeclaration(Element, "height", FormatLength(Height));
            Gesture.Handle = Handle;
            Gesture.Changed = true;
            Revision++;
            return true;
        }
        var Left = Base.Left;
        var Top = Base.Top;
        var TargetWidth = Base.Width;
        var TargetHeight = Base.Height;
        if (Handle.Contains('w', StringComparison.Ordinal))
        {
            Left += X;
            TargetWidth -= X;
        }
        if (Handle.Contains('e', StringComparison.Ordinal))
        {
            TargetWidth += X;
        }
        if (Handle.Contains('n', StringComparison.Ordinal))
        {
            Top += Y;
            TargetHeight -= Y;
        }
        if (Handle.Contains('s', StringComparison.Ordinal))
        {
            TargetHeight += Y;
        }
        if (TargetWidth < 8 || TargetHeight < 8)
        {
            Message = "Elements cannot be resized below 8px";
            return false;
        }
        WriteDeclaration(Element, "left", FormatLength(Left));
        WriteDeclaration(Element, "top", FormatLength(Top));
        WriteDeclaration(Element, "width", FormatLength(TargetWidth));
        WriteDeclaration(Element, "height", FormatLength(TargetHeight));
        Gesture.Handle = Handle;
        Gesture.Changed = true;
        Revision++;
        return true;
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
        var Inline = Declarations(Element);
        var Result = new List<(string, string)>(6);
        foreach (var Name in new[]
                 {
                     "left", "top", "right", "bottom", "width", "height",
                     "margin-left", "margin-top",
                 })
        {
            if (Inline.TryGetValue(Name, out var Value))
            {
                Result.Add((Name, Value));
            }
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
            UndoStack.Push(new(Active.Xml, Active.Selection));
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
            SavedState = Canonical(Document);
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
            return UiDocumentAsset.Create(PackageName, Document.ToString())
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
            if (Canonical(Rml) != Canonical(Document))
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
                ["width"] = DesignWidth,
                ["height"] = DesignHeight,
            },
            ["elements"] = Elements,
            ["styleSheet"] = new JsonObject
            {
                ["files"] = new JsonArray(StyleSheet.Files.Select(File =>
                    (JsonNode)File).ToArray()),
                ["inlineBlocks"] = StyleSheet.InlineBlocks,
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
        var Before = Canonical(Document);
        var BeforeSelection = SelectedKey;
        Mutation();
        if (Canonical(Document) == Before)
        {
            return false;
        }
        UndoStack.Push(new(Before, BeforeSelection));
        RedoStack.Clear();
        Revision++;
        RefreshDerivedState();
        return true;
    }

    private void Restore(UiEditorSnapshot Snapshot)
    {
        Document = XDocument.Parse(Snapshot.Xml,
            LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        SelectionKey = Snapshot.Selection;
        Revision++;
        RefreshDerivedState();
    }

    private void RefreshDerivedState()
    {
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
                     TextOnlyTags.Contains(Value.Name.LocalName)))
        {
            Diagnostics.Add(new("warning", "UI_ELEMENT_LEAF_HAS_CHILDREN",
                $"<{Leaf.Name.LocalName}> is a leaf element but contains children"));
        }
        foreach (var Unknown in Elements.Where(Value => !LeafTags.Contains(
                     Value.Name.LocalName) && !ContainerTags.Contains(
                     Value.Name.LocalName) && !DirectiveTags.Contains(
                     Value.Name.LocalName)))
        {
            Diagnostics.Add(new("warning", "UI_ELEMENT_TAG_UNKNOWN",
                $"<{Unknown.Name.LocalName}> is not a known RML layout element"));
        }
        StyleSheet = UiStyleSheet.Load(Document, Path, OwnerRoot, Diagnostics,
            PackageStyleSheets);
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
    }

    private bool TryBuildGestureBase(XElement Element, int RectX, int RectY,
        int RectWidth, int RectHeight, out UiEditorGestureBase Base, out string Message)
    {
        Message = string.Empty;
        Base = new(false, 0, 0, RectWidth, RectHeight, 0, 0);
        var Style = Declarations(Element);
        var Position = Style.TryGetValue("position", out var PositionValue)
            ? PositionValue.ToLowerInvariant()
            : "static";
        var Absolute = Position is "absolute" or "fixed";
        if (Absolute)
        {
            if (!TryPixelOffset(Style, "left", out var Left) ||
                !TryPixelOffset(Style, "top", out var Top))
            {
                Message = "Moving an absolute element requires pixel left and top values";
                return false;
            }
            var Width = TryPixelSize(Style, "width", RectWidth);
            var Height = TryPixelSize(Style, "height", RectHeight);
            Base = new(true, Left, Top, Width, Height, 0, 0);
            return true;
        }
        Base = new(false, 0, 0, Math.Max(8, RectWidth), Math.Max(8, RectHeight),
            UiStyleText.MarginSide(Element, "left"), UiStyleText.MarginSide(Element, "top"));
        return true;
    }

    private static bool TryPixelOffset(Dictionary<string, string> Style, string Name,
        out float Value)
    {
        Value = 0;
        if (!Style.TryGetValue(Name, out var Raw))
        {
            return false;
        }
        var Trimmed = Raw.Trim();
        if (Trimmed.EndsWith('%') || Trimmed is "auto" or "none")
        {
            return false;
        }
        var Parsed = UiStyleText.ParseLength(Trimmed);
        if (Parsed is null)
        {
            return false;
        }
        Value = Parsed.Value;
        return true;
    }

    private static float TryPixelSize(Dictionary<string, string> Style, string Name,
        int Fallback)
    {
        if (!Style.TryGetValue(Name, out var Raw))
        {
            return Math.Max(8, Fallback);
        }
        var Trimmed = Raw.Trim();
        if (Trimmed.EndsWith('%') || Trimmed is "auto" or "none")
        {
            return Math.Max(8, Fallback);
        }
        var Parsed = UiStyleText.ParseLength(Trimmed);
        return Parsed is null ? Math.Max(8, Fallback) : Math.Max(8, Parsed.Value);
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

    private static string DefaultPrefix(string Tag) => Tag switch
    {
        "span" => "text",
        "button" => "button",
        "input" => "input",
        "img" => "image",
        _ => "container",
    };

    private static bool CanContain(XElement Element) =>
        !LeafTags.Contains(Element.Name.LocalName);



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

    private static void SetOrRemove(XElement Element, string Name, string Value)
    {
        var Normalized = Value.Trim();
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
