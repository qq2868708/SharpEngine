using System.Runtime.InteropServices;
using System.Diagnostics;
using SharpEngine.Interop;
using SharpEngine.Input;
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
    private static NativeGpuSceneBridge? GpuSceneBridge;
    private static RenderTelemetry? RenderTelemetryService;
    private static bool GpuFallbackReported;
    private static int Frames;
    private static UiMcpSessionHost? Mcp;
    private static IDisposable? ReleasePointerBinding;

    [UnmanagedCallersOnly]
    public static int Initialize(nint Window, uint Width, uint Height, nint NativeApi)
    {
        try
        {
            Api = (NativeEngineApiTable*)NativeApi;
            GpuSceneBridge = new NativeGpuSceneBridge(NativeApi);
            RenderTelemetryService = new RenderTelemetry(NativeApi);
            Game = new GameSession(Environment.GetEnvironmentVariable("SHARPENGINE_ROOT")!,
                Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST") ?? "",
                typeof(TimelineRuntimeModule).Assembly);
            ReleasePointerBinding = Game.Input.BindAction(InputRoute.Gameplay,
                "engine://input/actions/player.release-pointer", int.MaxValue, Event =>
                {
                    if (Event.Phase == InputActionPhase.Performed && Api->Version >= 17 &&
                        Api->SetGameplayInputCapture != null)
                    {
                        Api->SetGameplayInputCapture(0);
                    }
                    return true;
                });
            SharpLog.Info("MCP", $"Game UI API version={Api->Version}, available={Api->GetUiSnapshot != null}");
            if (Api->Version >= 9 && Api->GetUiSnapshot != null)
            {
                Mcp = new UiMcpSessionHost(NativeApi, "game",
                    () => RenderTelemetryService.Describe(Game?.Assets));
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
            var Captured = Api->Version >= 17 && Api->GetGameplayInputCapture != null &&
                Api->GetGameplayInputCapture() != 0;
            Game.Input.ObserveGameplayCapture("game", Captured);
            Game.Input.SetRouteEnabled(InputRoute.Gameplay, Captured);
            Game.Update(Seconds);
            float X = 0, Y = 0, Width = 0, Height = 0;
            if (Api->GetViewportRect(&X, &Y, &Width, &Height) == 0)
            {
                return;
            }
            var Bounds = new UiRect(X, Y, Width, Height);
            var SceneStart = Stopwatch.GetTimestamp();
            var GpuSubmitted = false;
            var TriangleCount = 0;
            if (GpuSceneBridge?.Available == true)
            {
                var GpuFrame = GpuSceneBuilder.BuildFrame(Game.World, Bounds,
                    Game.Assets);
                GpuSubmitted = GpuSceneBridge.Submit(GpuFrame, Height);
                if (GpuSubmitted)
                {
                    TriangleCount = GpuFrame.Instances.Sum(Instance =>
                        Instance.Mesh.Vertices.Count / 3);
                    Api->SubmitViewportFrame(null, 0, null, 0);
                }
                else if (!GpuFallbackReported)
                {
                    GpuFallbackReported = true;
                    SharpLog.Warning("Render",
                        "GPU scene submission failed; using visible CPU fallback");
                }
            }
            if (!GpuSubmitted)
            {
                var Frame = SceneRenderer.BuildFrame(Game.World, Bounds, Game.Assets);
                TriangleCount = Frame.TriangleCount;
                fixed (WorldViewportVertex* Triangles = Frame.Triangles)
                fixed (WorldViewportVertex* Lines = Frame.Lines)
                {
                    Api->SubmitViewportFrame((float*)Triangles,
                        (uint)Frame.Triangles.Length, (float*)Lines,
                        (uint)Frame.Lines.Length);
                }
            }
            RenderTelemetryService?.Record(Seconds,
                Stopwatch.GetElapsedTime(SceneStart).TotalMilliseconds,
                GpuSceneBridge?.Available == true, GpuSubmitted);
            if (++Frames == 1)
            {
                SharpLog.Info("Game", $"First runtime frame: {TriangleCount} triangles",
                    new Dictionary<string, string>
                    {
                        ["triangleCount"] = TriangleCount.ToString(),
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
            if (Kind == (uint)NativeInputEventKind.PointerDown && Code == 0 && Handled == 0 &&
                Api != null && Api->Version >= 17 && Api->SetGameplayInputCapture != null)
            {
                Api->SetGameplayInputCapture(1);
            }
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
            return UiMcpSessionHost.RunBridge("game");
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
            ReleasePointerBinding?.Dispose();
            ReleasePointerBinding = null;
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
            GpuSceneBridge = null;
            RenderTelemetryService = null;
            GpuFallbackReported = false;
            SharpLog.Shutdown();
        }
    }
}
