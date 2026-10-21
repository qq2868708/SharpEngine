using System.Diagnostics;

internal static class PluginOutputValidation
{
    internal static void Run(string EngineRoot, string TemporaryRoot, Action<bool, string> Check)
    {
        var Module = Path.Combine(EngineRoot, "Plugins", "EngineTestPlugin", "Source",
            "EngineTestPlugin.Runtime", "EngineTestPlugin.Runtime.csproj");
        var ProjectRoot = Path.Combine(TemporaryRoot, "PluginOutput") + Path.DirectorySeparatorChar;
        var Intermediate = Path.Combine(ProjectRoot, "Intermediate", "Win64", "Debug", "Editor");
        var OutputRoot = Path.Combine(Intermediate, "ProjectBuild", "Validation") + Path.DirectorySeparatorChar;
        var ModuleRoot = Path.Combine(Intermediate, "Modules") + Path.DirectorySeparatorChar;
        (int Code, string Output) Run(string Output, string Modules, bool Unified)
        {
            var Start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var Argument in new[]
                     {
                         "msbuild", Module, "-nologo", "-t:ValidateSharpEngineOutputContract;ValidateSharpEngineProjectPluginOutput",
                         "-p:Configuration=Debug", "-p:SharpEngineTarget=Editor", "-p:SharpEnginePlatform=Win64",
                         $"-p:SharpEngineUnifiedBuild={Unified}", $"-p:SharpEngineProjectBuildRoot={ProjectRoot}",
                         $"-p:SharpEngineOutputRoot={Output}", $"-p:SharpEngineIntermediateRoot={Modules}",
                     })
            {
                Start.ArgumentList.Add(Argument);
            }
            using var Process = System.Diagnostics.Process.Start(Start)!;
            var StandardOutput = Process.StandardOutput.ReadToEndAsync();
            var Errors = Process.StandardError.ReadToEndAsync();
            if (!Process.WaitForExit(15000))
            {
                Process.Kill(true);
                throw new InvalidOperationException("Plugin output validation timed out");
            }
            Task.WaitAll(StandardOutput, Errors);
            return (Process.ExitCode, StandardOutput.Result + Errors.Result);
        }
        var Valid = Run(OutputRoot, ModuleRoot, true);
        Check(Valid.Code == 0, "Unified Plugin builds must allow project-owned staging: " + Valid.Output);
        var InvalidOutput = Run(Path.Combine(TemporaryRoot, "WrongOutput") + Path.DirectorySeparatorChar, ModuleRoot, true);
        Check(InvalidOutput.Code != 0 && InvalidOutput.Output.Contains("SEOUTPUT015"),
            "Unified Plugin builds must reject output outside project staging");
        var InvalidIntermediate = Run(OutputRoot, Path.Combine(TemporaryRoot, "WrongIntermediate") + Path.DirectorySeparatorChar, true);
        Check(InvalidIntermediate.Code != 0 && InvalidIntermediate.Output.Contains("SEOUTPUT016"),
            "Unified Plugin builds must reject intermediates outside the project Modules directory");
        var Ordinary = Run(OutputRoot, ModuleRoot, false);
        Check(Ordinary.Code != 0 && Ordinary.Output.Contains("SEOUTPUT007"),
            "Ordinary engine builds must retain their strict output layout");
    }
}
