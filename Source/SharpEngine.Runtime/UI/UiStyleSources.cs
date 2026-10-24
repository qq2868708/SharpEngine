using System.Text.RegularExpressions;
using SharpEngine.Assets;

namespace SharpEngine.UI;

public sealed record UiPrivateStyle(string SourcePath, string Rcss);

public static class GlobalStyleSheetAsset
{
    public const string Type = "GlobalStyleSheet";
    public const string FileSuffix = ".sglobalcss";
    public const string Importer = "RmlUi.GlobalStyleSheet";

    public static Func<string, string> CreateResolver(AssetRegistry Assets)
    {
        return AssetId =>
        {
            var Asset = Assets.Find(AssetId);
            var PathValue = Asset is null ? null : Assets.ResolvePath(Asset);
            if (Asset is null || Asset.Type != Type || PathValue is null ||
                !PathValue.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase) || !File.Exists(PathValue))
            {
                throw new InvalidDataException("UI_GLOBAL_STYLE_INVALID: " + AssetId);
            }
            return Path.GetFullPath(PathValue);
        };
    }
}

/// <summary>Derived style inputs. Shared styles precede private styles; discovery order is stable.</summary>
public sealed class UiStyleSources
{
    private readonly List<string> FilesValue = [];
    private readonly List<UiPrivateStyle> PrivateValue = [];
    private readonly HashSet<string> SeenGlobals = new(StringComparer.Ordinal);
    private readonly HashSet<string> SeenPrivate = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Files
    {
        get
        {
            return FilesValue;
        }
    }
    public IReadOnlyList<UiPrivateStyle> PrivateStyles
    {
        get
        {
            return PrivateValue;
        }
    }

    public void Add(UiDocumentAsset Package, string SourcePath, Func<string, string>? ResolveGlobal)
    {
        foreach (var AssetId in Package.GlobalStyleSheets)
        {
            if (SeenGlobals.Add(AssetId))
            {
                var PathValue = ResolveGlobal?.Invoke(AssetId) ??
                    throw new InvalidDataException("UI_GLOBAL_STYLE_RESOLVER_REQUIRED: " + AssetId);
                AddFile(PathValue);
            }
        }
        var Folder = Path.GetDirectoryName(SourcePath)!;
        foreach (var Href in Package.StyleSheets)
        {
            AddFile(Path.GetFullPath(Path.Combine(Folder, Href)));
        }
        if (Package.Rcss.Length != 0 && SeenPrivate.Add(SourcePath))
        {
            PrivateValue.Add(new UiPrivateStyle(SourcePath, Package.Rcss));
        }
    }

    private void AddFile(string PathValue)
    {
        if (!FilesValue.Contains(PathValue, StringComparer.OrdinalIgnoreCase))
        {
            FilesValue.Add(PathValue);
        }
    }

    public static string RebaseUrls(string Rcss, string SourcePath)
    {
        var Folder = Path.GetDirectoryName(SourcePath)!;
        const string Pattern = """
            /\*[\s\S]*?\*/|'(?:\\.|[^'\\])*'|"(?:\\.|[^"\\])*"|(?<Url>\burl\(\s*(?:"(?<Double>(?:\\.|[^"\\])*)"|'(?<Single>(?:\\.|[^'\\])*)'|(?<Bare>[^)\s]+))\s*\))
            """;
        return Regex.Replace(Rcss, Pattern, Match =>
        {
            if (!Match.Groups["Url"].Success)
            {
                return Match.Value;
            }
            var Value = Match.Groups["Double"].Success ? Match.Groups["Double"].Value :
                Match.Groups["Single"].Success ? Match.Groups["Single"].Value : Match.Groups["Bare"].Value;
            Value = Value.Trim();
            if (Value.Contains(':') || Value.StartsWith('/') || Value.StartsWith('#'))
            {
                return Match.Value;
            }
            return "url(\"" + Path.GetFullPath(Path.Combine(Folder, Value)).Replace('\\', '/') + "\")";
        }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
