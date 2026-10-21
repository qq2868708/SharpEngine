using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

internal static class BrowserLayoutValidation
{
    internal static void Run(McpClient Client, int ProcessId, string Evidence, Action<bool, string> Check)
    {
        nint Window = 0;
        EnumWindows((Handle, Parameter) =>
        {
            GetWindowThreadProcessId(Handle, out var Owner);
            if (Owner != ProcessId)
            {
                return true;
            }
            var ClassName = new StringBuilder(128);
            GetClassName(Handle, ClassName, ClassName.Capacity);
            if (ClassName.ToString() != "SelfEngineWindow")
            {
                return true;
            }
            Window = Handle;
            return false;
        }, 0);
        Check(Window != 0, "The isolated Project Browser owns a native window");
        (int Width, int Height)[] Sizes = [(1100, 760), (1100, 417), (640, 480), (420, 360), (1100, 760)];
        string[] HeaderIds = ["browser-new", "browser-browse", "browser-path-input", "browser-load-path"];
        foreach (var Size in Sizes)
        {
            var (Width, Height) = Size;
            Check(SetWindowPos(Window, 0, 0, 0, Width, Height, 0x0016),
                "Resize the isolated browser without activating another application");
            Thread.Sleep(200);
            var Snapshot = Client.Ui();
            Client.Capture(Evidence, $"browser-{Width}x{Height}-before-scroll", "main");
            foreach (var Id in HeaderIds)
            {
                var Element = Client.Element(Snapshot, "main", Id);
                var Bounds = Element["bounds"]!;
                Check(Element["visible"]!.GetValue<bool>() &&
                    Bounds["x"]!.GetValue<double>() >= 0 &&
                    Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>() <= Width &&
                    Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>() <= Height,
                    $"{Id} stays inside the {Width}x{Height} browser");
            }
            var Open = Client.Element(Snapshot, "main", "browser-open");
            for (var Attempt = 0; Attempt < 10 && !Inside(Open, Width, Height); Attempt++)
            {
                var Response = Client.Call("tools/call", new JsonObject
                {
                    ["name"] = "ui.pointer",
                    ["arguments"] = new JsonObject
                    {
                        ["hostId"] = "main",
                        ["event"] = "wheel",
                        ["x"] = Width - 70,
                        ["y"] = Height - 75,
                        ["wheelY"] = 4,
                        ["expectedFrameId"] = Snapshot["frameId"]!.DeepClone(),
                    },
                });
                var Result = Response["structuredContent"]!.AsObject();
                if (Result["diagnostics"]?.AsArray().FirstOrDefault()?["code"]?.GetValue<string>() == "UI_FRAME_STALE")
                {
                    Snapshot = Client.Ui();
                    continue;
                }
                Check(Result["accepted"]!.GetValue<bool>(), "Real wheel input reaches the browser details");
                Snapshot = Client.Ui();
                Open = Client.Element(Snapshot, "main", "browser-open");
            }
            Check(Inside(Open, Width, Height), $"Open Project remains reachable at {Width}x{Height}");
            Client.Capture(Evidence, $"browser-{Width}x{Height}", "main");
            Client.Click("main", "browser-new");
            Snapshot = Client.Ui();
            Check(Client.Element(Snapshot, "main", "browser-name-input")["bounds"]!["width"]!.GetValue<double>() > 80,
                "The create form retains usable input width");
            Client.Tool("browser.command", new JsonObject
            {
                ["operation"] = "preview",
                ["expectedRevision"] = Client.Tool("browser.describe")["stateRevision"]!.DeepClone(),
                ["path"] = Client.Tool("browser.describe")["selectedProject"]!["manifestPath"]!.DeepClone(),
            });
        }
    }

    private static bool Inside(JsonObject Element, int Width, int Height)
    {
        var Bounds = Element["bounds"]!;
        var Clip = Element["clip"];
        var Bottom = Bounds["y"]!.GetValue<double>() + Bounds["height"]!.GetValue<double>();
        return Element["visible"]!.GetValue<bool>() && Bottom <= Height &&
            Bounds["x"]!.GetValue<double>() + Bounds["width"]!.GetValue<double>() <= Width &&
            (Clip is null || Bottom <= Clip["y"]!.GetValue<double>() + Clip["height"]!.GetValue<double>() + 1);
    }

    private delegate bool WindowCallback(nint Window, nint Parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback Callback, nint Parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint Window, out uint ProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint Window, StringBuilder Name, int Capacity);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint Window, nint InsertAfter, int X, int Y, int Width, int Height, uint Flags);
}
