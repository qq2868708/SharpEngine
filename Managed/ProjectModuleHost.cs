using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using SharpEngine.Editor;
using SharpEngine.Modules;

namespace SharpEngine.Game;

internal sealed class ProjectModuleHost : IDisposable
{
    private readonly List<ISharpEngineModule> _modules = new();
    private readonly AssemblyLoadContext _loadContext;
    private string? _assemblyDirectory;
    private Func<AssemblyLoadContext, AssemblyName, Assembly?>? _resolver;

    public static ProjectModuleHost? Load(EditorRuntime runtime)
    {
        var projectManifest = Environment.GetEnvironmentVariable(
            "SHARPENGINE_PROJECT_MANIFEST");
        if (string.IsNullOrWhiteSpace(projectManifest)) return null;

        var manifestPath = Path.GetFullPath(projectManifest);
        var projectRoot = Path.GetDirectoryName(manifestPath)!;
        var runtimeManifestPath = Path.Combine(projectRoot, "Binaries", "Managed",
            "modules.runtime.json");
        if (!File.Exists(runtimeManifestPath))
            throw new FileNotFoundException(
                "Project modules are not built. Run SharpEngineProject.ps1 -Command Build.",
                runtimeManifestPath);

        using var document = JsonDocument.Parse(File.ReadAllText(runtimeManifestPath));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported module runtime manifest version");
        if (!string.Equals(root.GetProperty("target").GetString(), "Editor",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The editor requires an Editor module build target");
        using var projectDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var project = projectDocument.RootElement;
        if (project.GetProperty("schemaVersion").GetInt32() != 2)
            throw new InvalidDataException("Unsupported project manifest version");
        if (!string.Equals(project.GetProperty("projectId").GetString(),
                root.GetProperty("projectId").GetString(), StringComparison.Ordinal) ||
            !string.Equals(FileHash(manifestPath),
                root.GetProperty("projectManifestHash").GetString(),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Project manifest changed after Module build. Rebuild the project.");
        var engineRoot = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT") ??
            throw new InvalidDataException("SHARPENGINE_ROOT is not configured");
        using var engineDocument = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(engineRoot, "Engine", "engine.json")));
        if (!string.Equals(engineDocument.RootElement.GetProperty("engineId").GetString(),
                root.GetProperty("engineId").GetString(), StringComparison.Ordinal) ||
            !string.Equals(engineDocument.RootElement.GetProperty("version").GetString(),
                root.GetProperty("engineVersion").GetString(), StringComparison.Ordinal))
            throw new InvalidDataException(
                "Project Modules were built for a different engine. Rebuild the project.");

        var loadContext = AssemblyLoadContext.GetLoadContext(
            typeof(ProjectModuleHost).Assembly) ?? AssemblyLoadContext.Default;
        var host = new ProjectModuleHost(loadContext)
        { _assemblyDirectory = Path.GetDirectoryName(runtimeManifestPath)! };
        host._resolver = (_, name) => host.ResolveAssembly(name);
        host._loadContext.Resolving += host._resolver;
        try
        {
            foreach (var module in root.GetProperty("modules").EnumerateArray())
                host.StartModule(projectRoot, runtime, module);
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        for (var index = _modules.Count - 1; index >= 0; index--)
        {
            try { _modules[index].Shutdown(); }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Module] Shutdown failed: {exception}");
            }
        }
        _modules.Clear();
        if (_resolver is not null)
            _loadContext.Resolving -= _resolver;
        _resolver = null;
    }

    private void StartModule(string projectRoot, EditorRuntime runtime,
        JsonElement descriptor)
    {
        var id = descriptor.GetProperty("id").GetString()!;
        var type = descriptor.GetProperty("type").GetString()!;
        var assemblyPath = Path.GetFullPath(Path.Combine(_assemblyDirectory!,
            descriptor.GetProperty("assembly").GetString()!));
        var entryTypeName = descriptor.GetProperty("entryType").GetString()!;
        if (!string.Equals(FileHash(assemblyPath),
                descriptor.GetProperty("contentHash").GetString(),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Module '{id}' content hash does not match its build manifest");
        var assembly = _loadContext.LoadFromAssemblyPath(assemblyPath);
        var entryType = assembly.GetType(entryTypeName, throwOnError: true)!;
        if (Activator.CreateInstance(entryType) is not ISharpEngineModule instance)
            throw new InvalidDataException(
                $"Module '{id}' entry does not implement ISharpEngineModule");
        try
        {
            ModuleContext context = type.Equals("Editor", StringComparison.OrdinalIgnoreCase)
                ? new EditorModuleContext(projectRoot, id, runtime)
                : new ModuleContext(projectRoot, id, type);
            instance.Startup(context);
            _modules.Add(instance);
            Console.Error.WriteLine($"[Module] Started {id} ({type})");
        }
        catch
        {
            try { instance.Shutdown(); } catch { }
            throw;
        }
    }

    private Assembly? ResolveAssembly(AssemblyName name)
    {
        if (_assemblyDirectory is null || string.IsNullOrWhiteSpace(name.Name))
            return null;
        var path = Path.Combine(_assemblyDirectory, name.Name + ".dll");
        return File.Exists(path)
            ? _loadContext.LoadFromAssemblyPath(path)
            : null;
    }

    private ProjectModuleHost(AssemblyLoadContext loadContext)
    {
        _loadContext = loadContext;
    }

    private static string FileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
