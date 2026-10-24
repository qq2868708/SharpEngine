using System.Text.Json.Nodes;

namespace SharpEngine.Editor;

/// <summary>Current authoring input. Preview factories must not modify or save it.</summary>
public sealed record EditorUiPreviewSource(string AssetId, string SourcePath,
    string SourceText, long Revision, long DependencyRevision = 0);

public sealed record EditorUiPreviewDocument(string Rml, string SourcePath,
    float Width = 1280, float Height = 720);

/// <summary>One editor-owned use of project UI code, independent of Game and World state.</summary>
public interface IEditorUiPreview : IDisposable
{
    EditorUiPreviewDocument BuildDocument(EditorUiPreviewSource Source);
    bool TryHandleAction(string Action);
    JsonObject Describe();
}

public static class EditorUiAssetEditor
{
    public static IAssetEditorInstance Create(EditorAssetOpenContext Context,
        IEditorUiPreview Preview)
    {
        ArgumentNullException.ThrowIfNull(Preview);
        return new UiAssetEditorInstance(Context, Preview);
    }
}

/// <summary>Optional native document presentation, isolated from the editor's stylesheet.</summary>
public interface IAssetEditorDocumentPreview
{
    string PreviewElementId
    {
        get;
    }

    EditorUiPreviewDocument? BuildPreviewDocument();
}
