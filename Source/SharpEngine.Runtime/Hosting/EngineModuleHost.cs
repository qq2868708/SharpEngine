using System.Reflection;
using SharpEngine.Runtime;
using SharpEngine.Logging;

namespace SharpEngine.Runtime.Hosting;

/// <summary>
/// Owns the lifecycle of Engine-owned feature Modules that are statically linked
/// into the Host. Project Modules use <see cref="ProjectModuleHost"/> because they
/// are rebuilt and hot-reloaded from a project manifest; Engine Modules are built
/// with the Engine and remain in the Host generation for its whole lifetime.
/// </summary>
internal sealed class EngineModuleHost : IDisposable
{
    private readonly List<(Assembly Assembly, string Id, string Type, Type Entry)> _modules;
    private readonly List<ISharpEngineModule> _started = [];
    private readonly ModuleRegistrationScope _registrations = new();
    private bool _disposed;

    internal string? ShutdownError { get; private set; }

    private EngineModuleHost(List<(Assembly Assembly, string Id, string Type, Type Entry)> modules)
    {
        _modules = modules;
    }

    internal static EngineModuleHost Prepare(string target, params Assembly[] assemblies)
    {
        if (target is not ("Editor" or "Game"))
            throw new ArgumentException("Engine Module target must be Editor or Game", nameof(target));

        var modules = new List<(Assembly Assembly, string Id, string Type, Type Entry)>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies.Distinct())
        {
            var metadata = assembly.GetCustomAttribute<SharpEngineModuleAttribute>();
            if (metadata is null || target == "Game" && metadata.Type == "Editor") continue;
            if (!ids.Add(metadata.Id))
                throw new InvalidDataException($"Engine Modules must not share id '{metadata.Id}'");

            var entries = assembly.GetTypes().Where(type =>
                typeof(ISharpEngineModule).IsAssignableFrom(type) &&
                type is { IsInterface: false, IsAbstract: false } &&
                type.GetConstructor(Type.EmptyTypes) is not null).ToArray();
            if (entries.Length != 1)
                throw new InvalidDataException(
                    $"Engine Module '{metadata.Id}' must define exactly one public parameterless ISharpEngineModule entry; found {entries.Length}");
            var entry = entries[0];
            if (!entry.IsPublic && !entry.IsNestedPublic)
                throw new InvalidDataException(
                    $"Engine Module '{metadata.Id}' entry '{entry.FullName}' must be public");
            modules.Add((assembly, metadata.Id, metadata.Type, entry));
        }

        return new EngineModuleHost(modules);
    }

    internal void Start(Func<string, string, ModuleContext> createContext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(createContext);
        using var entered = _registrations.Enter();
        try
        {
            foreach (var module in _modules)
            {
                var instance = (ISharpEngineModule)(Activator.CreateInstance(module.Entry) ??
                    throw new InvalidDataException($"Could not construct Engine Module '{module.Id}'"));
                _started.Add(instance);
                instance.Startup(createContext(module.Id, module.Type));
                SharpLog.Info("EngineModule", $"Started {module.Id} ({module.Type})");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        using var entered = _registrations.Enter();
        for (var index = _started.Count - 1; index >= 0; index--)
        {
            try { _started[index].Shutdown(); }
            catch (Exception exception)
            {
                ShutdownError = exception.ToString();
                SharpLog.Error("EngineModule", "Shutdown failed", exception,
                    new Dictionary<string, string> { ["module"] = _started[index].GetType().FullName ?? string.Empty });
            }
        }
        _started.Clear();
        _registrations.Dispose();
    }
}
