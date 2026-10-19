using Microsoft.Win32;
using SharpEngine.ProjectSelector;

internal static class ProjectFileAssociationValidation
{
    internal static void Run(string RegistryPath, string TemporaryDirectory,
        Action<bool, string> Check)
    {
        var ClassesPath = RegistryPath + @"\Classes";
        var Executable = Path.Combine(TemporaryDirectory, "Selector With Spaces", "ProjectSelector.exe");
        var Notifications = 0;
        var Association = new ProjectFileAssociation(ClassesPath, () => Notifications++);
        string? Read(string Path)
        {
            using var Key = Registry.CurrentUser.OpenSubKey(ClassesPath + "\\" + Path);
            return Key?.GetValue(string.Empty) as string;
        }

        Check(Association.Register(Executable) && Notifications == 1 &&
              Read(".sharpproject") == "SharpEngine.Project" &&
              Read("SharpEngine.Project") == "SharpEngine Project",
            "First shell registration must create the per-user file association and notify Explorer");
        foreach (var (Verb, Label, Command) in new[]
                 {
                     ("open", "Open in SharpEngine", "open-editor"),
                     ("GenerateProjectFiles", "Generate Project Files", "GenerateProjectFiles"),
                     ("AddServerTarget", "Add Server Target", "add-target --target Server"),
                     ("RemoveServerTarget", "Remove Server Target", "remove-target --target Server"),
                     ("solution", "Open Visual Studio Solution", "open-solution"),
                 })
        {
            Check(Read(@"SharpEngine.Project\shell\" + Verb) == Label &&
                  Read(@"SharpEngine.Project\shell\" + Verb + @"\command") ==
                  $"\"{Executable}\" {Command} --project \"%1\"",
                "Shell actions must quote both executable and selected project paths: " + Verb);
        }

        Check(!Association.Register(Executable) && Notifications == 1,
            "Repeated startup registration must leave correct entries alone without notifying Explorer again");

        var EngineRoot = Path.Combine(TemporaryDirectory, "Engine Root");
        Check(Association.Register(Executable, EngineRoot) && Notifications == 2 &&
              Read(@"SharpEngine.Project\shell\AddServerTarget\command") ==
              $"\"{Executable}\" add-target --target Server --engine-root \"{EngineRoot}\" --project \"%1\"",
            "Add Server Target must carry the registered Engine root so a project can be updated directly from Explorer");

        using (var Shell = Registry.CurrentUser.OpenSubKey(
                   ClassesPath + @"\SharpEngine.Project\shell", writable: true)!)
        {
            Shell.DeleteSubKeyTree("GenerateProjectFiles");
        }
        Check(Association.Register(Executable) && Notifications == 3 &&
              Read(@"SharpEngine.Project\shell\GenerateProjectFiles\command") is not null,
            "A later startup must repair a missing Generate Project Files action");

        using (var Legacy = Registry.CurrentUser.CreateSubKey(
                   ClassesPath + @"\SharpEngine.Project\shell\generate"))
        {
            Legacy.SetValue(string.Empty, "Legacy generator");
        }
        using (var Custom = Registry.CurrentUser.CreateSubKey(
                   ClassesPath + @"\SharpEngine.Project\shell\custom"))
        {
            Custom.SetValue(string.Empty, "Unrelated action");
        }
        Check(Association.Register(Executable) && Notifications == 4 &&
              Read(@"SharpEngine.Project\shell\generate") is null &&
              Read(@"SharpEngine.Project\shell\custom") == "Unrelated action",
            "Registration must remove the legacy generate verb while preserving unrelated actions");

        var MovedExecutable = Path.Combine(TemporaryDirectory, "New Selector", "ProjectSelector.exe");
        Check(Association.Register(MovedExecutable) && Notifications == 5 &&
              Read(@"SharpEngine.Project\shell\GenerateProjectFiles\command") ==
              $"\"{MovedExecutable}\" GenerateProjectFiles --project \"%1\"" &&
              !Association.Register(MovedExecutable) && Notifications == 5,
            "A new build location must update the shell command once and then remain stable");
    }
}
