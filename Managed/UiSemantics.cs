using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpEngine.UI;

/// <summary>
/// Stable, renderer independent description of the interactive part of a UI.
/// RmlUi, a future Vulkan renderer, and editor automation can all consume this
/// without depending on the current GDI draw list.
/// </summary>
public sealed record UiSemanticElement(
    string Id,
    string Role,
    string Label,
    string? Action,
    bool Enabled,
    bool Visible,
    UiRect Bounds);

public sealed class UiSemanticSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private UiSemanticSnapshot(string source, IReadOnlyList<UiSemanticElement> elements)
    {
        Source = source;
        Elements = elements;
    }

    public string Source { get; }
    public IReadOnlyList<UiSemanticElement> Elements { get; }

    public static UiSemanticSnapshot Create(UiDocumentModel document,
        UiBindingContext? bindings = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var elements = document.Root.DescendantsAndSelf()
            .Where(node => node.Kind != UiNodeKind.Root && node.Visible &&
                node.IsInteractive)
            .Select(node => new UiSemanticElement(
                node.Id,
                node.GetAttribute("role", DefaultRole(node.Kind)),
                bindings?.ResolveText(node) ?? node.Text ?? node.Id,
                node.ActionName,
                node.Enabled,
                node.Visible,
                node.Bounds))
            .ToArray();
        return new UiSemanticSnapshot(document.SourcePath, elements);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    private static string DefaultRole(UiNodeKind kind) => kind switch
    {
        UiNodeKind.Button => "button",
        UiNodeKind.Input => "textbox",
        _ => "generic",
    };
}

