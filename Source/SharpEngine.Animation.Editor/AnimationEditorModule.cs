using SharpEngine.Editor;
using SharpEngine.Modules;

[assembly: SharpEngineModule("SharpEngine.Animation.Editor", "Editor")]

namespace SharpEngine.Animation;

public sealed class AnimationEditorModule : ISharpEngineModule
{
    private IDisposable? SkeletalAnimationRegistration;
    private IDisposable? StateMachineRegistration;

    public void Startup(ModuleContext Context)
    {
        var Editor = Context as EditorModuleContext;
        if (Editor is not null)
        {
            SkeletalAnimationRegistration = Editor.RegisterEditorExtension(new SkeletalAnimationEditorExtension());
            StateMachineRegistration = Editor.RegisterEditorExtension(new AnimationStateMachineEditorExtension());
        }
    }

    public void Shutdown()
    {
        StateMachineRegistration?.Dispose();
        StateMachineRegistration = null;
        SkeletalAnimationRegistration?.Dispose();
        SkeletalAnimationRegistration = null;
    }
}
