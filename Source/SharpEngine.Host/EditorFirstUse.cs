using System.Diagnostics;
using System.Text.Json;
using SharpEngine.Logging;

namespace SharpEngine.Host;

internal static class EditorFirstUse
{
    internal static void TryRegister(string EngineRoot, string EditorDirectory)
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("SHARPENGINE_REGISTER_ON_FIRST_USE") == "0")
        {
            return;
        }
        try
        {
            using var RegistrationProcess = Process.Start(CreateStartInfo(EngineRoot, EditorDirectory)) ??
                throw new IOException("Could not start Project Selector");
            if (!RegistrationProcess.WaitForExit(10_000))
            {
                SharpLog.Warning("EngineRegistration", "Desktop registration is still running; editor startup will continue");
            }
            else if (RegistrationProcess.ExitCode != 0)
            {
                SharpLog.Warning("EngineRegistration", $"Desktop registration failed with exit code {RegistrationProcess.ExitCode}; see the Project Selector log");
            }
        }
        catch (Exception Error)
        {
            SharpLog.Warning("EngineRegistration", "Automatic desktop registration failed: " + Error.Message);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string EngineRoot, string EditorDirectory)
    {
        using var Manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(EngineRoot, "engine.json")));
        var Binaries = Manifest.RootElement.GetProperty("paths").GetProperty("binaries").GetString()!;
        var Preferred = Directory.GetParent(Path.TrimEndingDirectorySeparator(EditorDirectory))?.Name;
        var Configurations = new[]
            {
                Preferred, "Development", "Debug", "Shipping"
            }
            .Where(Value => Value is "Debug" or "Development" or "Shipping")
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var Executable = Configurations.Select(Configuration => Path.GetFullPath(Path.Combine(EngineRoot,
                Binaries.Replace("{platform}", "Win64").Replace("{configuration}", Configuration)
                    .Replace("{target}", "Tools"), "SharpEngine.ProjectSelector.exe")))
            .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(
                "The engine distribution must include SharpEngine.ProjectSelector.exe in its Tools binaries");
        var StartInfo = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = EngineRoot,
        };
        foreach (var Argument in new[]
                 {
                     "ensure-registered", "--engine-root", EngineRoot, "--no-ui"
                 })
        {
            StartInfo.ArgumentList.Add(Argument);
        }
        return StartInfo;
    }
}
