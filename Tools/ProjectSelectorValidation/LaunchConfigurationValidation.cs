using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using SharpEngine.ProjectSelector;

internal static class LaunchConfigurationValidation
{
    internal static void Run(string EngineRoot, string TemporaryRoot, Action<bool, string> Check)
    {
        var Root = Path.Combine(TemporaryRoot, "Launch Configuration");
        var FakeEngine = Path.Combine(Root, "Engine");
        var ProjectRoot = Path.Combine(Root, "Project");
        Directory.CreateDirectory(Path.Combine(FakeEngine, "Build"));
        Directory.CreateDirectory(Path.Combine(FakeEngine, "Tools"));
        Directory.CreateDirectory(Path.Combine(ProjectRoot, "Source"));
        File.Copy(Path.Combine(EngineRoot, "engine.json"), Path.Combine(FakeEngine, "engine.json"));
        File.WriteAllText(Path.Combine(ProjectRoot, "Source", "Fixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var ManifestPath = Path.Combine(ProjectRoot, "Fixture.sharpproject");
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            projectId = "fixture",
            displayName = "Fixture",
            engine = new
            {
                id = "SharpEngine",
                version = "0.2.0-dev",
                association = "fixture",
            },
            modules = new[] { "Source/Fixture.csproj" },
            targets = new[] { "Editor", "Game", "Server" },
        }));
        File.WriteAllText(Path.Combine(FakeEngine, "Tools", "SharpEngineProject.ps1"), """
            param($Command, $EngineRoot, $Target, $Configuration, $Platform, $RenderBackends, $ProjectFile)
            [IO.File]::WriteAllText((Join-Path $EngineRoot 'receipt.json'),
                (@{ Configuration=$Configuration; Target=$Target } | ConvertTo-Json))
            [IO.File]::AppendAllText((Join-Path $EngineRoot 'builds.txt'), "$Configuration/$Target`n")
            """);
        var Project = ProjectContracts.ReadProject(ManifestPath);
        var Engine = ProjectContracts.ReadEngine(FakeEngine);
        var Resolved = new ResolvedSharpProject(Project, Engine);
        var Generated = SolutionGenerator.Generate(Resolved);
        var DirectoryPath = Path.GetDirectoryName(Generated.StampPath)!;
        var Solution = File.ReadAllText(Generated.SolutionPath);
        foreach (var Configuration in EngineBuildGraph.Configurations)
        {
            foreach (var Target in EngineBuildGraph.Targets)
            {
                var Files = SolutionGenerator.TargetFiles(DirectoryPath, "Fixture", Target, Configuration);
                var Wrapper = XDocument.Load(Files.Project);
                var Id = Wrapper.Descendants("ProjectGuid").Single().Value;
                foreach (var SolutionConfiguration in EngineBuildGraph.Configurations)
                {
                    Check(Solution.Contains($"{Id}.{SolutionConfiguration} Editor|Win64.ActiveCfg = {Configuration}|Any CPU",
                            StringComparison.Ordinal),
                        $"Launching {Configuration} {Target} must keep its build configuration under {SolutionConfiguration}");
                }
                using var LaunchDocument = JsonDocument.Parse(File.ReadAllText(Files.LaunchSettings));
                var Profiles = LaunchDocument.RootElement.GetProperty("profiles").EnumerateObject().ToArray();
                Check(Profiles.Length == 1 && Profiles[0].Name == $"{Configuration} {Target}",
                    "Each Target must expose only its own launch profile");
                var Executable = Target == "Server" ? "DedicatedServer.exe" : "Engine.exe";
                var ExpectedPath = Path.Combine(FakeEngine, "Binaries", "Win64", Configuration, Target, Executable);
                Check(Profiles[0].Value.GetProperty("executablePath").GetString() == ExpectedPath,
                    "Launch executable must match the fixed build tuple");
                var OtherConfiguration = Configuration == "Debug" ? "Development" : "Debug";
                var Build = RunDotnet("build", Files.Project, "-c", OtherConfiguration, "-v:quiet");
                Check(Build.ExitCode == 0, $"Fixed Target must build under a different global configuration: {Build.Output}");
                using var Receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(FakeEngine, "receipt.json")));
                Check(Receipt.RootElement.GetProperty("Configuration").GetString() == Configuration &&
                    Receipt.RootElement.GetProperty("Target").GetString() == Target,
                    "Unified builder must receive the launch configuration and target");
                var Assembly = Path.Combine(DirectoryPath, "bin", Path.GetFileNameWithoutExtension(Files.Project),
                    OtherConfiguration, Path.GetFileNameWithoutExtension(Files.Project) + ".dll");
                var Launch = RunDotnet(Assembly);
                Check(Launch.ExitCode == 1 && Launch.Output.Contains(ExpectedPath, StringComparison.Ordinal),
                    "Bootstrap launcher must use the same fixed tuple before any engine binaries exist");
            }
        }
        var DebugFiles = SolutionGenerator.TargetFiles(DirectoryPath, "Fixture", "Editor", "Debug");
        var ExplicitBuild = RunDotnet("msbuild", Generated.SolutionPath, "-restore",
            "-t:BuildTarget\\Debug Editor Target", "-p:Configuration=Development Editor", "-p:Platform=Win64",
            "-v:quiet");
        Check(ExplicitBuild.ExitCode == 0, "Rider-style explicit solution Target build must succeed: " + ExplicitBuild.Output);
        using (var Receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(FakeEngine, "receipt.json"))))
        {
            Check(Receipt.RootElement.GetProperty("Configuration").GetString() == "Debug" &&
                Receipt.RootElement.GetProperty("Target").GetString() == "Editor",
                "Explicit Debug Editor solution Target must override the active Development solution configuration");
        }
        File.WriteAllText(Path.Combine(FakeEngine, "builds.txt"), string.Empty);
        var SolutionBuild = RunDotnet("msbuild", Generated.SolutionPath, "-restore",
            "-t:Build", "-p:Configuration=Shipping Game", "-p:Platform=Win64", "-v:quiet");
        Check(SolutionBuild.ExitCode == 0, "Ordinary solution build must succeed: " + SolutionBuild.Output);
        Check(File.ReadAllLines(Path.Combine(FakeEngine, "builds.txt")).SequenceEqual(new[] { "Shipping/Game" }),
            "Ordinary solution build must build exactly the active tuple");
        File.Delete(Generated.SolutionTargetsPath);
        _ = SolutionGenerator.EnsureCurrent(Resolved);
        Check(File.Exists(Generated.SolutionTargetsPath), "Missing explicit launch forwarding file must regenerate");
        File.Delete(DebugFiles.LaunchSettings);
        _ = SolutionGenerator.EnsureCurrent(Resolved);
        Check(File.Exists(DebugFiles.LaunchSettings), "Missing per-target profiles must regenerate");
        _ = ProjectTargets.UpdateManifest(ManifestPath, "Server", Add: false);
        _ = SolutionGenerator.EnsureCurrent(new ResolvedSharpProject(ProjectContracts.ReadProject(ManifestPath), Engine));
        foreach (var Configuration in EngineBuildGraph.Configurations)
        {
            var Files = SolutionGenerator.TargetFiles(DirectoryPath, "Fixture", "Server", Configuration);
            Check(!File.Exists(Files.Project) && !File.Exists(Files.LaunchSettings),
                "Disabling Server must remove every generated Server launch target");
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
        using var Process = System.Diagnostics.Process.Start(Start) ?? throw new IOException("Could not start dotnet");
        var Output = Process.StandardOutput.ReadToEndAsync();
        var Errors = Process.StandardError.ReadToEndAsync();
        if (!Process.WaitForExit(60000))
        {
            Process.Kill(entireProcessTree: true);
            Process.WaitForExit();
            throw new TimeoutException("Launch configuration validation timed out");
        }
        Task.WaitAll(Output, Errors);
        return (Process.ExitCode, Output.Result + Errors.Result);
    }
}
