using System.Runtime.InteropServices;
using SharpEngine.Editor;

namespace SharpEngine.Game;

public static class GameEntrypoint
{
    private static EditorRuntime? s_runtime;

    [UnmanagedCallersOnly]
    public static int Initialize(
        nint windowHandle,
        uint width,
        uint height,
        nint nativeApi)
    {
        try
        {
            s_runtime = new EditorRuntime(windowHandle, width, height, nativeApi);
            s_runtime.Initialize();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Game] Initialize failed: {exception}");
            s_runtime = null;
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    public static void Update(float deltaSeconds)
    {
        try
        {
            s_runtime?.Update(deltaSeconds);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Game] Update failed: {exception}");
        }
    }

    [UnmanagedCallersOnly]
    public static void Shutdown()
    {
        try
        {
            s_runtime?.Shutdown();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Game] Shutdown failed: {exception}");
        }
        finally
        {
            s_runtime = null;
        }
    }

    [UnmanagedCallersOnly]
    public static void PointerDown(nint windowHandle, int x, int y, uint button)
    {
        try
        {
            s_runtime?.PointerDown(windowHandle, x, y, button);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Game] PointerDown failed: {exception}");
        }
    }

    [UnmanagedCallersOnly]
    public static void InvokeUiAction(nint actionUtf8)
    {
        try
        {
            if (Marshal.PtrToStringUTF8(actionUtf8) is { } action)
                s_runtime?.InvokeUiAction(action);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Game] UI action failed: {exception}");
        }
    }

    [UnmanagedCallersOnly]
    public static void ViewportInput(nint windowHandle, int x, int y,
        uint inputEvent, uint buttons, int wheelDelta)
    {
        try
        {
            s_runtime?.ViewportInput(windowHandle, x, y, inputEvent, buttons, wheelDelta);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Game] ViewportInput failed: {exception}");
        }
    }

    [UnmanagedCallersOnly]
    public static void ViewportKey(uint key, byte pressed, byte viewportFocused)
    {
        try
        {
            s_runtime?.ViewportKey(key, pressed != 0, viewportFocused != 0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Game] ViewportKey failed: {exception}");
        }
    }

    [UnmanagedCallersOnly]
    public static int RunMcp()
    {
        try
        {
            return s_runtime is null ? -1 : McpEndpoint.Run(s_runtime);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Game] MCP failed: {exception}");
            return -1;
        }
    }
}
