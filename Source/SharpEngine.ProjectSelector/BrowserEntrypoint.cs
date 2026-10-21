using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Interop;
using SharpEngine.Logging;

namespace SharpEngine.ProjectSelector;

/// <summary>The existing native RmlUi host runs this tool without constructing an Editor.</summary>
public static unsafe class BrowserEntrypoint
{
    private sealed class WindowOwner(nint Value) : IWin32Window
    {
        public nint Handle
        {
            get
            {
                return Value;
            }
        }
    }

    private static NativeEngineApiTable* Api;
    private static nint Window;
    private static BrowserUiApplication? ApplicationState;
    private static readonly ConcurrentQueue<string> Actions = new();
    private static string? ObservationDirectory;
    private static ulong PublishedRevision;
    private static UiMcpSessionHost? Mcp;

    [UnmanagedCallersOnly]
    public static int Initialize(nint WindowHandle, uint Width, uint Height, nint NativeApi)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Window = WindowHandle;
            Api = (NativeEngineApiTable*)NativeApi;
            if (Api == null || Api->Version < 9 || Api->SetUiMarkup == null)
            {
                throw new InvalidOperationException("Project Browser requires the engine RmlUi bridge");
            }
            var EngineRoot = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT") ??
                throw new InvalidOperationException("SHARPENGINE_ROOT is required");
            var ProcessDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!;
            var Configuration = Directory.GetParent(ProcessDirectory)!.Name;
            var Browser = new ProjectBrowser(EngineRoot, Option("--browser-recent-file"));
            ApplicationState = new BrowserUiApplication(Browser, Configuration, PickProject, PickDirectory, Manifest =>
            {
                var Resolved = Browser.Resolve(Manifest);
                Program.OpenEditor([
                    "open-editor", "--project", Manifest, "--engine-root", Resolved.Engine.Root,
                    "--configuration", Configuration,
                ], null, true);
            });
            var Selected = Option("--browser-project");
            if (Selected is not null)
            {
                ApplicationState.Handle("browser.input.manifest:" + Selected);
                ApplicationState.Handle("browser.load-path");
            }
            ObservationDirectory = Option("--browser-observation");
            PublishedRevision = 0;
            SetMarkup(ApplicationState.Render());
            Mcp = new UiMcpSessionHost(NativeApi, "project-browser", DescribeState,
                CommandDefinition, ExecuteCommand);
            SharpLog.Info("ProjectBrowser", "RmlUi project selector ready; no EditorRuntime or project Modules initialized");
            return 0;
        }
        catch (Exception Error)
        {
            SharpLog.Error("ProjectBrowser", "Initialization failed", Error);
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    public static void Update(float DeltaSeconds)
    {
        try
        {
            var State = ApplicationState;
            if (State is null)
            {
                return;
            }
            var Before = State.Revision;
            Mcp?.Pump();
            var Render = State.Tick() || State.Revision != Before;
            while (Actions.TryDequeue(out var Action))
            {
                State.Handle(Action);
                Render |= !Action.StartsWith("browser.input.", StringComparison.Ordinal);
            }
            if (Render)
            {
                SetMarkup(State.Render());
            }
            if (ObservationDirectory is not null && PublishedRevision != State.Revision)
            {
                PublishObservation(State);
            }
            if (State.ShouldClose)
            {
                PostQuitMessage(0);
            }
        }
        catch (Exception Error)
        {
            SharpLog.Error("ProjectBrowser", "Update failed", Error);
            ObservationDirectory = null;
        }
    }

    [UnmanagedCallersOnly]
    public static void InvokeUiAction(byte* Action)
    {
        var Value = Marshal.PtrToStringUTF8((nint)Action);
        if (Value is not null)
        {
            Actions.Enqueue(Value);
        }
    }

    [UnmanagedCallersOnly]
    public static void InputEvent(byte* Host, uint Kind, uint Code, int X, int Y,
        float ValueX, float ValueY, uint Buttons, uint Modifiers, byte Handled, byte ViewportFocused)
    {
    }

    [UnmanagedCallersOnly]
    public static void PointerDown(int X, int Y)
    {
    }

    [UnmanagedCallersOnly]
    public static void ViewportInput(int X, int Y, uint Event, uint Buttons, int WheelDelta)
    {
    }

    [UnmanagedCallersOnly]
    public static void ViewportKey(uint Key, byte Down, byte Focused)
    {
    }

    [UnmanagedCallersOnly]
    public static int RunMcp() { return -1; }

    [UnmanagedCallersOnly]
    public static void Shutdown()
    {
        Mcp?.Dispose();
        Mcp = null;
        ApplicationState = null;
        Api = null;
        Window = 0;
        while (Actions.TryDequeue(out _))
        {
        }
    }

    private static string? Option(string Name)
    {
        var Arguments = Environment.GetCommandLineArgs();
        var Index = Array.IndexOf(Arguments, Name);
        return Index >= 0 && Index + 1 < Arguments.Length ? Arguments[Index + 1] : null;
    }

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int ExitCode);

    private static string? PickProject()
    {
        using var Dialog = new OpenFileDialog
        {
            Title = "选择 SharpEngine 项目",
            Filter = "SharpEngine 项目 (*.sharpproject)|*.sharpproject",
        };
        return Dialog.ShowDialog(new WindowOwner(Window)) == DialogResult.OK ? Dialog.FileName : null;
    }

    private static string? PickDirectory()
    {
        using var Dialog = new FolderBrowserDialog
        {
            Description = "选择新项目的保存目录",
            UseDescriptionForTitle = true,
        };
        return Dialog.ShowDialog(new WindowOwner(Window)) == DialogResult.OK ? Dialog.SelectedPath : null;
    }

    private static void SetMarkup(string Markup)
    {
        var Id = Encoding.UTF8.GetBytes("browser-content\0");
        var Bytes = Encoding.UTF8.GetBytes(Markup + "\0");
        fixed (byte* IdPointer = Id)
        fixed (byte* MarkupPointer = Bytes)
        {
            Api->SetUiMarkup(IdPointer, MarkupPointer);
        }
    }

    private static JsonNode DescribeState()
    {
        return JsonSerializer.SerializeToNode(ApplicationState!.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static JsonObject CommandDefinition()
    {
        return new JsonObject
        {
            ["name"] = "browser.command",
            ["description"] = "Preview, create or open a project through the same semantic commands as the engine Project Browser. Disk writes and process launch are external effects.",
            ["inputSchema"] = JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["operation","expectedRevision"],
                 "properties":{"operation":{"enum":["preview","show-create","create","open","close"]},
                 "expectedRevision":{"type":"integer","minimum":1},"path":{"type":"string"},
                 "name":{"type":"string"},"parent":{"type":"string"}}}
                """),
            ["_meta"] = new JsonObject
            {
                ["sharpengine/capability"] = new JsonObject
                {
                    ["available"] = ApplicationState?.IsBusy == false,
                    ["revisionPrecondition"] = "expectedRevision matches browser.describe.stateRevision",
                },
            },
        };
    }

    private static JsonNode ExecuteCommand(JsonObject Arguments)
    {
        var State = ApplicationState ?? throw new InvalidOperationException("Project Browser is not initialized");
        if (State.IsBusy || Arguments["expectedRevision"]!.GetValue<ulong>() != State.Revision)
        {
            throw new ArgumentException("BROWSER_REVISION_CONFLICT: Read browser.describe and retry after pending work completes");
        }
        var Operation = Arguments["operation"]!.GetValue<string>();
        string Required(string Name)
        {
            var Value = Arguments[Name]?.GetValue<string>();
            return !string.IsNullOrWhiteSpace(Value) ? Value : throw new ArgumentException($"Missing Browser field '{Name}'");
        }
        switch (Operation)
        {
            case "preview":
                State.Handle("browser.input.manifest:" + Required("path"));
                State.Handle("browser.load-path");
                break;
            case "show-create":
                State.Handle("browser.show-create");
                break;
            case "create":
                var Name = Required("name");
                var Parent = Required("parent");
                State.Handle("browser.input.name:" + Name);
                State.Handle("browser.input.parent:" + Parent);
                State.Handle("browser.create");
                break;
            case "open":
                State.Handle("browser.open");
                break;
            case "close":
                State.RequestClose();
                break;
            default:
                throw new ArgumentException($"Unknown Browser operation: {Operation}");
        }
        return DescribeState();
    }

    private static void PublishObservation(BrowserUiApplication State)
    {
        var Directory = Path.GetFullPath(ObservationDirectory!);
        System.IO.Directory.CreateDirectory(Directory);
        var Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        AtomicWrite(Path.Combine(Directory, "browser-state.json"), JsonSerializer.Serialize(State.Snapshot(), Options));
        var Size = Api->GetUiSnapshot(0, 0, null, 0);
        if (Size > 0)
        {
            var Bytes = new byte[Size];
            fixed (byte* Pointer = Bytes)
            {
                Api->GetUiSnapshot(0, 0, Pointer, Size);
            }
            AtomicWrite(Path.Combine(Directory, "browser-ui.json"), Encoding.UTF8.GetString(Bytes).TrimEnd('\0'));
        }
        PublishedRevision = State.Revision;
    }

    private static void AtomicWrite(string PathValue, string Value)
    {
        var Temporary = PathValue + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(Temporary, Value + Environment.NewLine, new UTF8Encoding(false));
            File.Move(Temporary, PathValue, true);
        }
        finally
        {
            File.Delete(Temporary);
        }
    }
}
