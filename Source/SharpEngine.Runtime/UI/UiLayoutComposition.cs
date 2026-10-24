using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.Assets;

namespace SharpEngine.UI;

/// <summary>A fixed, serialized use of another UI template. Values belong to this use.</summary>
public sealed record UiLayoutUse(string Id, string AssetId, JsonObject Values)
{
    public static UiLayoutUse Read(XElement Element)
    {
        var Id = (string?)Element.Attribute("id");
        var AssetId = (string?)Element.Attribute("asset");
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(AssetId) ||
            !AssetId.Contains("://", StringComparison.Ordinal) || Element.HasElements ||
            Element.Nodes().OfType<XText>().Any(Text => !string.IsNullOrWhiteSpace(Text.Value)))
        {
            throw new InvalidDataException("UILAYOUT_USE_INVALID: ui-use requires id, asset URI and no child elements");
        }
        string[] Allowed =
        [
            "id", "asset", "values", "class", "style", "data-ui-name",
        ];
        if (Element.Attributes().Any(Attribute => !UiAuthoringMetadata.IsAttribute(Attribute.Name.LocalName) &&
            !Allowed.Contains(Attribute.Name.LocalName, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("UILAYOUT_USE_ATTRIBUTE_UNKNOWN: " + Id);
        }
        var Values = ReadValues((string?)Element.Attribute("values"));
        return new UiLayoutUse(Id, AssetId, Values);
    }

    public static JsonObject ReadValues(string? Source)
    {
        if (string.IsNullOrWhiteSpace(Source))
        {
            return new JsonObject();
        }
        try
        {
            return JsonNode.Parse(Source) as JsonObject ??
                throw new InvalidDataException("UILAYOUT_VALUES_INVALID: expected a JSON object");
        }
        catch (System.Text.Json.JsonException Error)
        {
            throw new InvalidDataException("UILAYOUT_VALUES_INVALID: " + Error.Message, Error);
        }
    }
}

public static class UiLayoutAssets
{
    public static Func<string, UiLayoutTemplate> CreateResolver(AssetRegistry Assets,
        IReadOnlyDictionary<string, string>? SourceOverrides = null)
    {
        return AssetId =>
        {
            var Asset = Assets.Find(AssetId);
            var PathValue = Asset is null ? null : Assets.ResolvePath(Asset);
            if (Asset is null || !Asset.Type.Equals("UiDocument", StringComparison.OrdinalIgnoreCase) || PathValue is null)
            {
                throw new InvalidDataException("UILAYOUT_ASSET_INVALID: " + AssetId);
            }
            var FullPath = Path.GetFullPath(PathValue);
            var Source = SourceOverrides?.GetValueOrDefault(FullPath);
            var Package = Source is null ? UiDocumentAsset.Load(FullPath) :
                UiDocumentAsset.Parse(Source, FullPath);
            if (!Package.IsTemplate)
            {
                throw new InvalidDataException("UILAYOUT_ASSET_TEMPLATE_REQUIRED: " + AssetId);
            }
            return UiLayoutTemplate.FromAsset(Package, FullPath);
        };
    }
}

internal static class UiLayoutComposition
{
    internal sealed class ComposedNode
    {
    }
    internal static XElement Expand(XElement Source, UiLayoutContext Context,
        IReadOnlyDictionary<string, IUiLayoutPart>? Parts,
        Func<string, UiLayoutTemplate>? ResolveAsset, HashSet<string> Stack)
    {
        var Definition = new XElement(Source);
        foreach (var Element in Definition.Descendants("ui-use").ToArray())
        {
            var Use = UiLayoutUse.Read(Element);
            if (Element.Ancestors("ui-repeat").Any())
            {
                throw new InvalidDataException("UILAYOUT_USE_IN_REPEAT_UNSUPPORTED: " + Use.Id);
            }
            if (!Stack.Add(Use.AssetId))
            {
                throw new InvalidDataException("UILAYOUT_ASSET_CYCLE: " + Use.AssetId);
            }
            try
            {
                var Template = ResolveAsset?.Invoke(Use.AssetId) ??
                    throw new InvalidDataException("UILAYOUT_ASSET_RESOLVER_REQUIRED: " + Use.AssetId);
                var Part = Parts?.GetValueOrDefault(Use.Id);
                var Markup = Part is null
                    ? Template.RenderCore(Context, Use.Values, null, ResolveAsset, Stack)
                    : Part.BuildLayout(Context);
                var Wrapper = new XElement("div", new XAttribute("id", Use.Id));
                foreach (var Attribute in Element.Attributes().Where(Attribute =>
                             Attribute.Name.LocalName is "class" or "style" || UiAuthoringMetadata.IsAttribute(Attribute.Name.LocalName)))
                {
                    Wrapper.SetAttributeValue(Attribute.Name, Attribute.Value);
                }
                var Fragment = XElement.Parse("<fragment>" + Markup + "</fragment>");
                Wrapper.Add(Fragment.Nodes());
                Wrapper.AddAnnotation(new ComposedNode());
                Element.ReplaceWith(Wrapper);
            }
            finally
            {
                Stack.Remove(Use.AssetId);
            }
        }
        return Definition;
    }
}
