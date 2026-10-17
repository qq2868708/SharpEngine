using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using SharpEngine.Runtime;

namespace SharpEngine.Runtime.Hosting;

internal sealed class ProjectModuleHost : IDisposable
{
    private static readonly List<(WeakReference Context, string Directory)> RetiredCopies = [];
    private readonly List<ISharpEngineModule> _modules = new();
    private readonly ModuleRegistrationScope _registrations = new();
    internal readonly List<(Assembly Assembly, string Id, string Type, string Hash)> Assemblies = [];
    private readonly List<string> _entryTypes = [];
    private bool _disposed;
    internal string? ShutdownError { get; private set; }
    private readonly AssemblyLoadContext _loadContext;
    private readonly HashSet<string> _publishedAssemblies =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _assemblyDirectory;
    private Func<AssemblyLoadContext, AssemblyName, Assembly?>? _resolver;

    internal static ProjectModuleHost? Prepare(params Assembly[] additionalSharedAssemblies)
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
            target is not ("Editor" or "Game"))
            throw new InvalidDataException(
                $"The Editor is not running from a supported Engine Target: {targetDirectory.FullName}");
        var binariesTemplate = engineManifest.GetProperty("paths")
            .GetProperty("binaries").GetString() ??
            throw new InvalidDataException("Engine manifest paths.binaries is required");
        var assemblyDirectory = ResolveLayoutPath(projectRoot, binariesTemplate,
            platform, configuration, target);
        var intermediateDirectory = ResolveLayoutPath(projectRoot,
            engineManifest.GetProperty("paths").GetProperty("intermediate").GetString()!, platform, configuration, target);
        var buildDirectory = Path.Combine(intermediateDirectory, "ProjectBuild");
        Directory.CreateDirectory(buildDirectory);
        using var publishLease = new FileStream(Path.Combine(buildDirectory, "build.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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
        if (root.TryGetProperty("plugins", out var plugins))
        {
            foreach (var plugin in plugins.EnumerateArray())
            {
                var source = plugin.GetProperty("source").GetString() ?? string.Empty;
                var pluginPath = ResolvePluginManifest(projectRoot, engineRoot, source);
                if (!string.Equals(FileHash(pluginPath),
                        plugin.GetProperty("manifestHash").GetString(),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Plugin manifest changed after Module build: {source}. Rebuild the project.");
            }
        }

        // Load immutable private copies so the unified builder can publish while
        // this generation is live. Keep shared engine contracts in the host ALC.
        var shadowDirectory = Path.Combine(intermediateDirectory, "HotReload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(shadowDirectory);
        var loadContext = new ProjectLoadContext(shadowDirectory,
            new[] { typeof(ISharpEngineModule).Assembly }.Concat(additionalSharedAssemblies).ToArray());
        var host = new ProjectModuleHost(loadContext)
        { _assemblyDirectory = shadowDirectory };
        try
        {
            foreach (var file in root.GetProperty("files").EnumerateArray())
            {
                var name = RequiredFileName(file.GetProperty("name").GetString());
                if (!host._publishedAssemblies.Add(name))
                    throw new InvalidDataException($"Duplicate published file '{name}'");
                var path = Path.Combine(assemblyDirectory, name);
                if (!File.Exists(path) || !string.Equals(FileHash(path),
                        file.GetProperty("contentHash").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Published file '{name}' does not match its build manifest");
                var copy = Path.Combine(shadowDirectory, name);
                File.Copy(path, copy);
                if (!string.Equals(FileHash(copy), file.GetProperty("contentHash").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Published file '{name}' changed during reload");
            }
            host._resolver = (_, name) => host.ResolveAssembly(name);
            host._loadContext.Resolving += host._resolver;
            foreach (var module in root.GetProperty("modules").EnumerateArray())
            {
                if (target == "Game" && module.GetProperty("type").GetString() != "Runtime")
                    throw new InvalidDataException("Game Target cannot load Editor Modules");
                host.ReadModule(module);
            }
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
        if (_disposed) return;
        _disposed = true;
        using var entered = _registrations.Enter();
        for (var index = _modules.Count - 1; index >= 0; index--)
        {
            try { _modules[index].Shutdown(); }
            catch (Exception exception)
            {
                ShutdownError = exception.ToString();
                Console.Error.WriteLine($"[Module] Shutdown failed: {exception}");
            }
        }
        _modules.Clear();
        _registrations.Dispose();
        Assemblies.Clear();
        _entryTypes.Clear();
        if (_resolver is not null)
            _loadContext.Resolving -= _resolver;
        _resolver = null;
        if (_assemblyDirectory is not null) RetiredCopies.Add((new WeakReference(_loadContext), _assemblyDirectory));
        _loadContext.Unload();
    }

    internal static void CollectRetiredCopies()
    {
        foreach (var copy in RetiredCopies.ToArray())
        {
            if (copy.Context.IsAlive) continue;
            try
            {
                if (Directory.Exists(copy.Directory))
                {
                    // These are flat, exclusively engine-created generation directories.
                    if ((File.GetAttributes(copy.Directory) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var file in Directory.EnumerateFiles(copy.Directory)) File.Delete(file);
                    Directory.Delete(copy.Directory);
                }
                RetiredCopies.Remove(copy);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void ReadModule(JsonElement descriptor)
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
        var entryType = assembly.GetType(entryTypeName, throwOnError: true)!;
        if (!typeof(ISharpEngineModule).IsAssignableFrom(entryType) || entryType.IsAbstract ||
            (!entryType.IsPublic && !entryType.IsNestedPublic) || entryType.ContainsGenericParameters || entryType.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidDataException($"Module '{id}' has an invalid entry type '{entryTypeName}'");
        Assemblies.Add((assembly, id, type, descriptor.GetProperty("contentHash").GetString()!));
        _entryTypes.Add(entryTypeName);
    }

    internal void Start(Func<string, string, ModuleContext> createContext)
    {
        using var entered = _registrations.Enter();
        var projectRoot = Path.GetDirectoryName(Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST"))!;
        for (var index = 0; index < Assemblies.Count; index++)
        {
            var (assembly, id, type, _) = Assemblies[index];
            var entryType = assembly.GetType(_entryTypes[index], throwOnError: true)!;
            if (Activator.CreateInstance(entryType) is not ISharpEngineModule instance)
                throw new InvalidDataException($"Module '{id}' entry does not implement ISharpEngineModule");
            // Include the partially started module in cleanup on failure.
            _modules.Add(instance);
            ModuleContext context = createContext(id, type);
            instance.Startup(context);
            Console.Error.WriteLine($"[Module] Started {id} ({type})");
        }
    }

    private sealed class ProjectLoadContext(string directory, Assembly[] sharedAssemblies) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            foreach (var shared in sharedAssemblies)
                if (AssemblyName.ReferenceMatchesDefinition(shared.GetName(), name)) return shared;
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
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

    private static string ResolvePluginManifest(string projectRoot, string engineRoot,
        string source)
    {
        var separator = source.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
            throw new InvalidDataException($"Invalid Plugin URI: {source}");
        var scheme = source[..separator];
        var relative = source[(separator + 3)..].Replace('/', Path.DirectorySeparatorChar);
        if (!relative.StartsWith("Plugins" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Plugin URI must remain under Plugins: {source}");
        var root = scheme.Equals("project", StringComparison.OrdinalIgnoreCase)
            ? projectRoot
            : scheme.Equals("engine", StringComparison.OrdinalIgnoreCase)
                ? engineRoot
                : throw new InvalidDataException($"Unknown Plugin URI scheme: {scheme}");
        var pluginsRoot = Path.GetFullPath(Path.Combine(root, "Plugins"));
        var prefix = pluginsRoot.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(pluginsRoot,
            relative[("Plugins" + Path.DirectorySeparatorChar).Length..]));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(".sharpplugin", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            throw new InvalidDataException($"Plugin manifest is invalid or missing: {source}");
        return path;
    }
}
