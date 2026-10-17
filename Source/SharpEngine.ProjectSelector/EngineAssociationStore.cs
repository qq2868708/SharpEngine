using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace SharpEngine.ProjectSelector;

internal sealed record EngineRegistration(string Association, string Root);

internal sealed class EngineAssociationStore
{
    internal const string RegistryPath = @"Software\SharpEngine\Builds";
    private readonly string _registryPath;

    internal EngineAssociationStore(string registryPath = RegistryPath)
    {
        _registryPath = registryPath;
    }

    internal IReadOnlyList<EngineRegistration> List()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: false);
        if (key is null) return Array.Empty<EngineRegistration>();
        return key.GetValueNames()
            .Select(name => new EngineRegistration(name,
                key.GetValue(name) as string ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Root))
            .OrderBy(item => item.Association, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal string? Resolve(string association)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: false);
        return key?.GetValue(association) as string;
    }

    internal EngineRegistration Register(string engineRoot, string? requestedAssociation,
        bool installed)
    {
        var descriptor = ProjectContracts.ReadEngine(engineRoot);
        var existing = List().FirstOrDefault(item => SamePath(item.Root, descriptor.Root));
        if (existing is not null && string.IsNullOrWhiteSpace(requestedAssociation))
            return existing;
        var association = requestedAssociation;
        if (string.IsNullOrWhiteSpace(association))
            association = installed ? InstalledAssociation(descriptor.Version) :
                "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        ValidateAssociation(association);
        using var key = Registry.CurrentUser.CreateSubKey(_registryPath, writable: true) ??
            throw new InvalidOperationException("Could not open the SharpEngine association registry");
        if (key.GetValue(association) is string current && !SamePath(current, descriptor.Root))
            throw new InvalidOperationException(
                $"Engine association '{association}' already belongs to '{current}'");
        key.SetValue(association, descriptor.Root, RegistryValueKind.String);
        return new EngineRegistration(association, descriptor.Root);
    }

    internal void Bind(string association, string engineRoot)
    {
        ValidateAssociation(association);
        var descriptor = ProjectContracts.ReadEngine(engineRoot);
        using var key = Registry.CurrentUser.CreateSubKey(_registryPath, writable: true) ??
            throw new InvalidOperationException("Could not open the SharpEngine association registry");
        key.SetValue(association, descriptor.Root, RegistryValueKind.String);
    }

    internal bool Unregister(string association)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: true);
        if (key?.GetValue(association) is null) return false;
        key.DeleteValue(association, throwOnMissingValue: false);
        return true;
    }

    private static string InstalledAssociation(string version)
    {
        var parts = version.Split('.', '-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }

    private static void ValidateAssociation(string association)
    {
        if (string.IsNullOrWhiteSpace(association) || association.Length > 128 ||
            (!Guid.TryParse(association, out _) &&
             !Regex.IsMatch(association, "^[A-Za-z0-9][A-Za-z0-9._-]*$",
                 RegexOptions.CultureInvariant)))
            throw new ArgumentException("Engine association is invalid", nameof(association));
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
