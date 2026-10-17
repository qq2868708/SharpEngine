using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;

internal static class TargetLaunchValidation
{
    internal static void Run(string engineRoot, string project, string configuration, Action<bool, string> check)
    {
        var directory = Path.Combine(engineRoot, "Binaries", "Win64", configuration, "Game");
        var executable = Path.Combine(directory, "Engine.exe");
        foreach (var assembly in new[] { "SharpEngine.Runtime.dll", "SharpEngine.Host.dll" })
        {
            using var stream = File.OpenRead(Path.Combine(directory, assembly));
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            check(!metadata.AssemblyReferences.Select(metadata.GetAssemblyReference)
                    .Any(reference => metadata.GetString(reference.Name) == "SharpEngine.Editor"),
                $"{assembly} Game binary must have no Editor assembly dependency");
        }
        (int Exit, string Output) Run(params string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = engineRoot, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("Game launch timed out"); }
            Task.WaitAll(output, errors);
            return (process.ExitCode, output.Result + errors.Result);
        }
        var missingProject = Run("--hidden", "--frames", "2");
        check(missingProject.Exit != 0 && missingProject.Output.Contains("GAME_PROJECT_REQUIRED"),
            "Game must reject a missing project instead of launching the editor/default scene");
        var temporary = Directory.CreateTempSubdirectory("SharpEngine-GameLaunch-");
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(project))!.AsObject();
            manifest.Remove("world");
            var path = Path.Combine(temporary.FullName, "Invalid.sharpproject");
            File.WriteAllText(path, manifest.ToJsonString());
            var missingWorld = Run("--project", path, "--hidden", "--frames", "2");
            check(missingWorld.Exit != 0 && missingWorld.Output.Contains("GAME_STARTUP_REQUIRED"),
                "Game must reject a missing startup World");
            manifest["world"] = new JsonObject { ["startup"] = "Content/Worlds/Missing.sworld" };
            File.WriteAllText(path, manifest.ToJsonString());
            var invalidWorld = Run("--project", path, "--hidden", "--frames", "2");
            check(invalidWorld.Exit != 0 && invalidWorld.Output.Contains("GAME_STARTUP_INVALID"),
                "Game must reject an invalid startup World");
        }
        finally { temporary.Delete(true); }
        var game = Run("--project", project, "--hidden", "--frames", "30");
        check(game.Exit == 0 && game.Output.Contains("First runtime frame") &&
            game.Output.Contains("editorAssemblyLoaded=False") && game.Output.Contains("Stopped after 30 frames"),
            "Standalone Game must render, run its lifecycle, and shut down without loading Editor: " + game.Output);
    }
}
