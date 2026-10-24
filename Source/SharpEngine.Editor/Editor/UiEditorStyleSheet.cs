using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SharpEngine.Editor;

internal sealed record UiStyleDeclaration(string Name, string Value, bool Important);

internal sealed record UiStyleRule(UiSelector Selector, string SelectorText,
    IReadOnlyList<UiStyleDeclaration> Declarations, int Specificity, int Order);

internal sealed record UiComputedValue(string Name, string Value, string Source);

/// <summary>Preview-only RCSS model. The authored RCSS stays authoritative; this
/// class resolves the cascade for the design surface so each authored element can
/// be handed to RmlUi as one flattened declaration set. Unsupported selector forms
/// are reported instead of being silently ignored.</summary>
internal sealed class UiStyleSheet
{
    public static readonly UiStyleSheet Empty = new([], [], [], [], 0);

    private UiStyleSheet(IReadOnlyList<UiStyleRule> Rules,
        IReadOnlyList<string> Classes, IReadOnlyList<string> Tags,
        IReadOnlyList<string> Files, int InlineBlocks)
    {
        this.Rules = Rules;
        this.Classes = Classes;
        this.Tags = Tags;
        this.Files = Files;
        this.InlineBlocks = InlineBlocks;
    }

    public IReadOnlyList<UiStyleRule> Rules { get; }
    public IReadOnlyList<string> Classes { get; }
    public IReadOnlyList<string> Tags { get; }
    /// <summary>External stylesheet files, in document order of their links.</summary>
    public IReadOnlyList<string> Files { get; }
    /// <summary>Inline <c>&lt;style&gt;</c> blocks that contributed rules.</summary>
    public int InlineBlocks { get; }

