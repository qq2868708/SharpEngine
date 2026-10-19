using SharpEngine.Host;
using SharpEngine.ProjectSelector;

internal static class EngineFirstUseValidation
{
    internal static void Run(string RegistryPath, string TemporaryDirectory, string EngineRoot,
        Action<bool, string> Check)
    {
        var Builds = new EngineAssociationStore(RegistryPath + @"\FirstUseBuilds");
        var ClassesPath = RegistryPath + @"\FirstUseClasses";
        var Notifications = 0;
        var FirstUse = new EngineFirstUse(Builds,
            new ProjectFileAssociation(ClassesPath, () => Notifications++));
        string Engine(string Name)
        {
            var Root = Directory.CreateDirectory(Path.Combine(TemporaryDirectory, Name)).FullName;
            File.Copy(Path.Combine(EngineRoot, "engine.json"), Path.Combine(Root, "engine.json"));
            return Root;
        }
        string CreateSelector(string Root, string Configuration)
        {
            var ToolDirectory = Directory.CreateDirectory(Path.Combine(Root, "Binaries", "Win64", Configuration, "Tools"));
            var Executable = Path.Combine(ToolDirectory.FullName, "SharpEngine.ProjectSelector.exe");
            File.WriteAllBytes(Executable, Array.Empty<byte>());
            return Executable;
        }
        var Source = Engine("First Use Source");
        Directory.CreateDirectory(Path.Combine(Source, "Source"));
        var SelectorExecutable = CreateSelector(Source, "Debug");
        var Registration = FirstUse.EnsureRegistered(Source, SelectorExecutable);
        Check(Guid.TryParse(Registration.Association, out _) && Builds.List().Count == 1 && Notifications == 1,
            "First use must register a source root and its shell actions together");
        Check(FirstUse.EnsureRegistered(Source, SelectorExecutable) == Registration && Builds.List().Count == 1 && Notifications == 1,
            "Repeated first use must reuse the root identity without rewriting shell actions");
        var Installed = Engine("First Use Installed");
        var InstalledSelector = CreateSelector(Installed, "Shipping");
        var InstallerRegistration = Builds.Register(Installed, null, installed: true);
        Check(FirstUse.EnsureRegistered(Installed, InstalledSelector) == InstallerRegistration &&
              InstallerRegistration.Association == "0.2",
            "Startup must preserve a version association already created by an installer");
        var Portable = Engine("First Use Extracted");
        var PortableSelector = CreateSelector(Portable, "Development");
        var PortableRegistration = FirstUse.EnsureRegistered(Portable, PortableSelector);
        Check(Guid.TryParse(PortableRegistration.Association, out _) &&
              PortableRegistration.Association != Registration.Association &&
              Builds.Resolve(InstallerRegistration.Association) == Installed,
            "An extracted engine must register without source files or replacing another installation");

        var Missing = Engine("First Use Missing Tool");
        var Count = Builds.List().Count;
        try
        {
            FirstUse.EnsureRegistered(Missing, Path.Combine(Missing, "SharpEngine.ProjectSelector.exe"));
            Check(false, "A missing selector must fail desktop setup");
        }
        catch (FileNotFoundException)
        {
            Check(Builds.List().Count == Count,
                "A missing selector must not leave an engine registered with broken shell commands");
        }
        Check(Path.GetFileName(EngineFirstUse.ExecutablePath) == "SharpEngine.ProjectSelector.exe" &&
              EngineFirstUse.ExecutablePath != Environment.ProcessPath,
            "Registration must use the selector apphost even when called from another managed host");

        var DevelopmentSelector = CreateSelector(Source, "Development");
        var EditorDirectory = Path.Combine(Source, "Binaries", "Win64", "Debug", "Editor");
        var Start = EditorFirstUse.CreateStartInfo(Source, EditorDirectory);
        Check(Start.FileName == SelectorExecutable && !Start.UseShellExecute && Start.CreateNoWindow &&
              Start.ArgumentList.SequenceEqual(new[]
              {
                  "ensure-registered", "--engine-root", Source, "--no-ui"
              }),
            "Direct editor startup must use the packaged selector and safely pass a root containing spaces");
        File.Delete(SelectorExecutable);
        Check(EditorFirstUse.CreateStartInfo(Source, EditorDirectory).FileName == DevelopmentSelector,
            "Editor startup must find another packaged tool configuration when its own is unavailable");
        Check(EditorFirstUse.CreateStartInfo(Portable,
                Path.Combine(Portable, "Binaries", "Win64", "Development", "Editor")).FileName == PortableSelector,
            "Extracted editor startup must not require selector source or dotnet run");
        try
        {
            _ = EditorFirstUse.CreateStartInfo(Missing, Path.Combine(Missing, "Binaries", "Win64", "Debug", "Editor"));
            Check(false, "An incomplete distribution must report a missing selector");
        }
        catch (FileNotFoundException)
        {
            Check(true, "An incomplete distribution must report a missing selector");
        }
    }
}
