using System.Text.Json.Nodes;
using SharpEngine.UI;

namespace SharpEngine.Editor;

internal sealed partial class UiEditorDocument
{
    private Func<string, string>? ResolveGlobalStyle;
    private readonly List<UiPrivateStyle> ChildPrivateStyles = [];

    private UiEditorSnapshot CaptureSnapshot()
    {
        return new UiEditorSnapshot(Canonical(Document), SelectedKey, PrivateRcss, GlobalStyleSheets.ToArray());
    }

    private string AuthoringState()
    {
        return new JsonObject
        {
            ["rml"] = Canonical(Document),
            ["rcss"] = PrivateRcss,
            ["globals"] = new JsonArray(GlobalStyleSheets.Select(Value => (JsonNode)Value).ToArray()),
        }.ToJsonString();
    }

    internal void SetGlobalStyleResolver(Func<string, string> Resolver)
    {
        ResolveGlobalStyle = Resolver;
        RefreshDerivedState();
    }

    internal bool SetPrivateStyleValue(int RuleIndex, string Name, string Value, out string Message)
    {
        Message = string.Empty;
        if (Format != UiDocumentFormat.Package)
        {
            Message = "Private package styles require a .sui asset";
            return false;
        }
        try
        {
            var Field = UiStyleProperties.Find(Name);
            if (Field is not null && !UiStyleProperties.Validate(Name, Value, out Message))
            {
                return false;
            }
            var Updated = RcssSourceEditing.SetValue(PrivateRcss, RuleIndex, Name, Value);
            return Edit("private style:" + Name, () => PrivateRcss = Updated, out Message);
        }
        catch (InvalidDataException Error)
        {
            Message = Error.Message;
            return false;
        }
    }

    internal bool AddPrivateRule(string Selector, out string Message)
    {
        Message = string.Empty;
        var Valid = UiSelector.TryParse(Selector.Trim(), out _);
        if (Format != UiDocumentFormat.Package || !Valid)
        {
            Message = "Enter a tag, class, id, descendant or child selector for this UI";
            return false;
        }
        var Updated = PrivateRcss.TrimEnd() + "\n" + Selector.Trim() + " { color: #ffffff; }\n";
        return Edit("add private style rule", () => PrivateRcss = Updated, out Message);
    }

    internal bool SetGlobalStyleReference(string AssetId, bool Add, out string Message)
    {
        Message = string.Empty;
        if (Format != UiDocumentFormat.Package)
        {
            Message = "Shared style references require a .sui asset";
            return false;
        }
        if (Add)
        {
            try
            {
                _ = ResolveGlobalStyle?.Invoke(AssetId) ??
                    throw new InvalidDataException("UI_GLOBAL_STYLE_RESOLVER_REQUIRED: " + AssetId);
            }
            catch (Exception Error) when (Error is InvalidDataException or IOException)
            {
                Message = Error.Message;
                return false;
            }
        }
        var Updated = GlobalStyleSheets.ToList();
        if (Add ? Updated.Contains(AssetId, StringComparer.Ordinal) : !Updated.Contains(AssetId, StringComparer.Ordinal))
        {
            return false;
        }
        if (Add)
        {
            Updated.Add(AssetId);
        }
        else
        {
            Updated.Remove(AssetId);
        }
        return Edit("shared style reference", () => GlobalStyleSheets = Updated, out Message);
    }

    private IReadOnlyList<string> ResolvedGlobalStyles(List<UiEditorDiagnostic> Diagnostics)
    {
        var Files = new List<string>();
        foreach (var Id in GlobalStyleSheets)
        {
            try
            {
                Files.Add(ResolveGlobalStyle?.Invoke(Id) ??
                    throw new InvalidDataException("UI_GLOBAL_STYLE_RESOLVER_REQUIRED: " + Id));
            }
            catch (Exception Error) when (Error is InvalidDataException or IOException)
            {
                Diagnostics.Add(new UiEditorDiagnostic("error", "UI_GLOBAL_STYLE_INVALID", Error.Message));
            }
        }
        return Files;
    }
}