    public IReadOnlyDictionary<string, UiComputedValue> Compute(XElement Element)
    {
        var Winners = new Dictionary<string, UiComputedValue>(StringComparer.OrdinalIgnoreCase);
        var ImportantWinners = new Dictionary<string, UiComputedValue>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var Rule in Rules.Where(Rule => Rule.Selector.Matches(Element))
                     .OrderBy(Rule => Rule.Specificity).ThenBy(Rule => Rule.Order))
        {
            foreach (var Declaration in Rule.Declarations)
            {
                var Target = Declaration.Important ? ImportantWinners : Winners;
                Target[Declaration.Name] = new UiComputedValue(Declaration.Name,
                    Declaration.Value, Rule.SelectorText);
            }
        }
        foreach (var Declaration in UiStyleText.ParseDeclarations(
                     UiStyleText.Attribute(Element, "style")))
        {
            var Target = Declaration.Important ? ImportantWinners : Winners;
            Target[Declaration.Name] = new UiComputedValue(Declaration.Name,
                Declaration.Value, "inline");
        }
        foreach (var Pair in ImportantWinners)
        {
            Winners[Pair.Key] = Pair.Value;
        }
        return Winners;
    }

    public static UiStyleSheet Load(XDocument Document, string DocumentPath,
        string OwnerRoot, List<UiEditorDiagnostic> Diagnostics,
        IReadOnlyList<string>? PackageStyleSheets = null,
        IReadOnlyList<string>? ReferencedStyleSheets = null,
        IReadOnlyList<SharpEngine.UI.UiPrivateStyle>? PrivateStyles = null)
    {
        var Rules = new List<UiStyleRule>();
        var Classes = new SortedSet<string>(StringComparer.Ordinal);
        var Tags = new SortedSet<string>(StringComparer.Ordinal);
        var Files = new List<string>();
        var Skipped = 0;
        var Order = 0;
        var InlineBlocks = 0;
        var DocumentDirectory = System.IO.Path.GetDirectoryName(DocumentPath) ??
            string.Empty;
        var OwnerPrefix = OwnerRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar) +
            System.IO.Path.DirectorySeparatorChar;
        var Head = Document.Root?.Elements()
            .FirstOrDefault(Element => Element.Name.LocalName == "head");
        // A template has no <head>, so a .sui package lists its stylesheets instead.
        // They load first, like the runtime does, so document order still decides ties.
        foreach (var Href in PackageStyleSheets ?? [])
        {
            if (!TryLoadFile(Href, DocumentDirectory, OwnerPrefix, Files, Diagnostics,
                    ref Order, ref Skipped))
            {
                continue;
            }
        }
        foreach (var Sheet in (ReferencedStyleSheets ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _ = TryLoadFile(Sheet, DocumentDirectory,
                System.IO.Path.GetDirectoryName(Sheet)! + System.IO.Path.DirectorySeparatorChar,
                Files, Diagnostics, ref Order, ref Skipped);
        }
        foreach (var Private in PrivateStyles ?? [])
        {
            if (Private.Rcss.Length > 0)
            {
                InlineBlocks++;
                AddRules(SharpEngine.UI.UiStyleSources.RebaseUrls(Private.Rcss, Private.SourcePath), ref Order, ref Skipped);
            }
        }
        // RmlUi pushes <link type="text/rcss|text/css"> and inline <style> text into
        // one ordered stylesheet list, so the preview must read both in document
        // order. Reading only links silently dropped every inline declaration.
        foreach (var Element in Document.Descendants().Where(Element =>
                     IsStyleSheetLink(Element) || Element.Name.LocalName == "style"))
        {
            if (Element.Name.LocalName == "style")
            {
                if (Head is null || !Element.Ancestors().Contains(Head))
                {
                    Diagnostics.Add(new("warning", "UI_STYLE_OUTSIDE_HEAD",
                        "An inline <style> outside <head> is ignored by RmlUi and by the " +
                        "design surface"));
                    continue;
                }
                var Inline = Element.Value;
                if (Inline.Trim().Length == 0)
                {
                    continue;
                }
                InlineBlocks++;
                AddRules(Inline, ref Order, ref Skipped);
                continue;
            }
            var Href = UiStyleText.Attribute(Element, "href").Trim();
            if (Href.Length == 0)
            {
                continue;
            }
            _ = TryLoadFile(Href, DocumentDirectory, OwnerPrefix, Files, Diagnostics,
                ref Order, ref Skipped);
        }
        if (Skipped != 0)
        {
            Diagnostics.Add(new("warning", "UI_STYLE_SELECTOR_UNSUPPORTED",
                $"{Skipped} stylesheet rule(s) use selector forms the design surface " +
                "cannot evaluate; their declarations are not previewed"));
        }
        return new UiStyleSheet(Rules, Classes.ToArray(), Tags.ToArray(), Files.ToArray(),
            InlineBlocks);

        void AddRules(string Source, ref int NextOrder, ref int NextSkipped)
        {
            foreach (var Rule in ParseRules(Source, ref NextSkipped))
            {
                Rules.Add(Rule with { Order = NextOrder++ });
                foreach (var ClassName in Rule.Selector.ClassNames)
                {
                    Classes.Add(ClassName);
                }
                foreach (var TagName in Rule.Selector.TagNames)
                {
                    Tags.Add(TagName);
                }
            }
        }

        bool TryLoadFile(string Href, string DirectoryPath, string OwnerPrefix,
            List<string> Files, List<UiEditorDiagnostic> Diagnostics,
            ref int NextOrder, ref int NextSkipped)
        {
            string FullPath;
            try
            {
                FullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    DirectoryPath,
                    Href.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            }
            catch (Exception Exception) when (Exception is ArgumentException or
                NotSupportedException or System.IO.PathTooLongException)
            {
                Diagnostics.Add(new("error", "UI_STYLE_PATH", Exception.Message));
                return false;
            }
            if (!FullPath.StartsWith(OwnerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Diagnostics.Add(new("error", "UI_STYLE_ESCAPE",
                    $"Stylesheet '{Href}' escapes its owning content root"));
                return false;
            }
            if (!File.Exists(FullPath))
            {
                Diagnostics.Add(new("error", "UI_STYLE_MISSING",
                    $"Stylesheet '{Href}' does not exist"));
                return false;
            }
            string Source;
            try
            {
                Source = File.ReadAllText(FullPath);
            }
            catch (Exception Exception) when (Exception is IOException or
                UnauthorizedAccessException)
            {
                Diagnostics.Add(new("error", "UI_STYLE_READ",
                    $"Stylesheet '{Href}' cannot be read: {Exception.Message}"));
                return false;
            }
            Files.Add(Href);
            AddRules(Source, ref NextOrder, ref NextSkipped);
            return true;
        }
    }

    /// <summary>RmlUi accepts both <c>text/rcss</c> and <c>text/css</c> for a stylesheet
    /// link; the preview must accept exactly the same types.</summary>
    private static bool IsStyleSheetLink(XElement Element)
    {
        if (Element.Name.LocalName != "link")
        {
            return false;
        }
        var Type = UiStyleText.Attribute(Element, "type");
        return Type.Equals("text/rcss", StringComparison.OrdinalIgnoreCase) ||
            Type.Equals("text/css", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<UiStyleRule> ParseRules(string Source, ref int Skipped)
    {
        var Result = new List<UiStyleRule>();
        var Text = Regex.Replace(Source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var Index = 0;
        while (Index < Text.Length)
        {
            var Open = Text.IndexOf('{', Index);
            if (Open < 0)
            {
                break;
            }
            var Close = UiStyleText.FindBlockEnd(Text, Open);
            if (Close < 0)
            {
                break;
            }
            var SelectorText = Text[Index..Open].Trim();
            var Body = Text[(Open + 1)..Close];
            Index = Close + 1;
            if (SelectorText.Length == 0)
            {
                continue;
            }
            var Declarations = UiStyleText.ParseDeclarations(Body);
            if (Declarations.Count == 0)
            {
                continue;
            }
            foreach (var Piece in SelectorText.Split(','))
            {
                var Candidate = Piece.Trim();
                if (Candidate.Length == 0)
                {
                    continue;
                }
                if (!UiSelector.TryParse(Candidate, out var Selector) || Selector is null)
                {
                    Skipped++;
                    continue;
                }
                Result.Add(new(Selector, Candidate, Declarations.ToArray(),
                    Selector.Specificity, 0));
            }
        }
        return Result;
    }
}

internal sealed record UiSelectorPart(UiSelectorCompound Compound, bool Child);

internal sealed record UiSelectorCompound(string? Tag, string? Id,
    IReadOnlyList<string> Classes)
{
    public int Specificity => (Id is null ? 0 : 10000) + Classes.Count * 100 +
        (Tag is null or "*" ? 0 : 1);

    public bool Matches(XElement Element)
    {
        if (Tag is not null && Tag != "*" &&
            !Element.Name.LocalName.Equals(Tag, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (Id is not null && !Id.Equals(UiStyleText.Attribute(Element, "id"),
                StringComparison.Ordinal))
        {
            return false;
        }
        if (Classes.Count != 0)
        {
            var Present = UiStyleText.ClassList(Element);
            foreach (var ClassName in Classes)
            {
                if (!Present.Contains(ClassName))
                {
                    return false;
                }
            }
        }
        return true;
    }
}

internal sealed class UiSelector
{
    private UiSelector(IReadOnlyList<UiSelectorPart> Parts)
    {
        this.Parts = Parts;
        ClassNames = Parts.SelectMany(Part => Part.Compound.Classes)
            .Distinct(StringComparer.Ordinal).ToArray();
        TagNames = Parts.Select(Part => Part.Compound.Tag)
            .Where(Tag => Tag is not null && Tag != "*").Select(Tag => Tag!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Specificity = Parts.Sum(Part => Part.Compound.Specificity);
    }

    private IReadOnlyList<UiSelectorPart> Parts { get; }
    public IReadOnlyList<string> ClassNames { get; }
    public IReadOnlyList<string> TagNames { get; }
    public int Specificity { get; }

    public bool Matches(XElement Element) => MatchAt(Element, Parts.Count - 1);

    public static bool TryParse(string Text, out UiSelector? Result)
    {
        Result = null;
        var Trimmed = Text.Trim();
        if (Trimmed.Length == 0 ||
            Trimmed.IndexOfAny([':', '[', ']', '+', '~', '@', '(', ')', '"', '\'']) >= 0)
        {
            return false;
        }
        var Parts = new List<UiSelectorPart>();
        var Index = 0;
        var Child = false;
        var SeekCompound = true;
        while (Index < Trimmed.Length)
        {
            var Character = Trimmed[Index];
            if (char.IsWhiteSpace(Character))
            {
                if (!Child)
                {
                    SeekCompound = true;
                }
                Index++;
                continue;
            }
            if (Character == '>')
            {
                if (Parts.Count == 0 || Child)
                {
                    return false;
                }
                Child = true;
                SeekCompound = true;
                Index++;
                continue;
            }
            if (!SeekCompound)
            {
                return false;
            }
            var Start = Index;
            while (Index < Trimmed.Length && !char.IsWhiteSpace(Trimmed[Index]) &&
                   Trimmed[Index] != '>')
            {
                Index++;
            }
            var Token = Trimmed[Start..Index];
            if (!TryParseCompound(Token, out var Compound) || Compound is null)
            {
                return false;
            }
            Parts.Add(new(Compound, Child));
            Child = false;
            SeekCompound = false;
        }
        if (Parts.Count == 0 || Child)
        {
            return false;
        }
        Result = new UiSelector(Parts);
        return true;
    }

    private bool MatchAt(XElement? Element, int Index)
    {
        if (Element is null || Index < 0)
        {
            return false;
        }
        if (!Parts[Index].Compound.Matches(Element))
        {
            return false;
        }
        if (Index == 0)
        {
            return true;
        }
        if (Parts[Index].Child)
        {
            return MatchAt(Element.Parent, Index - 1);
        }
        for (var Ancestor = Element.Parent; Ancestor is not null; Ancestor = Ancestor.Parent)
        {
            if (MatchAt(Ancestor, Index - 1))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryParseCompound(string Token, out UiSelectorCompound? Result)
    {
        Result = null;
        if (Token.Length == 0)
        {
            return false;
        }
        var Index = 0;
        string? Tag = null;
        string? Id = null;
        var Classes = new List<string>();
        if (Token[0] == '*')
        {
            Tag = "*";
            Index = 1;
        }
        else if (char.IsLetter(Token[0]))
        {
            var Start = Index;
            while (Index < Token.Length && (char.IsLetterOrDigit(Token[Index]) ||
                   Token[Index] == '-' || Token[Index] == '_'))
            {
                Index++;
            }
            Tag = Token[Start..Index];
        }
        while (Index < Token.Length)
        {
            var Marker = Token[Index];
            if (Marker != '.' && Marker != '#')
            {
                return false;
            }
            var Start = ++Index;
            while (Index < Token.Length && (char.IsLetterOrDigit(Token[Index]) ||
                   Token[Index] == '-' || Token[Index] == '_'))
            {
                Index++;
            }
            if (Index == Start)
            {
                return false;
            }
            var Name = Token[Start..Index];
            if (Marker == '#')
            {
                if (Id is not null)
                {
                    return false;
                }
                Id = Name;
            }
            else
            {
                Classes.Add(Name);
            }
        }
        if (Tag is null && Id is null && Classes.Count == 0)
        {
            return false;
        }
        Result = new(Tag, Id, Classes);
        return true;
    }
}

internal sealed record UiStyleField(string Name, string Label, string Group,
    string Placeholder, UiStyleValueKind Kind, IReadOnlyList<string> Options);

internal enum UiStyleValueKind
{
    Any,
    Length,
    Size,
    Number,
    Color,
    Enum,
}

internal static class UiStyleProperties
{
    public static readonly IReadOnlyList<UiStyleField> All =
    [
        Field("position", "Position", "Layout", "static", UiStyleValueKind.Enum,
            ["static", "relative", "absolute", "fixed"]),
        Field("display", "Display", "Layout", "block", UiStyleValueKind.Enum,
            ["block", "inline", "inline-block", "flex", "none"]),
        Field("left", "Left", "Layout", "auto", UiStyleValueKind.Length),
        Field("top", "Top", "Layout", "auto", UiStyleValueKind.Length),
        Field("right", "Right", "Layout", "auto", UiStyleValueKind.Length),
        Field("bottom", "Bottom", "Layout", "auto", UiStyleValueKind.Length),
        Field("width", "Width", "Layout", "auto", UiStyleValueKind.Size),
        Field("height", "Height", "Layout", "auto", UiStyleValueKind.Size),
        Field("min-width", "Min Width", "Layout", "0px", UiStyleValueKind.Size),
        Field("min-height", "Min Height", "Layout", "0px", UiStyleValueKind.Size),
        Field("max-width", "Max Width", "Layout", "none", UiStyleValueKind.Size),
        Field("max-height", "Max Height", "Layout", "none", UiStyleValueKind.Size),
        Field("z-index", "Z Index", "Layout", "auto", UiStyleValueKind.Number),
        Field("flex-direction", "Direction", "Flex", "row", UiStyleValueKind.Enum,
            ["row", "row-reverse", "column", "column-reverse"]),
        Field("flex-wrap", "Wrap", "Flex", "nowrap", UiStyleValueKind.Enum,
            ["nowrap", "wrap", "wrap-reverse"]),
        Field("justify-content", "Justify", "Flex", "flex-start", UiStyleValueKind.Enum,
            ["flex-start", "flex-end", "center", "space-between", "space-around"]),
        Field("align-items", "Align Items", "Flex", "flex-start", UiStyleValueKind.Enum,
            ["flex-start", "flex-end", "center", "baseline", "stretch"]),
        Field("align-self", "Align Self", "Flex", "auto", UiStyleValueKind.Enum,
            ["auto", "flex-start", "flex-end", "center", "baseline", "stretch"]),
        Field("gap", "Gap", "Flex", "8px", UiStyleValueKind.Length),
        Field("flex-grow", "Grow", "Flex", "0", UiStyleValueKind.Number),
        Field("flex-shrink", "Shrink", "Flex", "1", UiStyleValueKind.Number),
        Field("flex-basis", "Basis", "Flex", "auto", UiStyleValueKind.Size),
        Field("padding", "Padding", "Spacing", "8px", UiStyleValueKind.Length),
        Field("margin", "Margin", "Spacing", "0px", UiStyleValueKind.Length),
        Field("border-width", "Border Width", "Spacing", "1px", UiStyleValueKind.Length),
        Field("border-color", "Border Color", "Spacing", "#2f3a46", UiStyleValueKind.Color),
        Field("border-radius", "Radius", "Spacing", "4px", UiStyleValueKind.Length),
        Field("font-size", "Font Size", "Text", "14px", UiStyleValueKind.Size),
        Field("font-family", "Font Family", "Text", "inherit", UiStyleValueKind.Any),
        Field("font-weight", "Font Weight", "Text", "normal", UiStyleValueKind.Any),
        Field("line-height", "Line Height", "Text", "1.4", UiStyleValueKind.Any),
        Field("letter-spacing", "Letter Spacing", "Text", "0px", UiStyleValueKind.Length),
        Field("color", "Color", "Text", "#e6edf6", UiStyleValueKind.Color),
        Field("text-align", "Text Align", "Text", "left", UiStyleValueKind.Enum,
            ["left", "center", "right", "justify"]),
        Field("white-space", "White Space", "Text", "normal", UiStyleValueKind.Enum,
            ["normal", "nowrap", "pre", "pre-wrap"]),
        Field("overflow", "Overflow", "Text", "visible", UiStyleValueKind.Enum,
            ["visible", "hidden", "auto", "scroll"]),
        Field("background-color", "Background", "Appearance", "#202833", UiStyleValueKind.Color),
        Field("opacity", "Opacity", "Appearance", "1", UiStyleValueKind.Number),
        Field("cursor", "Cursor", "Appearance", "default", UiStyleValueKind.Any),
    ];

    public static IReadOnlyList<string> Groups { get; } =
        ["Layout", "Flex", "Spacing", "Text", "Appearance"];

    public static UiStyleField? Find(string Name) =>
        All.FirstOrDefault(Field => Field.Name.Equals(Name, StringComparison.OrdinalIgnoreCase));

    private static readonly Regex ClassNamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant);

    public static bool ValidClassName(string Name, out string Message)
    {
        Message = string.Empty;
        if (!ClassNamePattern.IsMatch(Name))
        {
            Message = "Class names must start with a letter or '_' and contain only " +
                "letters, digits, '-' or '_'";
            return false;
        }
        return true;
    }

    public static bool Validate(string Name, string Value, out string Message)
    {
        Message = string.Empty;
        if (Value.IndexOfAny([';', '{', '}']) >= 0)
        {
            Message = "Style values cannot contain ';', '{' or '}'";
            return false;
        }
        var Definition = Find(Name);
        if (Definition is null)
        {
            if (!Regex.IsMatch(Name, "^[A-Za-z][A-Za-z0-9-]*$"))
            {
                Message = $"'{Name}' is not a valid style property name";
                return false;
            }
            return true;
        }
        switch (Definition.Kind)
        {
            case UiStyleValueKind.Number:
                if (!float.TryParse(Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var Parsed) ||
                    !float.IsFinite(Parsed))
                {
                    Message = $"{Definition.Label} requires a finite number";
                    return false;
                }
                return true;
            case UiStyleValueKind.Length:
                if (!LengthPattern.IsMatch(Value))
                {
                    Message = $"{Definition.Label} requires a length such as 12px, 50% or auto";
                    return false;
                }
                return true;
            case UiStyleValueKind.Size:
                if (!LengthPattern.IsMatch(Value))
                {
                    Message = $"{Definition.Label} requires auto, none or a length";
                    return false;
                }
                return true;
            case UiStyleValueKind.Color:
                if (!ColorPattern.IsMatch(Value))
                {
                    Message = $"{Definition.Label} requires a color such as #ffffff or rgb(0, 0, 0)";
                    return false;
                }
                return true;
            case UiStyleValueKind.Enum:
                if (!Definition.Options.Contains(Value, StringComparer.OrdinalIgnoreCase))
                {
                    Message = $"{Definition.Label} must be one of " +
                        string.Join(", ", Definition.Options);
                    return false;
                }
                return true;
            default:
                return true;
        }
    }

    private static readonly Regex LengthPattern = new(
        "^(auto|none|normal|inherit|initial|unset|fit-content|" +
        "-?\\d+(\\.\\d+)?(px|%|em|rem|pt|vh|vw|deg|x)?)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ColorPattern = new(
        "^(#[0-9a-fA-F]{3,8}|rgba?\\([^()]+\\)|transparent|inherit|initial|unset|" +
        "[A-Za-z]+)$", RegexOptions.CultureInvariant);

    private static UiStyleField Field(string Name, string Label, string Group,
        string Placeholder, UiStyleValueKind Kind, IReadOnlyList<string>? Options = null) =>
        new(Name, Label, Group, Placeholder, Kind,
            Options ?? Array.Empty<string>());
}

internal static class UiStyleText
{
    public static string Attribute(XElement Element, string Name) =>
        ((string?)Element.Attribute(Name) ?? string.Empty).Trim();

    public static HashSet<string> ClassList(XElement Element) =>
        Attribute(Element, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

    public static int FindBlockEnd(string Text, int Open)
    {
        var Depth = 0;
        var Quote = '\0';
        for (var Index = Open; Index < Text.Length; Index++)
        {
            var Character = Text[Index];
            if (Quote != '\0')
            {
                if (Character == Quote && Text[Index - 1] != '\\')
                {
                    Quote = '\0';
                }
                continue;
            }
            if (Character is '"' or '\'')
            {
                Quote = Character;
                continue;
            }
            if (Character == '{')
            {
                Depth++;
                continue;
            }
            if (Character != '}')
            {
                continue;
            }
            Depth--;
            if (Depth == 0)
            {
                return Index;
            }
        }
        return -1;
    }

    public static List<UiStyleDeclaration> ParseDeclarations(string Source)
    {
        var Result = new List<UiStyleDeclaration>();
        var Start = 0;
        var Depth = 0;
        var Quote = '\0';
        for (var Index = 0; Index <= Source.Length; Index++)
        {
            var AtEnd = Index == Source.Length;
            var Character = AtEnd ? ';' : Source[Index];
            if (!AtEnd && Quote != '\0')
            {
                if (Character == Quote && Source[Index - 1] != '\\')
                {
                    Quote = '\0';
                }
                continue;
            }
            if (!AtEnd && Character is '"' or '\'')
            {
                Quote = Character;
                continue;
            }
            if (!AtEnd && Character == '(')
            {
                Depth++;
                continue;
            }
            if (!AtEnd && Character == ')')
            {
                Depth = Math.Max(0, Depth - 1);
                continue;
            }
            if (Character != ';' || Depth != 0)
            {
                continue;
            }
            var Entry = Source[Start..Index].Trim();
            Start = Index + 1;
            if (Entry.Length == 0)
            {
                continue;
            }
            var Separator = Entry.IndexOf(':');
            if (Separator <= 0)
            {
                continue;
            }
            var Name = Entry[..Separator].Trim();
            var Value = Entry[(Separator + 1)..].Trim();
            if (Name.Length == 0 || Value.Length == 0)
            {
                continue;
            }
            var Important = false;
            var Suffix = Value.LastIndexOf("!important", StringComparison.OrdinalIgnoreCase);
            if (Suffix >= 0 && Suffix == Value.Length - 10)
            {
                Important = true;
                Value = Value[..Suffix].Trim();
            }
            if (Value.Length != 0)
            {
                Result.Add(new(Name, Value, Important));
            }
        }
        return Result;
    }

    public static string SerializeDeclarations(IReadOnlyList<UiStyleDeclaration> Values)
    {
        var Builder = new StringBuilder();
        foreach (var Value in Values)
        {
            if (Builder.Length != 0)
            {
                Builder.Append(' ');
            }
            Builder.Append(Value.Name).Append(": ").Append(Value.Value);
            if (Value.Important)
            {
                Builder.Append(" !important");
            }
            Builder.Append(';');
        }
        return Builder.ToString();
    }

    public static float? ParseLength(string Value)
    {
        var Trimmed = Value.Trim();
        var Match = Regex.Match(Trimmed, "^(-?\\d+(\\.\\d+)?)(px|em|rem|pt|%)?$",
            RegexOptions.CultureInvariant);
        if (!Match.Success)
        {
            return null;
        }
        if (!float.TryParse(Match.Groups[1].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var Result) || !float.IsFinite(Result))
        {
            return null;
        }
        return Result;
    }

    public static float MarginSide(XElement Element, string Side)
    {
        var Declarations = ParseDeclarations(Attribute(Element, "style"));
        foreach (var Declaration in Declarations.Where(Value =>
                     Value.Name.Equals("margin-" + Side, StringComparison.OrdinalIgnoreCase)))
        {
            var Parsed = ParseLength(Declaration.Value);
            if (Parsed is not null)
            {
                return Parsed.Value;
            }
        }
        foreach (var Declaration in Declarations.Where(Value =>
                     Value.Name.Equals("margin", StringComparison.OrdinalIgnoreCase)))
        {
            var Parts = Declaration.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var Index = Side switch
            {
                "top" => Parts.Length >= 1 ? 0 : -1,
                "right" => Parts.Length >= 2 ? 1 : Parts.Length >= 1 ? 0 : -1,
                "bottom" => Parts.Length >= 3 ? 2 : Parts.Length >= 1 ? 0 : -1,
                _ => Parts.Length >= 4 ? 3 : Parts.Length >= 2 ? 1 :
                    Parts.Length >= 1 ? 0 : -1,
            };
            if (Index < 0)
            {
                continue;
            }
            var Parsed = ParseLength(Parts[Index]);
            if (Parsed is not null)
            {
                return Parsed.Value;
            }
        }
        return 0;
    }
}
