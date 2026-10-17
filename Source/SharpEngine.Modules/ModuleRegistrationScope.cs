namespace SharpEngine.Modules;

/// <summary>Owns engine registrations made by one loaded project generation.</summary>
public sealed class ModuleRegistrationScope : IDisposable
{
    private static readonly AsyncLocal<ModuleRegistrationScope?> Ambient = new();
    private readonly List<IDisposable> _registrations = [];
    private bool _disposed;
    public static ModuleRegistrationScope? Current => Ambient.Value;

    public IDisposable Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = Ambient.Value;
        Ambient.Value = this;
        return new Restore(() => Ambient.Value = previous);
    }

    public static T Track<T>(T registration) where T : IDisposable
    {
        var scope = Current;
        if (scope is null) return registration;
        lock (scope._registrations)
        {
            if (scope._disposed)
            {
                registration.Dispose();
                throw new ObjectDisposedException(nameof(ModuleRegistrationScope));
            }
            scope._registrations.Add(registration);
        }
        return registration;
    }

    public void Dispose()
    {
        IDisposable[] registrations;
        lock (_registrations)
        {
            if (_disposed) return;
            _disposed = true;
            registrations = _registrations.ToArray();
            _registrations.Clear();
        }
        foreach (var registration in registrations.Reverse())
            try { registration.Dispose(); }
            catch (Exception error) { Console.Error.WriteLine($"[Module] Registration cleanup: {error}"); }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
