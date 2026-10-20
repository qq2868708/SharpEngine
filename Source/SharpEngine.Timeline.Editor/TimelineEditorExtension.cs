using SharpEngine.Editor;

namespace SharpEngine.Timeline;

internal sealed class TimelineEditorExtension : IEditorExtension
{
    private readonly List<IDisposable> Registrations = [];

    public void Startup(EditorContext Context)
    {
        Registrations.Add(Context.AssetOpeners.Register(new EditorAssetOpener(
            "engine.skeletal-animation-editor", "SkeletalAnimation",
            OpenAsset, Priority: -100, EditorId: "timeline-editor")));
        Registrations.Add(Context.AssetOpeners.Register(new EditorAssetOpener(
            "engine.timeline-editor", "Timeline", OpenAsset, Priority: -100,
            EditorId: "timeline-editor")));
        Registrations.Add(Context.AssetOpeners.Register(new EditorAssetOpener(
            "engine.animation-state-machine-editor", "AnimationStateMachine",
            OpenAsset, Priority: -100, EditorId: "timeline-editor")));
    }

    public void Shutdown()
    {
        for (var Index = Registrations.Count - 1; Index >= 0; Index--)
        {
            Registrations[Index].Dispose();
        }

        Registrations.Clear();
    }

    private static IAssetEditorInstance OpenAsset(EditorAssetOpenContext Context) =>
        new TimelineAssetEditorInstance(Context);
}
