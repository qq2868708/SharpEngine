using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SharpEngine.ProjectSelector;

internal sealed record GeneratedSolution(string SolutionPath, string WrapperProjectPath,
    string TargetSourcePath, string LaunchSettingsPath, string TargetPropsPath,
    string GeneratedPropsPath, string StampPath);

internal static class SolutionGenerator
{
    private const int GeneratorVersion = 6;
    private const string CsType = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";
    private const string CppType = "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}";
    private const string FolderType = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
    private sealed record Entry(string Name, string Path, string Id, bool Native, string Folder, string? Target);

    internal static GeneratedSolution Generate(ResolvedSharpProject resolved)
    {
        var paths = Paths(resolved.Project);
        WriteGeneratedProps(paths.GeneratedPropsPath, resolved);
        var wrappers = WriteTargets(Path.GetDirectoryName(paths.StampPath)!, resolved.Project.DisplayName,
            resolved.Engine, resolved.Project.ManifestPath);
        WriteSolution(paths.SolutionPath, resolved.Engine, wrappers, resolved.Project);
        WriteText(paths.StampPath, Stamp(resolved));
        return paths;
    }

    internal static void GenerateEngine(string root)
    {
        var engine = ProjectContracts.ReadEngine(root);
        var wrappers = WriteTargets(Path.Combine(engine.Root, "Intermediate", "ProjectFiles"), engine.Id, engine, null);
        WriteSolution(Path.Combine(engine.Root, "SharpEngine.sln"), engine, wrappers, null);
    }

    internal static GeneratedSolution EnsureCurrent(ResolvedSharpProject resolved)
    {
        var p = Paths(resolved.Project);
        if (new[] { p.SolutionPath, p.WrapperProjectPath, p.TargetSourcePath, p.LaunchSettingsPath,
                p.TargetPropsPath, p.GeneratedPropsPath, p.StampPath,
                p.WrapperProjectPath.Replace(".Editor.Target.csproj", ".Game.Target.csproj", StringComparison.Ordinal),
                p.TargetSourcePath.Replace(".Editor.Target.cs", ".Game.Target.cs", StringComparison.Ordinal) }.All(File.Exists) &&
            File.ReadAllText(p.StampPath) == Stamp(resolved)) return p;
        return Generate(resolved);
    }

    private static GeneratedSolution Paths(SharpProjectDescriptor project)
    {
        var name = SafeName(project.DisplayName);
        var dir = Path.Combine(project.Root, "Intermediate", "ProjectFiles");
        return new(Path.Combine(project.Root, name + ".sln"), Path.Combine(dir, name + ".Editor.Target.csproj"),
            Path.Combine(dir, name + ".Editor.Target.cs"), Path.Combine(dir, "Properties", "launchSettings.json"),
            Path.Combine(dir, "Directory.Build.props"), Path.Combine(dir, "SharpEngine.Generated.props"),
            Path.Combine(dir, "SharpEngine.ProjectFiles.json"));
    }

