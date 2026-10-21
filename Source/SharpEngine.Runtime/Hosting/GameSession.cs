using SharpEngine.Modules;
using System.Text.Json;
using System.Reflection;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Input;
using SharpEngine.Logging;
using SharpEngine.Settings;
using SharpEngine.UI;
using SharpEngine.World;
using SharpEngine.Subsystems;

namespace SharpEngine.Hosting;

/// <summary>Standalone game composition. Editor Play uses the same RuntimeWorld,
/// input service, component catalogs, and scene renderer.</summary>
internal sealed class GameSession : IComponentRuntime, ISubsystemRuntime, IDisposable
{
    public EngineLifetime SubsystemLifetime
    {
        get;
    } = new();
    public RuntimeInstance? Instance
    {
        get;
        private set;
    }
    private ProjectModuleHost? _modules;
    private EngineModuleHost? _engineModules;
    private DevelopmentSettingsRegistry? _settings;
    public RuntimeWorld? RuntimeWorld { get; private set; }
    public AssetRegistry Assets { get; }
    public InputRuntimeService Input { get; }
    public UiRuntimeService Ui { get; } = new();
    public WorldDocument World { get; private set; } = null!;

    public GameSession(string engineRoot, string manifestPath,
        params Assembly[] engineModuleAssemblies)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new InvalidDataException("GAME_PROJECT_REQUIRED: Game requires --project <file.sharpproject>");
        manifestPath = Path.GetFullPath(manifestPath);
        var projectRoot = Path.GetDirectoryName(manifestPath)!;
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!manifest.RootElement.TryGetProperty("world", out var world) ||
            !world.TryGetProperty("startup", out var startup) ||
            startup.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(startup.GetString()))
            throw new InvalidDataException("GAME_STARTUP_REQUIRED: Project world.startup must name a saved .sworld");
        var worldPath = Path.GetFullPath(Path.Combine(projectRoot, startup.GetString()!));
        if (!worldPath.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !worldPath.EndsWith(".sworld", StringComparison.OrdinalIgnoreCase) || !File.Exists(worldPath))
            throw new InvalidDataException("GAME_STARTUP_INVALID: world.startup must resolve to an existing project .sworld");
        SharpLog.Initialize("Game", projectRoot, engineRoot);
        Environment.SetEnvironmentVariable("SHARPENGINE_ROOT", engineRoot);
        Environment.SetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST", manifestPath);
        Assets = AssetRegistry.Load(engineRoot, projectRoot, manifestPath);
        Input = InputRuntimeService.Load(Assets);
        try
        {
            Instance = new RuntimeInstance(Assets, SubsystemLifetime, Input, "game");
            _settings = new DevelopmentSettingsRegistry(projectRoot, engineRoot);
            var registry = ComponentRegistry.CreateBuiltin();
            _engineModules = EngineModuleHost.Prepare("Game", engineModuleAssemblies);
            _engineModules.Start((id, type) =>
                new ModuleContext(projectRoot, id, type, Ui, Input, this));
            _modules = ProjectModuleHost.Prepare() ?? throw new InvalidDataException("GAME_MODULES_REQUIRED");
            foreach (var (assembly, id, type, hash) in _modules.Assemblies)
            {
                if (type != "Runtime") throw new InvalidDataException("Game may only start Runtime Modules");
                registry.RegisterAssembly(assembly, id, hash);
                _settings.RegisterAssembly(assembly, SettingsScope.Project);
            }
            registry.ValidateRegistrations();
            _modules.Start((id, type) => new ModuleContext(projectRoot, id, type, Ui, Input, this));
            World = WorldDocument.Load(worldPath, registry);
            if (World.ActiveCamera is null)
                throw new InvalidDataException("GAME_CAMERA_REQUIRED: Startup World must contain an active camera");
            RuntimeWorld = Instance.CreateWorld(World.Data, registry);
            World.ApplyRuntimeFrame(RuntimeWorld.Frame);
            Input.SetRouteEnabled(InputRoute.Editor, false);
            Input.SetRouteEnabled(InputRoute.RuntimeUi, true);
            Input.SetRouteEnabled(InputRoute.Gameplay, true);
            SharpLog.Info("Game", $"Started {manifestPath}; World={worldPath}; entities={World.Entities.Count}",
                new Dictionary<string, string>
                {
                    ["projectManifest"] = manifestPath,
                    ["world"] = worldPath,
                    ["entityCount"] = World.Entities.Count.ToString(),
                });
        }
        catch { Dispose(); throw; }
    }

    public void Update(float Seconds)
    {
        Input.AdvanceFrame(Seconds);
        SubsystemLifetime.Update(Seconds);
        Instance!.Tick(Seconds);
        World.ApplyRuntimeFrame(RuntimeWorld!.Frame);
    }

    public void Dispose()
    {
        Instance?.Dispose();
        Instance = null;
        RuntimeWorld = null;
        _modules?.Dispose(); _modules = null;
        _engineModules?.Dispose(); _engineModules = null;
        SubsystemLifetime.Dispose();
        _settings?.Dispose(); _settings = null;
    }
}
