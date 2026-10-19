using System.ComponentModel;

namespace SharpEngine.Settings;

public enum SettingsScope : byte
{
    Engine,
    Project,
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DevelopmentSettingsAttribute : Attribute
{
    public DevelopmentSettingsAttribute(string id, SettingsScope scope, int schemaVersion = 1)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A settings ID is required", nameof(id));
        if (schemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        Id = id.Trim();
        Scope = scope;
        SchemaVersion = schemaVersion;
    }

    public string Id { get; }
    public SettingsScope Scope { get; }
    public int SchemaVersion { get; }
    public string? DisplayName { get; init; }
    public string? Category { get; init; }
    public int Order { get; init; }
}

[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SettingAttribute : Attribute
{
    public SettingAttribute(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A setting field ID is required", nameof(id));
        Id = id.Trim();
    }

    public string Id { get; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public double Minimum { get; init; } = double.NaN;
    public double Maximum { get; init; } = double.NaN;
    public int Order { get; init; }
}

public abstract class DevelopmentSettings
{
    private sealed class Binding(IDevelopmentSettingsProvider provider, Binding? previous)
    {
        public IDevelopmentSettingsProvider Provider { get; } = provider;
        public Binding? Previous { get; } = previous;
        public volatile bool Removed;
    }
    private static readonly AsyncLocal<Binding?> Current = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IDevelopmentSettingsProvider, byte>
        Providers = new();

    public static T Get<T>() where T : DevelopmentSettings
    {
        var binding = Current.Value;
        while (binding is { Removed: true }) binding = binding.Previous;
        var provider = binding?.Provider;
        if (provider is null)
        {
            var providers = Providers.Keys.ToArray();
            provider = providers.Length == 1 ? providers[0] :
                throw new InvalidOperationException("No unambiguous Development Settings session is available");
        }
        return (T)provider.Get(typeof(T));
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static void InstallProvider(IDevelopmentSettingsProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Providers.TryAdd(provider, 0);
        Current.Value = new Binding(provider, Current.Value);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static void RemoveProvider(IDevelopmentSettingsProvider provider)
    {
        Providers.TryRemove(provider, out _);
        for (var binding = Current.Value; binding is not null; binding = binding.Previous)
            if (ReferenceEquals(binding.Provider, provider)) binding.Removed = true;
        var current = Current.Value;
        while (current is { Removed: true }) current = current.Previous;
        Current.Value = current;
    }
}

internal interface IDevelopmentSettingsProvider
{
    DevelopmentSettings Get(Type settingsType);
}
