using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.Assets;

namespace SharpEngine.UI;

/// <summary>Pure screen composition shared by runtime hosts and isolated editor previews.</summary>
public static class UiScreenComposer
{
    public static string Compose(UiDocumentAsset Package, UiScreenRequest Request,
        Func<string, UiLayoutTemplate>? ResolveAsset = null,
        Func<string, string>? ResolveGlobalStyle = null, string? SourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(Package);
        ArgumentNullException.ThrowIfNull(Request);
        SourcePath ??= Path.Combine(Environment.CurrentDirectory, "screen.sui");
        var Template = Package.IsTemplate ? UiLayoutTemplate.Parse(Package.Rml) : null;
        var Styles = new UiStyleSources();
        Styles.Add(Package, SourcePath, ResolveGlobalStyle);
        var Visited = new HashSet<string>(StringComparer.Ordinal);
        var Visiting = new HashSet<string>(StringComparer.Ordinal);
        UiLayoutTemplate Resolve(string AssetId)
        {
            if (!Visiting.Add(AssetId))
            {
                throw new InvalidDataException("UILAYOUT_ASSET_CYCLE: " + AssetId);
            }
            var Child = ResolveAsset?.Invoke(AssetId) ??
                throw new InvalidDataException("UILAYOUT_ASSET_RESOLVER_REQUIRED: " + AssetId);
            if (Visited.Add(AssetId))
            {
                if (Child.Package is not null && Child.SourcePath is not null)
                {
                    Styles.Add(Child.Package, Child.SourcePath, ResolveGlobalStyle);
                }
                foreach (var Use in Child.Uses)
                {
                    _ = Resolve(Use.AssetId);
                }
            }
            Visiting.Remove(AssetId);
            return Child;
        }
        var Body = Package.IsTemplate ? Template!.Render(new UiLayoutContext(Request.Width, Request.Height, Package.Name),
            Request.Values ?? new JsonObject(), Request.Parts, Resolve) : string.Empty;
        var Builder = new StringBuilder("<rml><head>");
        foreach (var Href in Styles.Files)
        {
            if (Href.EndsWith(GlobalStyleSheetAsset.FileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                Builder.Append("<style>").Append(UiStyleSources.RebaseUrls(File.ReadAllText(Href), Href)).Append("</style>");
            }
            else
            {
                Builder.Append("<link type=\"text/rcss\" href=\"")
                    .Append(WebUtility.HtmlEncode(Href)).Append("\" />");
            }
        }
        foreach (var Style in Styles.PrivateStyles)
        {
            var Css = UiStyleSources.RebaseUrls(Style.Rcss, Style.SourcePath);
            Builder.Append("<style>").Append(Css).Append("</style>");
        }
        if (Package.IsTemplate)
        {
            return Builder.Append("</head><body>").Append(Body).Append("</body></rml>").ToString();
        }
        var Document = XDocument.Parse(Package.Rml, LoadOptions.PreserveWhitespace);
        var Head = Document.Root!.Element("head");
        if (Head is null)
        {
            Head = new XElement("head");
            Document.Root.AddFirst(Head);
        }
        foreach (var Href in Styles.Files.AsEnumerable().Reverse())
        {
            if (!Href.EndsWith(GlobalStyleSheetAsset.FileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                Head.AddFirst(new XElement("link", new XAttribute("type", "text/rcss"), new XAttribute("href", Href)));
            }
        }
        var Replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var GlobalStyles = Styles.Files.Where(FileValue => FileValue.EndsWith(GlobalStyleSheetAsset.FileSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(FileValue => new UiPrivateStyle(FileValue, File.ReadAllText(FileValue)));
        foreach (var Style in GlobalStyles.Concat(Styles.PrivateStyles))
        {
            var Marker = "__SHARPENGINE_PRIVATE_STYLE_" + Replacements.Count + "__";
            while (Package.Rml.Contains(Marker, StringComparison.Ordinal))
            {
                Marker += "_";
            }
            Replacements[Marker] = UiStyleSources.RebaseUrls(Style.Rcss, Style.SourcePath);
            Head.Add(new XElement("style", Marker));
        }
        var Output = Document.ToString(SaveOptions.DisableFormatting);
        foreach (var Pair in Replacements)
        {
            Output = Output.Replace(Pair.Key, Pair.Value, StringComparison.Ordinal);
        }
        return Output;
    }
}
