using System.Diagnostics;
using SharpEngine.ProjectSelector;

internal static class TargetWrapperCompileValidation
{
    internal static void Run(GeneratedSolution Generated, string Name, IReadOnlyList<string> Targets,
        Action<bool, string> Check)
    {
        foreach (var Target in Targets)
        {
            var Project = SolutionGenerator.TargetFiles(Path.GetDirectoryName(Generated.StampPath)!,
                Name, Target, "Debug").Project;
            var OutputDirectory = Path.Combine(Path.GetDirectoryName(Project)!, "bin",
                "BootstrapCheck-" + Guid.NewGuid().ToString("N"));
            var Build = RunDotnet("build", Project, "-c", "Debug", "--nologo", "-v:minimal",
                "-p:DesignTimeBuild=true", "-p:UseAppHost=false",
                "-p:OutputPath=" + OutputDirectory + Path.DirectorySeparatorChar);
            Check(Build.ExitCode == 0,
                $"Generated {Target} Target must compile before engine binaries exist: {Build.Output}");
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
