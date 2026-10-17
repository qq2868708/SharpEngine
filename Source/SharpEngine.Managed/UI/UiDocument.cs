using SharpEngine.UI;

namespace SharpEngine.Game;

/// <summary>
/// Compatibility facade for the original game entrypoint. New code should use
/// SharpEngine.UI.UiDocumentModel directly; keeping this facade avoids forcing
/// the managed host and existing game lifecycle to change at once.
/// </summary>
internal sealed class UiDocument
{
    private UiDocument(UiDocumentModel model, string title)
    {
        Model = model;
        SourcePath = model.SourcePath;
        NodeCount = model.NodeCount;
        Title = title;
    }

    public UiDocumentModel Model { get; }
    public string SourcePath { get; }
    public int NodeCount { get; }
    public string Title { get; }

    public static UiDocument Load(string path)
    {
        var model = UiDocumentModel.Load(path);
        var title = model.Root.DescendantsAndSelf()
            .Where(node => node.Kind == UiNodeKind.Text)
            .Select(node => node.Text)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? "SharpEngine";
        return new UiDocument(model, title);
    }
}
