using System.Runtime.InteropServices;
using SharpEngine.Runtime.Interop;
using SharpEngine.Timeline;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Host;

// Compiled only into Game Target. No reference to the Editor assembly exists.
public static unsafe class HostEntrypoint
{
    private static GameSession? s_game;
    private static NativeEngineApiTable* s_api;
    private static int s_frames;

    [UnmanagedCallersOnly]
    public static int Initialize(nint window, uint width, uint height, nint api)
    {
        try
        {
            s_api = (NativeEngineApiTable*)api;
            s_game = new GameSession(Environment.GetEnvironmentVariable("SHARPENGINE_ROOT")!,
                Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST") ?? "",
                typeof(TimelineRuntimeModule).Assembly);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return -1; }
    }

    [UnmanagedCallersOnly]
    public static void Update(float seconds)
    {
        try
        {
            if (s_game is null || s_api == null) return;
            s_game.Update(seconds);
            float x = 0, y = 0, width = 0, height = 0;
            if (s_api->GetViewportRect(&x, &y, &width, &height) == 0) return;
            var frame = SceneRenderer.BuildFrame(s_game.World, new UiRect(x, y, width, height), s_game.Assets);
            fixed (WorldViewportVertex* triangles = frame.Triangles)
            fixed (WorldViewportVertex* lines = frame.Lines)
                s_api->SubmitViewportFrame((float*)triangles, (uint)frame.Triangles.Length,
                    (float*)lines, (uint)frame.Lines.Length);
            if (++s_frames == 1)
                Console.Error.WriteLine($"[Game] First runtime frame: {frame.TriangleCount} triangles; editorAssemblyLoaded={AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "SharpEngine.Editor")}");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[Game] Runtime failed: {error}");
            try { s_game?.Dispose(); }
            finally { Environment.Exit(5); }
        }
    }

    [UnmanagedCallersOnly]
    public static void InputEvent(nint host, uint kind, uint code, int x, int y,
        float valueX, float valueY, uint buttons, uint modifiers, byte handled, byte focused)
    {
        if (s_game is null || !Enum.IsDefined(typeof(NativeInputEventKind), kind)) return;
        try { s_game.Input.ProcessNative("game", (NativeInputEventKind)kind, code,
            x, y, valueX, valueY, modifiers, handled != 0, focused != 0); }
        catch (Exception error) { Console.Error.WriteLine(error); }
    }

    [UnmanagedCallersOnly]
    public static void Shutdown()
    {
        try { s_game?.Dispose(); }
        finally { s_game = null; s_api = null; Console.Error.WriteLine($"[Game] Stopped after {s_frames} frames"); }
    }
}
