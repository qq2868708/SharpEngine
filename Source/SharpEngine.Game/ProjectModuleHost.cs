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
    private readonly HashSet<string> _publishedAssemblies =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _assemblyDirectory;
    private Func<AssemblyLoadContext, AssemblyName, Assembly?>? _resolver;

    public static ProjectModuleHost? Load(EditorRuntime runtime)
    {
        var projectManifest = Environment.GetEnvironmentVariable(
            "SHARPENGINE_PROJECT_MANIFEST");
        if (string.IsNullOrWhiteSpace(projectManifest)) return null;

        var manifestPath = Path.GetFullPath(projectManifest);
        var projectRoot = Path.GetDirectoryName(manifestPath)!;
        var engineRoot = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT") ??
            throw new InvalidDataException("SHARPENGINE_ROOT is not configured");
        using var engineDocument = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(engineRoot, "engine.json")));
        var engineManifest = engineDocument.RootElement;
        if (engineManifest.GetProperty("schemaVersion").GetInt32() != 2)
            throw new InvalidDataException("Unsupported engine manifest version");
        var bootstrapLocation = typeof(ProjectModuleHost).Assembly.Location;
        if (string.IsNullOrWhiteSpace(bootstrapLocation))
            throw new InvalidDataException("Managed bootstrap location is unavailable");
        var targetDirectory = new DirectoryInfo(
            Path.GetDirectoryName(bootstrapLocation)!);
        var target = targetDirectory.Name;
        var configuration = targetDirectory.Parent?.Name ?? string.Empty;
        var platform = targetDirectory.Parent?.Parent?.Name ?? string.Empty;
        if (platform != "Win64" ||
            configuration is not ("Debug" or "Development" or "Shipping") ||
            target != "Editor")
            throw new InvalidDataException(
                $"The Editor is not running from a supported Engine Target: {targetDirectory.FullName}");
        var binariesTemplate = engineManifest.GetProperty("paths")
            .GetProperty("binaries").GetString() ??
            throw new InvalidDataException("Engine manifest paths.binaries is required");
        var assemblyDirectory = ResolveLayoutPath(projectRoot, binariesTemplate,
            platform, configuration, target);
        var runtimeManifestPath = Path.Combine(assemblyDirectory,
            "modules.runtime.json");
        if (!File.Exists(runtimeManifestPath))
            throw new FileNotFoundException(
                "Project modules are not built. Run SharpEngineProject.ps1 -Command Build.",
                runtimeManifestPath);

        using var document = JsonDocument.Parse(File.ReadAllText(runtimeManifestPath));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 2)
            throw new InvalidDataException("Unsupported module runtime manifest version");
        if (!string.Equals(root.GetProperty("platform").GetString(), platform,
                StringComparison.Ordinal) ||
            !string.Equals(root.GetProperty("configuration").GetString(), configuration,
                StringComparison.Ordinal) ||
            !string.Equals(root.GetProperty("target").GetString(), target,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Project Modules were built for a different Platform/Configuration/Target");
        using var projectDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var project = projectDocument.RootElement;
        if (project.GetProperty("schemaVersion").GetInt32() != 3)
            throw new InvalidDataException("Unsupported project manifest version");
        if (!string.Equals(project.GetProperty("projectId").GetString(),
                root.GetProperty("projectId").GetString(), StringComparison.Ordinal) ||
            !string.Equals(FileHash(manifestPath),
                root.GetProperty("projectManifestHash").GetString(),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Project manifest changed after Module build. Rebuild the project.");
        if (!string.Equals(engineManifest.GetProperty("engineId").GetString(),
                root.GetProperty("engineId").GetString(), StringComparison.Ordinal) ||
            !string.Equals(engineManifest.GetProperty("version").GetString(),
                root.GetProperty("engineVersion").GetString(), StringComparison.Ordinal))
            throw new InvalidDataException(
                "Project Modules were built for a different engine. Rebuild the project.");

        var loadContext = AssemblyLoadContext.GetLoadContext(
            typeof(ProjectModuleHost).Assembly) ?? AssemblyLoadContext.Default;
        var host = new ProjectModuleHost(loadContext)
        { _assemblyDirectory = assemblyDirectory };
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            var name = RequiredFileName(file.GetProperty("name").GetString());
            if (!host._publishedAssemblies.Add(name))
                throw new InvalidDataException($"Duplicate published file '{name}'");
            var path = Path.Combine(assemblyDirectory, name);
            if (!File.Exists(path) || !string.Equals(FileHash(path),
                    file.GetProperty("contentHash").GetString(),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Published file '{name}' does not match its build manifest");
        }
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
        if (!_publishedAssemblies.Contains(Path.GetFileName(assemblyPath)))
            throw new InvalidDataException(
                $"Module '{id}' assembly is missing from the published file manifest");
        var entryTypeName = descriptor.GetProperty("entryType").GetString()!;
        if (!string.Equals(FileHash(assemblyPath),
                descriptor.GetProperty("contentHash").GetString(),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Module '{id}' content hash does not match its build manifest");
        var assembly = _loadContext.LoadFromAssemblyPath(assemblyPath);
        runtime.RegisterProjectSettingsAssembly(assembly);
        var entryType = assembly.GetType(entryTypeName, throwOnError: true)!;
        if (Activator.CreateInstance(entryType) is not ISharpEngineModule instance)
            throw new InvalidDataException(
                $"Module '{id}' entry does not implement ISharpEngineModule");
        try
        {
            ModuleContext context = type.Equals("Editor", StringComparison.OrdinalIgnoreCase)
                ? new EditorModuleContext(projectRoot, id, runtime)
                : new ModuleContext(projectRoot, id, type, runtime.UiRuntime);
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
        var fileName = name.Name + ".dll";
        if (!_publishedAssemblies.Contains(fileName)) return null;
        var path = Path.Combine(_assemblyDirectory, fileName);
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

    private static string RequiredFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != Path.GetFileName(value))
            throw new InvalidDataException("Published file names must be plain file names");
        return value;
    }

    private static string ResolveLayoutPath(string root, string template,
        string platform, string configuration, string target)
    {
        var relative = template.Replace("{platform}", platform, StringComparison.Ordinal)
            .Replace("{configuration}", configuration, StringComparison.Ordinal)
            .Replace("{target}", target, StringComparison.Ordinal);
        if (relative.Contains('{') || relative.Contains('}') || Path.IsPathRooted(relative))
            throw new InvalidDataException(
                $"Invalid Engine binaries path template '{template}'");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Engine binaries path escapes the project root");
        return result;
    }
}
