using System.Runtime.InteropServices;
using SharpEngine.Interop;
using SharpEngine.Logging;
using SharpEngine.Timeline;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Host;

// Compiled only into Game Target. No reference to the Editor assembly exists.
public static unsafe class HostEntrypoint
{
    private static GameSession? Game;
    private static NativeEngineApiTable* Api;
    private static int Frames;
    private static GameMcpHost? Mcp;

    [UnmanagedCallersOnly]
    public static int Initialize(nint Window, uint Width, uint Height, nint NativeApi)
    {
        try
        {
            Api = (NativeEngineApiTable*)NativeApi;
            Game = new GameSession(Environment.GetEnvironmentVariable("SHARPENGINE_ROOT")!,
                Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST") ?? "",
                typeof(TimelineRuntimeModule).Assembly);
            SharpLog.Info("MCP", $"Game UI API version={Api->Version}, available={Api->GetUiSnapshot != null}");
            if (Api->Version >= 9 && Api->GetUiSnapshot != null)
            {
                Mcp = new GameMcpHost(NativeApi);
            }
            return 0;
        }
        catch (Exception Error)
        {
            SharpLog.Critical("Game", "Initialization failed", Error);
            SharpLog.Shutdown();
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    public static void Update(float Seconds)
    {
        try
        {
            if (Game is null || Api == null)
            {
                return;
            }
            Mcp?.Pump();
            Game.Update(Seconds);
            float X = 0, Y = 0, Width = 0, Height = 0;
            if (Api->GetViewportRect(&X, &Y, &Width, &Height) == 0)
            {
                return;
            }
            var Frame = SceneRenderer.BuildFrame(Game.World, new UiRect(X, Y, Width, Height), Game.Assets);
            fixed (WorldViewportVertex* Triangles = Frame.Triangles)
            fixed (WorldViewportVertex* Lines = Frame.Lines)
            {
                Api->SubmitViewportFrame((float*)Triangles, (uint)Frame.Triangles.Length,
                    (float*)Lines, (uint)Frame.Lines.Length);
            }
            if (++Frames == 1)
            {
                SharpLog.Info("Game", $"First runtime frame: {Frame.TriangleCount} triangles",
                    new Dictionary<string, string>
                    {
                        ["triangleCount"] = Frame.TriangleCount.ToString(),
                        ["editorAssemblyLoaded"] = AppDomain.CurrentDomain.GetAssemblies()
                            .Any(Assembly => Assembly.GetName().Name == "SharpEngine.Editor").ToString(),
                    });
            }
        }
        catch (Exception Error)
        {
            SharpLog.Critical("Game", "Runtime failed", Error);
            try
            {
                Game?.Dispose();
            }
            finally
            {
                Environment.Exit(5);
            }
        }
    }

    [UnmanagedCallersOnly]
    public static void InputEvent(nint Host, uint Kind, uint Code, int X, int Y,
        float ValueX, float ValueY, uint Buttons, uint Modifiers, byte Handled, byte Focused)
    {
        if (Game is null || !Enum.IsDefined(typeof(NativeInputEventKind), Kind))
        {
            return;
        }
        try
        {
            Game.Input.ProcessNative("game", (NativeInputEventKind)Kind, Code,
                X, Y, ValueX, ValueY, Modifiers, Handled != 0, Focused != 0);
        }
        catch (Exception Error)
        {
            SharpLog.Error("Input", "Input event failed", Error);
        }
    }

    [UnmanagedCallersOnly]
    public static int RunMcp()
    {
        try
        {
            return GameMcpHost.RunBridge();
        }
        catch (Exception Error)
        {
            Console.Error.WriteLine(Error.Message);
            return 2;
        }
    }

    [UnmanagedCallersOnly]
    public static void Shutdown()
    {
        try
        {
            Mcp?.Dispose();
            Mcp = null;
            Game?.Dispose();
        }
        finally
        {
            SharpLog.Info("Game", $"Stopped after {Frames} frames",
                new Dictionary<string, string>
                {
                    ["frames"] = Frames.ToString(),
                });
            Game = null;
            Api = null;
            SharpLog.Shutdown();
        }
    }
}
