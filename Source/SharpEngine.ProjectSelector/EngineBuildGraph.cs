using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SharpEngine.Logging;

namespace SharpEngine.ProjectSelector;

internal sealed record EngineBuildProject(string Name, string Path, string Role,
    string? ModuleId = null, string? ModuleType = null);

/// <summary>Project files own their role and references; no parallel project list.</summary>
internal static class EngineBuildGraph
{
    internal static readonly string[] Configurations = ["Debug", "Development", "Shipping"];
    internal static readonly string[] Targets = ["Editor", "Game", "Server"];
    internal static IReadOnlyList<EngineBuildProject> Discover(string root)
    {
        var source = Path.Combine(root, "Source");
        if (!Directory.Exists(source)) return [];
        var result = Directory.EnumerateFiles(source, "*.*proj", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".csproj" or ".vcxproj")
            .Where(path => !Path.GetRelativePath(source, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .Select(path =>
            {
                var document = XDocument.Load(path);
                var role = document.Descendants().SingleOrDefault(e => e.Name.LocalName == "SharpEngineRole")?.Value
                    ?? throw new InvalidDataException($"Engine project must declare SharpEngineRole: {path}");
                if (role is not ("Runtime" or "Editor" or "Host" or "NativeHost" or "Tool" or "Generator"))
                    throw new InvalidDataException($"Unknown engine project role '{role}': {path}");
                var moduleId = document.Descendants().SingleOrDefault(
                    e => e.Name.LocalName == "SharpEngineModuleId")?.Value;
                var moduleType = document.Descendants().SingleOrDefault(
                    e => e.Name.LocalName == "SharpEngineModuleType")?.Value;
                if (role is "Runtime" or "Editor")
                {
                    if (string.IsNullOrWhiteSpace(moduleId))
                        throw new InvalidDataException(
                            $"Engine {role} project must declare SharpEngineModuleId: {path}");
                    if (!string.Equals(moduleType, role, StringComparison.Ordinal))
                        throw new InvalidDataException(
                            $"Engine {role} project must declare SharpEngineModuleType={role}: {path}");
                }
                return new EngineBuildProject(Path.GetFileNameWithoutExtension(path),
                    Path.GetFullPath(path), role, moduleId, moduleType);
            }).OrderBy(project => project.Name, StringComparer.Ordinal).ToArray();
        var duplicateModule = result.Where(project => project.ModuleId is not null)
            .GroupBy(project => project.ModuleId!, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateModule is not null)
            throw new InvalidDataException(
                $"Engine projects must not share Module id '{duplicateModule.Key}'");
        return result;
    }

    internal static string Fingerprint(string root)
    {
        var files = Discover(root).Select(project => project.Path)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Build"), "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path) is ".props" or ".targets"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + "\n" + File.ReadAllText(path));
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('\n', files))));
    }

    internal static void Build(string Root, string Target, string Configuration, string Platform,
        string RenderBackends = "Both")
    {
        if (!Targets.Contains(Target) || !Configurations.Contains(Configuration) || Platform != "Win64")
        {
            throw new ArgumentException("Build requires Editor|Game|Server, Debug|Development|Shipping, Win64");
        }
        RenderBackends = RenderBuildOptions.Validate(RenderBackends);
        Root = Path.GetFullPath(Root);
        var Projects = Discover(Root);
        if (Projects.Count == 0)
        {
            var Executable = Target == "Server" ? "DedicatedServer.exe" : "Engine.exe";
            var PathValue = Path.Combine(Root, "Binaries", Platform, Configuration, Target, Executable);
            if (!File.Exists(PathValue))
            {
                throw new FileNotFoundException("Installed engine target is unavailable", PathValue);
            }
            return;
        }
        var Host = Projects.Single(Project => Project.Role == "Host");
        var Native = Projects.Single(Project => Project.Role == "NativeHost");
        var State = Path.Combine(Root, "Intermediate", Platform, Configuration, Target);
        Directory.CreateDirectory(State);
        using var Lease = new FileStream(Path.Combine(State, "engine-build.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        Run("dotnet", ["build", Host.Path, "-c", Configuration, $"-p:SharpEngineTarget={Target}",
            $"-p:SharpEnginePlatform={Platform}"], Root);
        Run(FindMSBuild(), [Native.Path, "/t:Build", "/m:1", "/nologo", "/v:minimal",
            $"/p:SharpEngineRoot={Path.TrimEndingDirectorySeparator(Root)}{Path.DirectorySeparatorChar}",
            $"/p:Configuration={Configuration}", "/p:Platform=x64", $"/p:SharpEngineTarget={Target}",
            $"/p:SharpEngineRenderBackends={RenderBackends}"], Root);
    }

    internal static void Run(string executable, IEnumerable<string> arguments, string directory)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false,
            CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit(); Task.WaitAll(output, errors);
        if (!string.IsNullOrWhiteSpace(output.Result)) Console.Write(output.Result);
        if (!string.IsNullOrWhiteSpace(errors.Result)) Console.Error.Write(errors.Result);
        var properties = new Dictionary<string, string>
        {
            ["exitCode"] = process.ExitCode.ToString(),
            ["stdoutLength"] = output.Result.Length.ToString(),
            ["stderrLength"] = errors.Result.Length.ToString(),
        };
        if (process.ExitCode == 0) SharpLog.Info("EngineBuild", $"{executable} completed", properties);
        else SharpLog.Error("EngineBuild", $"{executable} failed", properties: properties);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {output.Result} {errors.Result}");
    }

    private static string FindMSBuild()
    {
        if (Environment.GetEnvironmentVariable("MSBUILD_EXE_PATH") is { } configured &&
            configured.EndsWith("MSBuild.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(configured))
            return configured;
        var candidates = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Select(path => Path.Combine(path, "Microsoft Visual Studio"))
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateDirectories(path))
            .SelectMany(path => Directory.EnumerateDirectories(path))
            .Select(path => Path.Combine(path, "MSBuild", "Current", "Bin", "amd64", "MSBuild.exe"))
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        return candidates.FirstOrDefault() ?? throw new FileNotFoundException("Visual Studio C++ MSBuild is required");
    }
}
