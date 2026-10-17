using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpEngine.Logging;
using System.Xml.Linq;

namespace SharpEngine.ProjectSelector;

internal sealed record GeneratedSolution(string SolutionPath, string WrapperProjectPath,
    string TargetSourcePath, string LaunchSettingsPath, string TargetPropsPath,
    string GeneratedPropsPath, string StampPath);

internal static class SolutionGenerator
{
    private const int GeneratorVersion = 9;
    private const string CsType = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";
    private const string CppType = "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}";
    private const string FolderType = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
    private sealed record Entry(string Name, string Path, string Id, bool Native, string Folder, string? Target);
    private sealed record PluginDisplay(string Scope, SharpPluginDescriptor Descriptor);

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
        plugins = resolved.Project.Plugins.Select(plugin => new
        {
            uri = plugin,
            manifestHash = Hash(ProjectContracts.ResolvePluginManifest(
                resolved.Project.Root, resolved.Engine.Root, plugin)),
        }).ToArray(),
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
                if (!File.Exists(executable)) { SharpLog.Error("ProjectSelector", $"Build this Target first: {executable}"); return 1; }
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = {{JsonSerializer.Serialize(manifest is null ? engine.Root : Path.GetDirectoryName(manifest))}} };
                {{(manifest is null ? "" : "start.ArgumentList.Add(\"--project\"); start.ArgumentList.Add(" + JsonSerializer.Serialize(manifest) + ");")}}
                foreach (var argument in args) start.ArgumentList.Add(argument);
                start.Environment["SHARPENGINE_ROOT"] = {{JsonSerializer.Serialize(engine.Root)}};
                using var process = Process.Start(start) ?? throw new IOException("Could not start engine Target");
                process.WaitForExit();
                return process.ExitCode;
                """ + Environment.NewLine);
            // Target wrappers are build entry points generated for the solution. Keep them
            // separate from the source projects they build so editing a Module project does
            // not look like editing the build graph itself.
            result.Add(new($"{target} Target", path, Id(path), false, "BuildTarget", target));
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
        var solutionDirectory = Path.GetDirectoryName(path)!;
        var entries = new List<Entry>();
        var entryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootFolders = project is null
            ? new[] { "Engine", "BuildTarget" }
            : new[] { "Engine", "Project", "BuildTarget" };
        var folders = new HashSet<string>(rootFolders,
            StringComparer.OrdinalIgnoreCase);
        var solutionItems = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        void EnsureFolder(string folder)
        {
            var current = string.Empty;
            foreach (var part in folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.Length == 0 ? part : current + "/" + part;
                folders.Add(current);
            }
        }

        void AddEntry(Entry entry)
        {
            if (!entryPaths.Add(entry.Path)) return;
            EnsureFolder(entry.Folder);
            entries.Add(entry);
        }

        void AddSolutionItem(string folder, string fullPath)
        {
            if (!File.Exists(fullPath)) return;
            EnsureFolder(folder);
            var relative = Path.GetRelativePath(solutionDirectory, fullPath)
                .Replace(Path.DirectorySeparatorChar, '\\')
                .Replace(Path.AltDirectorySeparatorChar, '\\');
            if (!solutionItems.TryGetValue(folder, out var items))
                solutionItems[folder] = items = [];
            if (!items.Contains(relative, StringComparer.OrdinalIgnoreCase)) items.Add(relative);
        }

        void AddDirectoryItems(string folder, string root)
        {
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)
                             .Any(part => part is "bin" or "obj" or "Intermediate")))
                AddSolutionItem(folder, file);
        }

        EnsureFolder("Engine/Source");
        EnsureFolder("Engine/Plugins");
        if (project is not null)
        {
            EnsureFolder("Project/Source");
            EnsureFolder("Project/Plugins");
        }
        AddSolutionItem("Engine", engine.ManifestPath);
        AddDirectoryItems("Engine/Config", Path.Combine(engine.Root, "Config"));
        foreach (var p in EngineBuildGraph.Discover(engine.Root))
            AddEntry(new(EngineDisplayName(p.Name), p.Path,
                p.Role == "NativeHost" ? XDocument.Load(p.Path).Descendants()
                    .Single(e => e.Name.LocalName == "ProjectGuid").Value.ToUpperInvariant() : Id(p.Path),
                p.Role == "NativeHost", "Engine/Source", null));

        if (project is not null)
        {
            AddSolutionItem("Project", project.ManifestPath);
            AddDirectoryItems("Project/Config", Path.Combine(project.Root, "Config"));
            foreach (var module in project.Modules)
            {
                var modulePath = Path.GetFullPath(Path.Combine(project.Root, module));
                AddEntry(new(ProjectModuleDisplayName(project, Path.GetFileNameWithoutExtension(modulePath)), modulePath,
                    Id(modulePath), false, "Project/Source", null));
            }
        }

        foreach (var plugin in ResolvePlugins(engine, project))
        {
            var pluginName = SafeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(plugin.Descriptor.Root)));
            var folder = $"{plugin.Scope}/Plugins/{pluginName}";
            var sourceFolder = folder + "/Source";
            AddSolutionItem(folder, plugin.Descriptor.ManifestPath);
            foreach (var module in plugin.Descriptor.Modules)
            {
                var modulePath = ProjectContracts.ResolvePluginModule(plugin.Descriptor, module);
                AddEntry(new(Path.GetFileNameWithoutExtension(modulePath), modulePath,
                    Id(modulePath), false, sourceFolder, null));
            }
        }

        foreach (var wrapper in wrappers)
        {
            AddEntry(wrapper);
            AddSolutionItem("BuildTarget", Path.ChangeExtension(wrapper.Path, ".cs"));
        }

        var folderIds = folders.ToDictionary(folder => folder, folder => Id(path + folder),
            StringComparer.OrdinalIgnoreCase);
        static int FolderDepth(string folder) => folder.Count(character => character == '/');
        static int RootOrder(string folder) => folder.Split('/')[0] switch
        {
            "Engine" => 0,
            "Project" => 1,
            "BuildTarget" => 2,
            _ => 3,
        };
        static int ChildOrder(string folder)
        {
            var parts = folder.Split('/');
            if (parts.Length != 2) return 100;
            return parts[1] switch
            {
                "Plugins" => 0,
                "Source" => 1,
                "Config" => 2,
                _ => 3,
            };
        }
        var orderedFolders = folders.OrderBy(FolderDepth).ThenBy(RootOrder)
            .ThenBy(ChildOrder)
            .ThenBy(folder => folder, StringComparer.Ordinal).ToArray();
        var b = new StringBuilder("Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 18\n");
        foreach (var folder in orderedFolders)
        {
            var name = folder[(folder.LastIndexOf('/') + 1)..];
            b.AppendLine($"Project(\"{FolderType}\") = \"{name}\", \"{name}\", \"{folderIds[folder]}\"");
            if (solutionItems.TryGetValue(folder, out var items) && items.Count > 0)
            {
                b.AppendLine("ProjectSection(SolutionItems) = preProject");
                foreach (var item in items.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
                    b.AppendLine($"\t\t{item} = {item}");
                b.AppendLine("EndProjectSection");
            }
            b.AppendLine("EndProject");
        }
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
        foreach (var folder in orderedFolders)
        {
            var separator = folder.LastIndexOf('/');
            if (separator >= 0)
                b.AppendLine($"\t\t{folderIds[folder]} = {folderIds[folder[..separator]]}");
        }
        foreach (var e in entries) b.AppendLine($"\t\t{e.Id} = {folderIds[e.Folder]}");
        b.AppendLine("\tEndGlobalSection\nEndGlobal");
        WriteText(path, b.ToString());
    }

    private static IEnumerable<PluginDisplay> ResolvePlugins(EngineDescriptor engine,
        SharpProjectDescriptor? project)
    {
        if (project is null)
        {
            var pluginsRoot = Path.Combine(engine.Root, "Plugins");
            if (!Directory.Exists(pluginsRoot)) yield break;
            foreach (var manifest in Directory.EnumerateFiles(pluginsRoot, "*.sharpplugin",
                         SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                yield return new("Engine", PluginContracts.Read(manifest));
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in project.Plugins)
        {
            var manifest = ProjectContracts.ResolvePluginManifest(project.Root, engine.Root, uri);
            if (!seen.Add(manifest)) continue;
            yield return new(uri.StartsWith("engine://", StringComparison.OrdinalIgnoreCase)
                ? "Engine" : "Project", PluginContracts.Read(manifest));
        }
    }

    private static string SafeName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string EngineDisplayName(string name) => StripPrefix(name, "SharpEngine");

    private static string ProjectModuleDisplayName(SharpProjectDescriptor project, string name)
    {
        var prefixes = new List<string> { SafeName(project.DisplayName), Path.GetFileName(project.Root) };
        prefixes.AddRange(project.Modules.Select(module =>
            Path.GetFileName(Path.GetDirectoryName(module.Replace('/', Path.DirectorySeparatorChar))))
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))!);
        foreach (var prefix in prefixes.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(prefix => prefix.Length))
        {
            var stripped = StripPrefix(name, prefix);
            if (!string.Equals(stripped, name, StringComparison.Ordinal)) return stripped;
        }
        return name;
    }

    private static string StripPrefix(string name, string prefix)
    {
        var qualifiedPrefix = prefix + ".";
        return name.StartsWith(qualifiedPrefix, StringComparison.OrdinalIgnoreCase)
            ? name[qualifiedPrefix.Length..]
            : name;
    }

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
