using System.Text.RegularExpressions;

namespace SharpEngine.Editor;

internal sealed record RcssDeclaration(string Name, string Value, int ValueStart, int ValueEnd);
internal sealed record RcssSourceRule(string Selector, IReadOnlyList<RcssDeclaration> Declarations);

/// <summary>Edits one authored value by its original span. Unrelated RCSS stays byte-for-byte intact.</summary>
internal static class RcssSourceEditing
{
    internal static IReadOnlyList<RcssSourceRule> Read(string Source)
    {
        var Rules = new List<RcssSourceRule>();
        var Position = 0;
        while (Position < Source.Length)
        {
            var Open = Find(Source, Position, Source.Length, "{");
            if (Open < 0)
            {
                break;
            }
            var Depth = 1;
            var Close = Open;
            while (Depth > 0)
            {
                Close = Find(Source, Close + 1, Source.Length, "{}");
                if (Close < 0)
                {
                    throw new InvalidDataException("STYLESHEET_BRACE_MISMATCH");
                }
                Depth += Source[Close] == '{' ? 1 : -1;
            }
            var Selector = Regex.Replace(Source[Position..Open], @"/\*.*?\*/", " ", RegexOptions.Singleline).Trim();
            if (!Selector.StartsWith('@'))
            {
                var Fields = new List<RcssDeclaration>();
                var FieldStart = Open + 1;
                while (FieldStart < Close)
                {
                    var End = Find(Source, FieldStart, Close, ";");
                    End = End < 0 ? Close : End;
                    var Colon = Find(Source, FieldStart, End, ":");
                    if (Colon >= 0)
                    {
                        var Name = Regex.Replace(Source[FieldStart..Colon], @"/\*.*?\*/", " ", RegexOptions.Singleline).Trim();
                        var Start = Colon + 1;
                        while (Start < End && char.IsWhiteSpace(Source[Start]))
                        {
                            Start++;
                        }
                        var ValueEnd = End;
                        while (ValueEnd > Start && char.IsWhiteSpace(Source[ValueEnd - 1]))
                        {
                            ValueEnd--;
                        }
                        Fields.Add(new RcssDeclaration(Name, Source[Start..ValueEnd], Start, ValueEnd));
                    }
                    FieldStart = End + 1;
                }
                Rules.Add(new RcssSourceRule(Selector, Fields));
            }
            Position = Close + 1;
        }
        return Rules;
    }

    internal static string SetValue(string Source, int RuleIndex, string Name, string Value)
    {
        var Rules = Read(Source);
        if (RuleIndex < 0 || RuleIndex >= Rules.Count || Value.Trim().Length == 0 ||
            Find(Value, 0, Value.Length, ";{}") >= 0)
        {
            throw new InvalidDataException("STYLESHEET_VALUE_INVALID");
        }
        var Declaration = Rules[RuleIndex].Declarations.LastOrDefault(Field => Field.Name == Name) ??
            throw new InvalidDataException("STYLESHEET_PROPERTY_MISSING: " + Name);
        return Source[..Declaration.ValueStart] + Value.Trim() + Source[Declaration.ValueEnd..];
    }

    private static int Find(string Source, int Start, int End, string Tokens)
    {
        var Parentheses = 0;
        for (var Index = Start; Index < End; Index++)
        {
            var Character = Source[Index];
            if (Character == '/' && Index + 1 < End && Source[Index + 1] == '*')
            {
                var CommentEnd = Source.IndexOf("*/", Index + 2, StringComparison.Ordinal);
                if (CommentEnd < 0 || CommentEnd >= End)
                {
                    throw new InvalidDataException("STYLESHEET_COMMENT_UNCLOSED");
                }
                Index = CommentEnd + 1;
                continue;
            }
            if (Character is '\'' or '"')
            {
                var Quote = Character;
                var Closed = false;
                for (Index++; Index < End; Index++)
                {
                    if (Source[Index] == '\\')
                    {
                        Index++;
                    }
                    else if (Source[Index] == Quote)
                    {
                        Closed = true;
                        break;
                    }
                }
                if (!Closed)
                {
                    throw new InvalidDataException("STYLESHEET_STRING_UNCLOSED");
                }
                continue;
            }
            if (Character == '(')
            {
                Parentheses++;
            }
            else if (Character == ')')
            {
                Parentheses--;
            }
            if (Parentheses < 0)
            {
                throw new InvalidDataException("STYLESHEET_PARENTHESIS_INVALID");
            }
            if (Parentheses == 0 && Tokens.Contains(Character))
            {
                return Index;
            }
        }
        if (Parentheses != 0)
        {
            throw new InvalidDataException("STYLESHEET_PARENTHESIS_UNCLOSED");
        }
        return -1;
    }
}
