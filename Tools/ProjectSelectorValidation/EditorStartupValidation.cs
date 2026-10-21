using System.Diagnostics;
using System.Text.Json;

internal static class EditorStartupValidation
{
    internal static void Run(string EngineRoot, string TemporaryRoot, Action<bool, string> Check,
        string Configuration = "Debug")
    {
        var Native = Path.Combine(EngineRoot, "Binaries", "Win64", Configuration, "Editor", "Engine.exe");
        if (!File.Exists(Native))
        {
            Console.WriteLine($"SKIP: native Editor startup routing ({Configuration} Editor is not built)");
            return;
        }
        var FixtureRoot = Path.Combine(TemporaryRoot, "StartupRouting");
        var EditorDirectory = Path.Combine(FixtureRoot, "Binaries", "Win64", Configuration, "Editor");
        var ToolsDirectory = Path.Combine(FixtureRoot, "Binaries", "Win64", Configuration, "Tools");
        Directory.CreateDirectory(EditorDirectory);
        Directory.CreateDirectory(ToolsDirectory);
        File.Copy(Path.Combine(EngineRoot, "engine.json"), Path.Combine(FixtureRoot, "engine.json"));
        var Executable = Path.Combine(EditorDirectory, "Engine.exe");
        File.Copy(Native, Executable);
        foreach (var Source in Directory.EnumerateFiles(Path.Combine(EngineRoot, "Binaries", "Win64", Configuration, "Tools")))
        {
            File.Copy(Source, Path.Combine(ToolsDirectory, Path.GetFileName(Source)), true);
        }
        string[] Resources =
        [
            "Content/UI/Rml/ProjectBrowser.rml", "Content/UI/Rml/ProjectBrowser.rcss", "Content/UI/Rml/Editor.rcss",
            "Resources/Editor/Icons/minimize.tga", "Resources/Editor/Icons/maximize.tga", "Resources/Editor/Icons/close.tga",
        ];
        foreach (var Resource in Resources)
        {
            var Destination = Path.Combine(FixtureRoot, Resource);
            Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
            File.Copy(Path.Combine(EngineRoot, Resource), Destination);
        }
        (int ExitCode, string Output) Run(params string[] Arguments)
        {
            var Start = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var Argument in Arguments)
            {
                Start.ArgumentList.Add(Argument);
            }
            using var Process = System.Diagnostics.Process.Start(Start)!;
            var Output = Process.StandardOutput.ReadToEndAsync();
            var Errors = Process.StandardError.ReadToEndAsync();
            if (!Process.WaitForExit(20000))
            {
                Process.Kill(true);
                throw new InvalidOperationException("Native project routing did not finish its bounded run");
            }
            Task.WaitAll(Output, Errors);
            return (Process.ExitCode, Output.Result + Errors.Result);
        }
        var Isolated = Run("--mcp-isolated");
        Check(Isolated.ExitCode != 0 && Isolated.Output.Contains("EDITOR_PROJECT_REQUIRED"),
            "An isolated Editor must reject a missing project");
        var Observation = Path.Combine(FixtureRoot, "Observation");
        var Browser = Run("--project-browser", "--mcp-isolated", "--frames", "3", "--browser-observation", Observation,
            "--browser-recent-file", Path.Combine(FixtureRoot, "recent.json"));
        Check(Browser.ExitCode == 0 && Browser.Output.Contains("Project browser ready"),
            "Projectless native startup must render the engine RmlUi Project Browser: " + Browser.Output);
        using var State = JsonDocument.Parse(File.ReadAllText(Path.Combine(Observation, "browser-state.json")));
        Check(!State.RootElement.GetProperty("editorAssemblyLoaded").GetBoolean() &&
            !State.RootElement.GetProperty("projectModulesLoaded").GetBoolean(),
            "Project Browser must not load the Editor assembly or project Modules");
        Check(File.ReadAllText(Path.Combine(Observation, "browser-ui.json")).Contains("browser-path-input"),
            "Browser observation must use the live native RmlUi tree");
        var Invalid = Run("--project", Path.Combine(FixtureRoot, "Missing.sharpproject"), "--no-splash");
        Check(Invalid.ExitCode != 0, "Invalid explicit projects must fail instead of entering an empty Editor");
    }
}
