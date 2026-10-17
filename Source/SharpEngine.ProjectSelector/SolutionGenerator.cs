using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SharpEngine.ProjectSelector;

internal sealed record GeneratedSolution(
    string SolutionPath,
    string WrapperProjectPath,
    string TargetSourcePath,
    string LaunchSettingsPath,
    string TargetPropsPath,
    string GeneratedPropsPath,
    string StampPath);

internal static class SolutionGenerator
{
    private const int GeneratorVersion = 4;
    private const string CppProjectType = "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}";
    private const string CSharpProjectType = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";
    private const string FolderProjectType = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
    private static readonly string[] Configurations = ["Debug", "Development", "Shipping"];

    internal static GeneratedSolution Generate(ResolvedSharpProject resolved)
    {
        var project = resolved.Project;
        var paths = GetGeneratedPaths(project);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.StampPath)!);
        var obsoleteNMakeProject = Path.Combine(Path.GetDirectoryName(paths.StampPath)!,
            SafeFileName(project.DisplayName) + ".Editor.vcxproj");
        if (File.Exists(obsoleteNMakeProject)) File.Delete(obsoleteNMakeProject);
        var obsoleteNMakeOutput = Path.Combine(Path.GetDirectoryName(paths.StampPath)!, "x64");
        if (Directory.Exists(obsoleteNMakeOutput))
            Directory.Delete(obsoleteNMakeOutput, recursive: true);

        WriteGeneratedProps(paths.GeneratedPropsPath, resolved);
        var wrapperGuid = StableGuid("wrapper:" + project.ManifestPath);
        WriteTargetProject(paths, wrapperGuid, resolved);
        WriteTargetSource(paths.TargetSourcePath, resolved);
        WriteLaunchSettings(paths.LaunchSettingsPath, resolved);
        WriteTargetProps(paths.TargetPropsPath);
        WriteSolution(paths.SolutionPath, paths.WrapperProjectPath, wrapperGuid, resolved);
        WriteStamp(paths.StampPath, paths.SolutionPath, paths.WrapperProjectPath, resolved);
        return paths;
    }

    internal static GeneratedSolution EnsureCurrent(ResolvedSharpProject resolved)
    {
        var paths = GetGeneratedPaths(resolved.Project);
        return IsCurrent(paths, resolved) ? paths : Generate(resolved);
    }

    private static GeneratedSolution GetGeneratedPaths(SharpProjectDescriptor project)
    {
        var projectFilesDirectory = Path.Combine(project.Root, "Intermediate", "ProjectFiles");
        return new GeneratedSolution(
            Path.Combine(project.Root, SafeFileName(project.DisplayName) + ".sln"),
            Path.Combine(projectFilesDirectory,
                SafeFileName(project.DisplayName) + ".Editor.Target.csproj"),
            Path.Combine(projectFilesDirectory,
                SafeFileName(project.DisplayName) + ".Editor.Target.cs"),
            Path.Combine(projectFilesDirectory, "Properties", "launchSettings.json"),
            Path.Combine(projectFilesDirectory, "Directory.Build.props"),
            Path.Combine(projectFilesDirectory, "SharpEngine.Generated.props"),
            Path.Combine(projectFilesDirectory, "SharpEngine.ProjectFiles.json"));
    }

    private static bool IsCurrent(GeneratedSolution paths, ResolvedSharpProject resolved)
    {
        if (!File.Exists(paths.SolutionPath) || !File.Exists(paths.WrapperProjectPath) ||
            !File.Exists(paths.TargetSourcePath) || !File.Exists(paths.LaunchSettingsPath) ||
            !File.Exists(paths.TargetPropsPath) ||
            !File.Exists(paths.GeneratedPropsPath) || !File.Exists(paths.StampPath)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(paths.StampPath));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("generatorVersion").GetInt32() != GeneratorVersion ||
                !string.Equals(root.GetProperty("projectManifestHash").GetString(),
                    FileHash(resolved.Project.ManifestPath), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(root.GetProperty("engineManifestHash").GetString(),
                    FileHash(resolved.Engine.ManifestPath), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(root.GetProperty("engineRoot").GetString(),
                    resolved.Engine.Root, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(root.GetProperty("association").GetString(),
                    resolved.Project.EngineAssociation, StringComparison.Ordinal)) return false;
            var modules = root.GetProperty("modules").EnumerateArray()
                .Select(value => value.GetString()).ToArray();
            return modules.SequenceEqual(resolved.Project.Modules,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or JsonException or
                                          InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static void WriteGeneratedProps(string path, ResolvedSharpProject resolved)
    {
        var engine = resolved.Engine;
        var root = EnsureTrailingSeparator(engine.Root);
        var document = new XDocument(
            new XElement("Project",
                new XElement("PropertyGroup",
                    new XElement("SharpEngineGeneratedProjectFiles", "true"),
                    new XElement("SharpEngineAssociation",
                        resolved.Project.EngineAssociation),
                    new XElement("SharpEngineRoot", root),
                    new XElement("SharpEngineManagedApi",
                        root + @"Binaries\Win64\$(Configuration)\Editor\SharpEngine.Managed.dll"),
                    new XElement("SharpEngineModulesApi",
                        root + @"Binaries\Win64\$(Configuration)\Editor\SharpEngine.Modules.dll"))));
        WriteXmlAtomic(path, document);
    }

    private static void WriteTargetProject(GeneratedSolution paths, string projectGuid,
        ResolvedSharpProject resolved)
    {
        var project = resolved.Project;
        var engine = resolved.Engine;
        var script = Path.Combine(engine.Root, "Tools", "SharpEngineProject.ps1");
        var common = $"-ProjectFile {Quote(project.ManifestPath)} -EngineRoot {Quote(engine.Root)} " +
                     "-Target Editor -Configuration $(Configuration) -Platform Win64";
        var build = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File {Quote(script)} " +
                    $"-Command Build {common}";
        var clean = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File {Quote(script)} " +
                    $"-Command Clean {common}";
        var document = new XDocument(
            new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup",
                    new XElement("OutputType", "Exe"),
                    new XElement("TargetFramework", "net8.0-windows"),
                    new XElement("AssemblyName",
                        SafeFileName(project.DisplayName) + ".Editor.Target"),
                    new XElement("RootNamespace",
                        SafeFileName(project.DisplayName) + ".Editor.Target"),
                    new XElement("ProjectGuid", projectGuid),
                    new XElement("EnableDefaultCompileItems", "false"),
                    new XElement("ImplicitUsings", "enable"),
                    new XElement("Nullable", "enable"),
                    new XElement("AppendTargetFrameworkToOutputPath", "false")),
                new XElement("ItemGroup",
                    new XElement("Compile", new XAttribute("Include",
                        paths.TargetSourcePath))),
                new XElement("Target", new XAttribute("Name", "BuildSharpEngineProject"),
                    new XAttribute("AfterTargets", "Build"),
                    new XAttribute("Condition", "'$(DesignTimeBuild)' != 'true'"),
                    new XElement("Exec", new XAttribute("Command", build))),
                new XElement("Target", new XAttribute("Name", "CleanSharpEngineProject"),
                    new XAttribute("AfterTargets", "Clean"),
                    new XAttribute("Condition", "'$(DesignTimeBuild)' != 'true'"),
                    new XElement("Exec", new XAttribute("Command", clean)))));
        WriteXmlAtomic(paths.WrapperProjectPath, document);
    }

    private static void WriteTargetProps(string path)
    {
        var projectFiles = Path.GetDirectoryName(path)!;
        var targetRoot = Path.Combine(projectFiles, "EditorTarget");
        var document = new XDocument(
            new XElement("Project",
                new XElement("PropertyGroup",
                    new XElement("Configuration",
                        new XAttribute("Condition", "'$(Configuration)' == ''"), "Debug"),
                    new XElement("BaseIntermediateOutputPath",
                        EnsureTrailingSeparator(Path.Combine(targetRoot, "obj"))),
                    new XElement("OutputPath",
                        EnsureTrailingSeparator(Path.Combine(targetRoot, "bin",
                            "$(Configuration)"))),
                    new XElement("AppendTargetFrameworkToOutputPath", "false"))));
        WriteXmlAtomic(path, document);
    }

    private static void WriteTargetSource(string path, ResolvedSharpProject resolved)
    {
        var engineRoot = CSharpString(resolved.Engine.Root);
        var projectManifest = CSharpString(resolved.Project.ManifestPath);
        var source = $$"""
            using System.Diagnostics;

            var configuration = new DirectoryInfo(
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Name;
            var engineRoot = {{engineRoot}};
            var projectManifest = {{projectManifest}};
            var executable = Path.Combine(engineRoot, "Binaries", "Win64",
                configuration, "Editor", "Engine.exe");
            if (!File.Exists(executable))
            {
                Console.Error.WriteLine($"SharpEngine Editor is not built: {executable}");
                return 1;
            }
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(projectManifest)!,
            };
            start.ArgumentList.Add("--project");
            start.ArgumentList.Add(projectManifest);
            start.Environment["SHARPENGINE_ROOT"] = engineRoot;
            using var process = Process.Start(start) ??
                throw new IOException("Could not start the SharpEngine Editor");
            process.WaitForExit();
            return process.ExitCode;
            """ + Environment.NewLine;
        WriteTextAtomic(path, source);
    }

    private static void WriteLaunchSettings(string path, ResolvedSharpProject resolved)
    {
        var profiles = new Dictionary<string, object>();
        foreach (var configuration in Configurations)
        {
            profiles[$"{configuration} Editor"] = new
            {
                commandName = "Executable",
                executablePath = Path.Combine(ProjectContracts.ResolveBinaries(resolved.Engine,
                    "Win64", configuration, "Editor"), "Engine.exe"),
                commandLineArgs = "--project " + Quote(resolved.Project.ManifestPath),
                workingDirectory = resolved.Project.Root,
                environmentVariables = new Dictionary<string, string>
                {
                    ["SHARPENGINE_ROOT"] = resolved.Engine.Root,
                },
            };
        }
        WriteTextAtomic(path, JsonSerializer.Serialize(new { profiles },
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static void WriteSolution(string path, string wrapperPath,
        string wrapperGuid, ResolvedSharpProject resolved)
    {
        var project = resolved.Project;
        var engine = resolved.Engine;
        var engineFolderGuid = StableGuid("folder:engine:" + project.ManifestPath);
        var gameFolderGuid = StableGuid("folder:game:" + project.ManifestPath);
        var entries = new List<SolutionProject>();

        var nativeEnginePath = Path.Combine(engine.Root, engine.SourcePath,
            "Engine", "Engine.vcxproj");
        if (File.Exists(nativeEnginePath))
            entries.Add(new SolutionProject("Engine", nativeEnginePath,
                ReadVcxProjectGuid(nativeEnginePath), CppProjectType, engineFolderGuid,
                ProjectConfigurationKind.Cpp, Build: true));
        foreach (var name in new[]
                 {
                     "SharpEngine.Game", "SharpEngine.Managed", "SharpEngine.Modules",
                     "SharpEngine.ProjectSelector",
                 })
        {
            var engineProject = Path.Combine(engine.Root, engine.SourcePath, name,
                name + ".csproj");
            if (!File.Exists(engineProject)) continue;
            entries.Add(new SolutionProject(name, engineProject,
                StableGuid("project:" + engineProject), CSharpProjectType, engineFolderGuid,
                ProjectConfigurationKind.CSharp, Build: true));
        }
        var managedBootstrap = entries.FirstOrDefault(entry => entry.Name == "SharpEngine.Game");
        foreach (var module in project.Modules)
        {
            var modulePath = Path.GetFullPath(Path.Combine(project.Root, module));
            entries.Add(new SolutionProject(Path.GetFileNameWithoutExtension(modulePath),
                modulePath, StableGuid("project:" + modulePath), CSharpProjectType,
                gameFolderGuid, ProjectConfigurationKind.CSharp, Build: false));
        }
        var nativeEngine = entries.FirstOrDefault(entry => entry.Name == "Engine");
        var targetDependencies = new[] { nativeEngine?.Guid, managedBootstrap?.Guid }
            .OfType<string>().ToArray();
        entries.Add(new SolutionProject(project.DisplayName + ".Editor Target", wrapperPath,
            wrapperGuid, CSharpProjectType, gameFolderGuid, ProjectConfigurationKind.CSharp,
            Build: true, DependencyGuids: targetDependencies));

        var builder = new StringBuilder();
        builder.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        builder.AppendLine("# Visual Studio Version 18");
        builder.AppendLine("VisualStudioVersion = 18.3.11312.210 d18.3");
        builder.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");
        AppendFolder(builder, "Engine", engineFolderGuid);
        AppendFolder(builder, "Game", gameFolderGuid, project.ManifestPath, path);
        foreach (var entry in entries)
        {
            builder.Append("Project(\"").Append(entry.TypeGuid).Append("\") = \"")
                .Append(entry.Name).Append("\", \"")
                .Append(SolutionPath(path, entry.Path)).Append("\", \"")
                .Append(entry.Guid).AppendLine("\"");
            if (entry.DependencyGuids is { Count: > 0 })
            {
                builder.AppendLine("\tProjectSection(ProjectDependencies) = postProject");
                foreach (var dependencyGuid in entry.DependencyGuids)
                    builder.Append('\t').Append('\t').Append(dependencyGuid).Append(" = ")
                        .AppendLine(dependencyGuid);
                builder.AppendLine("\tEndProjectSection");
            }
            builder.AppendLine("EndProject");
        }
        builder.AppendLine("Global");
        builder.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        foreach (var configuration in Configurations)
            builder.Append('\t').Append('\t').Append(configuration)
                .Append(" Editor|Win64 = ").Append(configuration)
                .AppendLine(" Editor|Win64");
        builder.AppendLine("\tEndGlobalSection");
        builder.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var entry in entries)
            foreach (var configuration in Configurations)
            {
                var solutionConfiguration = configuration + " Editor|Win64";
                var projectConfiguration = configuration + (entry.Kind ==
                    ProjectConfigurationKind.CSharp ? "|Any CPU" : "|x64");
                builder.Append('\t').Append('\t').Append(entry.Guid).Append('.')
                    .Append(solutionConfiguration).Append(".ActiveCfg = ")
                    .AppendLine(projectConfiguration);
                if (entry.Build)
                    builder.Append('\t').Append('\t').Append(entry.Guid).Append('.')
                        .Append(solutionConfiguration).Append(".Build.0 = ")
                        .AppendLine(projectConfiguration);
            }
        builder.AppendLine("\tEndGlobalSection");
        builder.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
        builder.AppendLine("\t\tHideSolutionNode = FALSE");
        builder.AppendLine("\tEndGlobalSection");
        builder.AppendLine("\tGlobalSection(NestedProjects) = preSolution");
        foreach (var entry in entries)
            builder.Append('\t').Append('\t').Append(entry.Guid).Append(" = ")
                .AppendLine(entry.FolderGuid);
        builder.AppendLine("\tEndGlobalSection");
        builder.AppendLine("\tGlobalSection(ExtensibilityGlobals) = postSolution");
        builder.Append("\t\tSolutionGuid = ")
            .AppendLine(StableGuid("solution:" + project.ManifestPath));
        builder.AppendLine("\tEndGlobalSection");
        builder.AppendLine("EndGlobal");
        WriteTextAtomic(path, builder.ToString());
    }

    private static void AppendFolder(StringBuilder builder, string name, string guid,
        string? solutionItem = null, string? solutionPath = null)
    {
        builder.Append("Project(\"").Append(FolderProjectType).Append("\") = \"")
            .Append(name).Append("\", \"").Append(name).Append("\", \"")
            .Append(guid).AppendLine("\"");
        if (solutionItem is not null && solutionPath is not null)
        {
            var item = SolutionPath(solutionPath, solutionItem);
            builder.AppendLine("\tProjectSection(SolutionItems) = preProject");
            builder.Append('\t').Append('\t').Append(item).Append(" = ").AppendLine(item);
            builder.AppendLine("\tEndProjectSection");
        }
        builder.AppendLine("EndProject");
    }

    private static void WriteStamp(string path, string solutionPath, string wrapperPath,
        ResolvedSharpProject resolved)
    {
        var value = new
        {
            schemaVersion = 1,
            generatorVersion = GeneratorVersion,
            projectManifestHash = FileHash(resolved.Project.ManifestPath),
            engineManifestHash = FileHash(resolved.Engine.ManifestPath),
            engineRoot = resolved.Engine.Root,
            association = resolved.Project.EngineAssociation,
            solution = Path.GetFileName(solutionPath),
            wrapperProject = Path.GetRelativePath(resolved.Project.Root, wrapperPath)
                .Replace('\\', '/'),
            modules = resolved.Project.Modules,
        };
        WriteTextAtomic(path, JsonSerializer.Serialize(value,
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static string ReadVcxProjectGuid(string path)
    {
        var document = XDocument.Load(path);
        var value = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "ProjectGuid")?.Value;
        return string.IsNullOrWhiteSpace(value) ? StableGuid("project:" + path) :
            value.ToUpperInvariant();
    }

    private static string SolutionPath(string solutionPath, string projectPath) =>
        Path.GetRelativePath(Path.GetDirectoryName(solutionPath)!, projectPath)
            .Replace('/', '\\');

    private static string StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            value.Replace('\\', '/').ToLowerInvariant()));
        return "{" + new Guid(bytes.AsSpan(0, 16)).ToString().ToUpperInvariant() + "}";
    }

    private static string FileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string EnsureTrailingSeparator(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' :
            character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "SharpEngineProject" : result;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string CSharpString(string value) => JsonSerializer.Serialize(value);

    private static void WriteXmlAtomic(string path, XDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        document.Save(temporary);
        File.Move(temporary, path, true);
    }

    private static void WriteTextAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private enum ProjectConfigurationKind
    {
        Cpp,
        CSharp,
    }

    private sealed record SolutionProject(
        string Name,
        string Path,
        string Guid,
        string TypeGuid,
        string FolderGuid,
        ProjectConfigurationKind Kind,
        bool Build,
        IReadOnlyList<string>? DependencyGuids = null);
}
