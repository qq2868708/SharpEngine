namespace SharpEngine.ProjectSelector;

/// <summary>Per-user desktop setup shared by editor startup and project tools.</summary>
internal sealed class EngineFirstUse
{
    private readonly EngineAssociationStore Engines;
    private readonly ProjectFileAssociation Files;

    internal EngineFirstUse(EngineAssociationStore? Engines = null,
        ProjectFileAssociation? Files = null)
    {
        this.Engines = Engines ?? new EngineAssociationStore();
        this.Files = Files ?? new ProjectFileAssociation();
    }

    internal EngineRegistration EnsureRegistered(string EngineRoot, string Executable)
    {
        ValidateExecutable(Executable);
        // Reuse an installer's version association, or allocate a per-root GUID
        // for an unregistered source/portable distribution. Never steal another root's ID.
        var Registration = Engines.Register(EngineRoot, null, installed: false);
        Files.Register(Executable);
        return Registration;
    }

    internal static string ExecutablePath
    {
        get
        {
            return Path.ChangeExtension(typeof(EngineFirstUse).Assembly.Location, ".exe");
        }
    }

    internal static void ValidateExecutable(string Executable)
    {
        if (!File.Exists(Executable) || !string.Equals(Path.GetFileName(Executable),
                "SharpEngine.ProjectSelector.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(
                "Desktop integration requires the built SharpEngine.ProjectSelector.exe", Executable);
        }
    }
}