    private static string Stamp(ResolvedSharpProject resolved) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, generatorVersion = GeneratorVersion,
        generatorBuildId = typeof(SolutionGenerator).Module.ModuleVersionId,
        projectManifestHash = Hash(resolved.Project.ManifestPath), engineManifestHash = Hash(resolved.Engine.ManifestPath),
        engineProjectsHash = EngineBuildGraph.Fingerprint(resolved.Engine.Root), engineRoot = resolved.Engine.Root,
        association = resolved.Project.EngineAssociation, modules = resolved.Project.Modules,
        plugins = resolved.Project.Plugins,
    }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

    private static void WriteGeneratedProps(string path, ResolvedSharpProject resolved)
    {
        var root = Path.TrimEndingDirectorySeparator(resolved.Engine.Root) + Path.DirectorySeparatorChar;
        WriteXml(path, new XElement("Project", new XElement("PropertyGroup",
            new XElement("SharpEngineGeneratedProjectFiles", "true"),
            new XElement("SharpEngineAssociation", resolved.Project.EngineAssociation), new XElement("SharpEngineRoot", root),
            new XElement("SharpEngineTarget", new XAttribute("Condition", "'$(SharpEngineTarget)' == ''"), "Editor"),
            new XElement("SharpEngineEditorApi", root + @"Binaries\Win64\$(Configuration)\Editor\SharpEngine.Editor.dll"),
            new XElement("SharpEngineRuntimeApi", root + @"Binaries\Win64\$(Configuration)\$(SharpEngineTarget)\SharpEngine.Runtime.dll"),
            new XElement("SharpEngineComponentGenerator", root + @"Binaries\Win64\$(Configuration)\Tools\SharpEngine.ComponentGenerator.dll"),
            new XElement("CustomAfterMicrosoftCommonTargets", root + @"Build\SharpEngine.Components.targets"))));
    }

    private static Entry[] WriteTargets(string directory, string displayName, EngineDescriptor engine, string? manifest)
    {
        Directory.CreateDirectory(directory);
        var name = SafeName(displayName);
        WriteXml(Path.Combine(directory, "Directory.Build.props"), new XElement("Project", new XElement("PropertyGroup",
            new XElement("BaseIntermediateOutputPath", Path.Combine(directory, @"obj\$(MSBuildProjectName)\")),
            new XElement("OutputPath", Path.Combine(directory, @"bin\$(MSBuildProjectName)\$(Configuration)\")),
            new XElement("AppendTargetFrameworkToOutputPath", "false"))));
        var result = new List<Entry>();
        foreach (var target in EngineBuildGraph.Targets)
        {
            var path = Path.Combine(directory, $"{name}.{target}.Target.csproj");
            var source = Path.ChangeExtension(path, ".cs");
            var script = Path.Combine(engine.Root, "Tools", manifest is null ? "SharpEngineBuild.ps1" : "SharpEngineProject.ps1");
            var common = $"-EngineRoot {Quote(engine.Root)} -Target {target} -Configuration $(Configuration) -Platform Win64";
            if (manifest is not null) common += " -ProjectFile " + Quote(manifest);
            WriteXml(path, new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("OutputType", "Exe"), new XElement("TargetFramework", "net8.0-windows"),
                    new XElement("ImplicitUsings", "enable"), new XElement("Nullable", "enable"),
                    new XElement("EnableDefaultCompileItems", "false"), new XElement("AssemblyName", $"{name}.{target}.Target"),
                    new XElement("ProjectGuid", Id(path))),
                new XElement("ItemGroup", new XElement("Compile", new XAttribute("Include", source))),
                new XElement("Target", new XAttribute("Name", "BuildSharpEngineTarget"), new XAttribute("AfterTargets", "Build"),
                    new XAttribute("Condition", "'$(DesignTimeBuild)' != 'true'"), new XElement("Exec", new XAttribute("Command",
                        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File {Quote(script)} -Command Build {common}"))),
                new XElement("Target", new XAttribute("Name", "CleanSharpEngineTarget"), new XAttribute("AfterTargets", "Clean"),
                    new XAttribute("Condition", "'$(DesignTimeBuild)' != 'true'"), new XElement("Exec", new XAttribute("Command",
                        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File {Quote(script)} -Command Clean {common}")))));
            WriteText(source, $$"""
                using System.Diagnostics;
                var configuration = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Name;
                var executable = Path.Combine({{JsonSerializer.Serialize(engine.Root)}}, "Binaries", "Win64", configuration, "{{target}}", "Engine.exe");
                if (!File.Exists(executable)) { Console.Error.WriteLine($"Build this Target first: {executable}"); return 1; }
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = {{JsonSerializer.Serialize(manifest is null ? engine.Root : Path.GetDirectoryName(manifest))}} };
                {{(manifest is null ? "" : "start.ArgumentList.Add(\"--project\"); start.ArgumentList.Add(" + JsonSerializer.Serialize(manifest) + ");")}}
                foreach (var argument in args) start.ArgumentList.Add(argument);
                start.Environment["SHARPENGINE_ROOT"] = {{JsonSerializer.Serialize(engine.Root)}};
                using var process = Process.Start(start) ?? throw new IOException("Could not start engine Target");
                process.WaitForExit();
                return process.ExitCode;
                """ + Environment.NewLine);
            result.Add(new($"{name}.{target} Target", path, Id(path), false, manifest is null ? "Engine" : "Project", target));
        }
        var profiles = new Dictionary<string, object>();
        foreach (var configuration in EngineBuildGraph.Configurations)
        foreach (var target in EngineBuildGraph.Targets)
            profiles[$"{configuration} {target}"] = new
            {
                commandName = "Executable",
                executablePath = Path.Combine(ProjectContracts.ResolveBinaries(engine, "Win64", configuration, target), "Engine.exe"),
                commandLineArgs = manifest is null ? "" : "--project " + Quote(manifest),
                workingDirectory = manifest is null ? engine.Root : Path.GetDirectoryName(manifest),
                environmentVariables = new Dictionary<string, string> { ["SHARPENGINE_ROOT"] = engine.Root },
            };
        WriteText(Path.Combine(directory, "Properties", "launchSettings.json"), JsonSerializer.Serialize(new { profiles }, new JsonSerializerOptions { WriteIndented = true }));
        return result.ToArray();
    }

    private static void WriteSolution(string path, EngineDescriptor engine, Entry[] wrappers, SharpProjectDescriptor? project)
    {
        var entries = EngineBuildGraph.Discover(engine.Root).Select(p => new Entry(p.Name, p.Path,
            p.Role == "NativeHost" ? XDocument.Load(p.Path).Descendants().Single(e => e.Name.LocalName == "ProjectGuid").Value.ToUpperInvariant() : Id(p.Path),
            p.Role == "NativeHost", "Engine", null)).ToList();
        if (project is not null) entries.AddRange(project.Modules.Select(module => Path.GetFullPath(Path.Combine(project.Root, module)))
            .Select(p => new Entry(Path.GetFileNameWithoutExtension(p), p, Id(p), false, "Project", null)));
        entries.AddRange(wrappers);
        var b = new StringBuilder("Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 18\n");
        foreach (var folder in new[] { "Engine", "Project" }) b.AppendLine($"Project(\"{FolderType}\") = \"{folder}\", \"{folder}\", \"{Id(path + folder)}\"\nEndProject");
        foreach (var e in entries) b.AppendLine($"Project(\"{(e.Native ? CppType : CsType)}\") = \"{e.Name}\", \"{Path.GetRelativePath(Path.GetDirectoryName(path)!, e.Path)}\", \"{e.Id}\"\nEndProject");
        b.AppendLine("Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        foreach (var c in EngineBuildGraph.Configurations)
        foreach (var t in EngineBuildGraph.Targets) b.AppendLine($"\t\t{c} {t}|Win64 = {c} {t}|Win64");
        b.AppendLine("\tEndGlobalSection\n\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var e in entries)
        foreach (var c in EngineBuildGraph.Configurations)
        foreach (var t in EngineBuildGraph.Targets)
        {
            var key = $"\t\t{e.Id}.{c} {t}|Win64";
            var value = c + (e.Native ? "|x64" : "|Any CPU");
            b.AppendLine($"{key}.ActiveCfg = {value}");
            if (e.Target == t) b.AppendLine($"{key}.Build.0 = {value}");
        }
        b.AppendLine("\tEndGlobalSection\n\tGlobalSection(NestedProjects) = preSolution");
        foreach (var e in entries) b.AppendLine($"\t\t{e.Id} = {Id(path + e.Folder)}");
        b.AppendLine("\tEndGlobalSection\nEndGlobal");
        WriteText(path, b.ToString());
    }

    private static string SafeName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string Quote(string value) => "\"" + value + "\"";
    private static string Id(string value) => "{" + new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())).AsSpan(0, 16)).ToString().ToUpperInvariant() + "}";
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void WriteXml(string path, XElement root) => WriteText(path, new XDocument(root).ToString());
    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
