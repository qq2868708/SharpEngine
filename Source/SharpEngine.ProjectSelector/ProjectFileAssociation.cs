using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SharpEngine.ProjectSelector;

internal sealed class ProjectFileAssociation
{
    private readonly string ClassesPath;
    private readonly Action NotifyChanged;

    internal ProjectFileAssociation(string ClassesPath = @"Software\Classes",
        Action? NotifyChanged = null)
    {
        this.ClassesPath = ClassesPath;
        this.NotifyChanged = NotifyChanged ?? NotifyAssociationChanged;
    }

    internal bool Register(string Executable)
    {
        var Changed = SetDefault(".sharpproject", "SharpEngine.Project");
        Changed |= SetDefault("SharpEngine.Project", "SharpEngine Project");
        Changed |= SetShellCommand("open", "Open in SharpEngine", Executable, "open-editor");
        Changed |= SetShellCommand("GenerateProjectFiles", "Generate Project Files", Executable,
            "GenerateProjectFiles");
        Changed |= SetShellCommand("solution", "Open Visual Studio Solution", Executable,
            "open-solution");
        using (var Shell = Registry.CurrentUser.OpenSubKey(
                   ClassesPath + @"\SharpEngine.Project\shell", writable: true))
        {
            if (Shell?.GetSubKeyNames().Contains("generate", StringComparer.OrdinalIgnoreCase) == true)
            {
                Shell.DeleteSubKeyTree("generate", throwOnMissingSubKey: false);
                Changed = true;
            }
        }
        if (Changed)
        {
            NotifyChanged();
        }
        return Changed;
    }

    private bool SetShellCommand(string Verb, string Label, string Executable, string Command)
    {
        var Path = @"SharpEngine.Project\shell\" + Verb;
        var Changed = SetDefault(Path, Label);
        return SetDefault(Path + @"\command", $"\"{Executable}\" {Command} --project \"%1\"") || Changed;
    }

    private bool SetDefault(string RelativePath, string Value)
    {
        var Path = ClassesPath + "\\" + RelativePath;
        using var Key = Registry.CurrentUser.OpenSubKey(Path, writable: true) ??
            Registry.CurrentUser.CreateSubKey(Path, writable: true) ??
            throw new IOException($"Could not open the file association registry: {Path}");
        if (Key.GetValue(string.Empty) is string Current && Current == Value)
        {
            return false;
        }
        Key.SetValue(string.Empty, Value, RegistryValueKind.String);
        return true;
    }

    private static void NotifyAssociationChanged()
    {
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint EventId, uint Flags, IntPtr Item1, IntPtr Item2);
}
