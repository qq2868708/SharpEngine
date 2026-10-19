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

    internal static void Build(string root, string target, string configuration, string platform)
    {
        if (!Targets.Contains(target) || !Configurations.Contains(configuration) || platform != "Win64")
            throw new ArgumentException("Build requires Editor|Game|Server, Debug|Development|Shipping, Win64");
        root = Path.GetFullPath(root);
        var projects = Discover(root);
        if (projects.Count == 0)
        {
            var executable = target == "Server" ? "DedicatedServer.exe" : "Engine.exe";
            var path = Path.Combine(root, "Binaries", platform, configuration, target, executable);
            if (!File.Exists(path)) throw new FileNotFoundException("Installed engine target is unavailable", path);
            return;
        }
        var host = projects.Single(project => project.Role == "Host");
        var native = projects.Single(project => project.Role == "NativeHost");
        var state = Path.Combine(root, "Intermediate", platform, configuration, target);
        Directory.CreateDirectory(state);
        using var lease = new FileStream(Path.Combine(state, "engine-build.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        Run("dotnet", ["build", host.Path, "-c", configuration, $"-p:SharpEngineTarget={target}",
            $"-p:SharpEnginePlatform={platform}"], root);
        Run(FindMSBuild(), [native.Path, "/t:Build", "/m:1", "/nologo", "/v:minimal",
            $"/p:SharpEngineRoot={Path.TrimEndingDirectorySeparator(root)}{Path.DirectorySeparatorChar}",
            $"/p:Configuration={configuration}", "/p:Platform=x64", $"/p:SharpEngineTarget={target}"], root);
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
