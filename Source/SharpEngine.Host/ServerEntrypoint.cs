using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Hosting;
using SharpEngine.Logging;
using SharpEngine.Modules;
using SharpEngine.Networking;
using SharpEngine.Subsystems;
using SharpEngine.World;

namespace SharpEngine.Host;

/// <summary>Dedicated Server entry point. It never creates a window or renderer.</summary>
public static class HostEntrypoint
{
    private static DedicatedServerSession? Session;
    private static DedicatedServerNetworkHost? NetworkHost;
    private static ProjectModuleHost? ProjectModules;
    private static EngineModuleHost? EngineModules;
    private static int Updates;
    private static int CommandExecutions;

    [UnmanagedCallersOnly]
    public static int Initialize(nint Window, uint Width, uint Height, nint NativeApi)
    {
        try
        {
            var EngineRoot = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT") ??
                throw new InvalidDataException("SHARPENGINE_ROOT is required");
            var ProjectManifest = Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST") ??
                throw new InvalidDataException("SERVER_PROJECT_REQUIRED: use --project <file.sharpproject>");
            var ProjectRoot = Path.GetDirectoryName(Path.GetFullPath(ProjectManifest))!;
            SharpLog.Initialize("Server", ProjectRoot, EngineRoot);
            var Registry = ComponentRegistry.CreateBuiltin();
            ProjectModules = ProjectModuleHost.Prepare(typeof(DedicatedServerSession).Assembly) ??
                throw new InvalidDataException("SERVER_MODULES_REQUIRED: build the project for Server first");
            foreach (var (Assembly, Id, Type, Hash) in ProjectModules.Assemblies)
            {
                if (Type != "Runtime")
                {
                    throw new InvalidDataException("Server may only start Runtime Modules");
                }

                Registry.RegisterAssembly(Assembly, Id, Hash);
            }

            Registry.ValidateRegistrations();
            var WorldPath = StartupWorld(ProjectManifest, ProjectRoot);
            var Scene = WorldDocument.Load(WorldPath, Registry).Data;
            var Schema = CreateSchema();
            Session = new DedicatedServerSession(Scene, Registry, Schema);
            var CurrentSession = Session;
            var RuntimeContext = new ServerRuntimeContext(CurrentSession);
            EngineModules = EngineModuleHost.Prepare("Server",
                typeof(NetworkingModule).Assembly);
            EngineModules.Start((Id, Type) => new ModuleContext(ProjectRoot, Id, Type,
                components: RuntimeContext));
            ProjectModules.Start((Id, Type) => new ModuleContext(ProjectRoot, Id, Type,
                components: RuntimeContext));
            var Port = CommandLineInteger("--network-port", 0);
            NetworkHost = new DedicatedServerNetworkHost(Session,
                new UdpNetworkTransport(new NetworkTransportOptions { LocalPort = Port }))
            {
                AuthorizeCommand = RuntimeContext.AuthorizeCommand,
                RelevantEntities = RuntimeContext.RelevantEntities,
            };
            Console.WriteLine(new JsonObject
            {
                ["status"] = "listening",
                ["target"] = "Server",
                ["port"] = NetworkHost.LocalPort,
                ["project"] = Path.GetFileName(ProjectManifest),
                ["world"] = Path.GetFileName(WorldPath),
            }.ToJsonString());
            Console.Out.Flush();
            return 0;
        }
        catch (Exception Error)
        {
            SharpLog.Critical("Server", "Initialization failed", Error);
            ShutdownCore();
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    public static void Update(float Seconds)
    {
        try
        {
            var Frame = NetworkHost?.Tick(RuntimeWorld.FixedDeltaSeconds);
            if (Frame is not null)
            {
                CommandExecutions += Frame.CommandResults.Count(Result => Result.Accepted && !Result.Duplicate);
            }
            Updates++;
        }
        catch (Exception Error)
        {
            SharpLog.Critical("Server", "Tick failed", Error);
            Environment.Exit(5);
        }
    }

    [UnmanagedCallersOnly]
    public static void InputEvent(nint Host, uint Kind, uint Code, int X, int Y,
        float ValueX, float ValueY, uint Buttons, uint Modifiers, byte Handled, byte Focused)
    {
    }

    [UnmanagedCallersOnly]
    public static int RunMcp() => 2;

    [UnmanagedCallersOnly]
    public static void Shutdown()
    {
        ShutdownCore();
    }

    private static void ShutdownCore()
    {
        try
        {
            NetworkHost?.Dispose();
            NetworkHost = null;
            ProjectModules?.Dispose();
            ProjectModules = null;
            EngineModules?.Dispose();
            EngineModules = null;
            Session?.Dispose();
            Session = null;
        }
        catch (Exception Error)
        {
            SharpLog.Error("Server", "Shutdown failed", Error);
        }
        finally
        {
            SharpLog.Info("Server", $"Stopped after {Updates} ticks",
                new Dictionary<string, string>
                {
                    ["ticks"] = Updates.ToString(),
                    ["commandExecutions"] = CommandExecutions.ToString(),
                });
            SharpLog.Shutdown();
            Updates = 0;
            CommandExecutions = 0;
        }
    }

    private static NetworkSchemaCatalog CreateSchema()
    {
        return new NetworkSchemaCatalog();
    }

    private static string StartupWorld(string ProjectManifest, string ProjectRoot)
    {
        using var Document = JsonDocument.Parse(File.ReadAllText(ProjectManifest));
        var Startup = Document.RootElement.GetProperty("world").GetProperty("startup").GetString();
        if (string.IsNullOrWhiteSpace(Startup))
        {
            throw new InvalidDataException("SERVER_STARTUP_REQUIRED: world.startup is required");
        }

        var FullPath = Path.GetFullPath(Path.Combine(ProjectRoot, Startup));
        if (!FullPath.StartsWith(ProjectRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !FullPath.EndsWith(".sworld", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(FullPath))
        {
            throw new InvalidDataException("SERVER_STARTUP_INVALID: world.startup must resolve to an existing .sworld");
        }

        return FullPath;
    }

    private static int CommandLineInteger(string Name, int Fallback)
    {
        var Arguments = Environment.GetCommandLineArgs();
        var Index = Array.IndexOf(Arguments, Name);
        return Index >= 0 && Index + 1 < Arguments.Length &&
            int.TryParse(Arguments[Index + 1], out var Value) ? Value : Fallback;
    }

    private sealed class ServerRuntimeContext : INetworkServerRuntime
    {
        private readonly DedicatedServerSession Session;

        public ServerRuntimeContext(DedicatedServerSession Session)
        {
            this.Session = Session;
        }

        public RuntimeWorld? RuntimeWorld
        {
            get
            {
                return Session.World;
            }
        }

        public RuntimeInstance? Instance
        {
            get
            {
                return Session.Instance;
            }
        }

        public NetworkSchemaCatalog Schema
        {
            get
            {
                return Session.Schema;
            }
        }

        public NetworkCommandRouter CommandRouter
        {
            get
            {
                return Session.CommandRouter;
            }
        }

        public Func<NetworkCommandEnvelope, bool>? AuthorizeCommand
        {
            get;
            set;
        }

        public Func<string, IReadOnlySet<string>?>? RelevantEntities
        {
            get;
            set;
        }

        public void RegisterCommand(NetworkRpcDescriptor Descriptor, NetworkCommandHandler Handler)
        {
            Session.CommandRouter.Register(Descriptor, Handler);
        }

        public EngineLifetime SubsystemLifetime
        {
            get
            {
                return Session.Instance.Engine;
            }
        }
    }
}
