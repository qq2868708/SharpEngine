using System.Diagnostics;

internal static class TargetWrapperCompileValidation
{
    internal static void Run(string EditorProject, Action<bool, string> Check)
    {
        foreach (var Target in new[]
        {
            "Editor",
            "Game",
        })
        {
            var Project = EditorProject.Replace(".Editor.Target.csproj",
                $".{Target}.Target.csproj", StringComparison.Ordinal);
            // A unique output directory also makes the launcher's inferred configuration
            // unavailable, so the missing-engine diagnostic can be exercised safely.
            var OutputDirectory = Path.Combine(Path.GetDirectoryName(Project)!, "bin",
                "BootstrapCheck-" + Guid.NewGuid().ToString("N"));
            var Build = RunDotnet("build", Project, "-c", "Debug", "--nologo", "-v:minimal",
                "-p:DesignTimeBuild=true", "-p:UseAppHost=false",
                "-p:OutputPath=" + OutputDirectory + Path.DirectorySeparatorChar);
            Check(Build.ExitCode == 0,
                $"Generated {Target} Target must compile before engine binaries exist: {Build.Output}");
            var AssemblyPath = Path.Combine(OutputDirectory, Path.GetFileNameWithoutExtension(Project) + ".dll");
            var Launch = RunDotnet(AssemblyPath);
            Check(Launch.ExitCode == 1 &&
                Launch.Output.Contains("Build this Target first:", StringComparison.Ordinal) &&
                Launch.Output.Contains(Path.Combine("Win64", Path.GetFileName(OutputDirectory), Target, "Engine.exe"),
                    StringComparison.Ordinal),
                $"Generated {Target} Target must report a missing engine executable: {Launch.Output}");
        }
    }

    private static (int ExitCode, string Output) RunDotnet(params string[] Arguments)
    {
        var Start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var Argument in Arguments)
        {
            Start.ArgumentList.Add(Argument);
        }
        using var ProcessInstance = Process.Start(Start) ?? throw new IOException("Could not start dotnet");
        var Output = ProcessInstance.StandardOutput.ReadToEndAsync();
        var Errors = ProcessInstance.StandardError.ReadToEndAsync();
        if (!ProcessInstance.WaitForExit(60000))
        {
            ProcessInstance.Kill(entireProcessTree: true);
            ProcessInstance.WaitForExit();
            throw new TimeoutException("Target wrapper validation timed out");
        }
        Task.WaitAll(Output, Errors);
        return (ProcessInstance.ExitCode, Output.Result + Errors.Result);
    }
}
