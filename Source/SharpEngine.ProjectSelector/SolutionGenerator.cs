using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SharpEngine.Plugins;

namespace SharpEngine.ProjectSelector;

internal sealed record GeneratedSolution(string SolutionPath, string WrapperProjectPath,
    string TargetSourcePath, string LaunchSettingsPath, string TargetPropsPath,
    string GeneratedPropsPath, string StampPath, string SolutionTargetsPath);

internal sealed record GeneratedTargetFiles(string Project, string Source, string LaunchSettings);

internal static class SolutionGenerator
{
    private const int GeneratorVersion = 16;
    private const string CsType = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";
    private const string CppType = "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}";
    private const string FolderType = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
    private sealed record Entry(string Name, string Path, string Id, bool Native, string Folder,
        string? Target, string? Configuration = null);
    private sealed record PluginDisplay(string Scope, SharpPluginDescriptor Descriptor);

    internal static GeneratedSolution Generate(ResolvedSharpProject resolved)
    {
        var paths = Paths(resolved.Project);
        WriteGeneratedProps(paths.GeneratedPropsPath, resolved);
        var wrappers = WriteTargets(Path.GetDirectoryName(paths.StampPath)!, resolved.Project.DisplayName,
            resolved.Engine, resolved.Project.ManifestPath, resolved.Project.Targets,
            resolved.Project.RenderBackends);
        WriteSolution(paths.SolutionPath, resolved.Engine, wrappers, resolved.Project);
        WriteText(paths.StampPath, Stamp(resolved));
        return paths;
    }

    internal static void GenerateEngine(string root)
    {
        var engine = ProjectContracts.ReadEngine(root);
        var wrappers = WriteTargets(Path.Combine(engine.Root, "Intermediate", "ProjectFiles"), engine.Id, engine, null,
            EngineBuildGraph.Targets);
        WriteSolution(Path.Combine(engine.Root, "SharpEngine.sln"), engine, wrappers, null);
    }

    internal static GeneratedSolution EnsureCurrent(ResolvedSharpProject resolved)
    {
        var p = Paths(resolved.Project);
        var DirectoryPath = Path.GetDirectoryName(p.StampPath)!;
        var Name = SafeName(resolved.Project.DisplayName);
        IEnumerable<string> FilesFor(string Target)
        {
            foreach (var Configuration in EngineBuildGraph.Configurations)
            {
                var Files = TargetFiles(DirectoryPath, Name, Target, Configuration);
                yield return Files.Project;
                yield return Files.Source;
                yield return Files.LaunchSettings;
            }
        }
        var ExpectedTargetFiles = resolved.Project.Targets.SelectMany(FilesFor);
        var StaleTargetFiles = EngineBuildGraph.Targets.Except(resolved.Project.Targets,
            StringComparer.OrdinalIgnoreCase).SelectMany(FilesFor);
        if (new[] { p.SolutionPath, p.SolutionTargetsPath, p.LaunchSettingsPath, p.TargetPropsPath,
                p.GeneratedPropsPath, p.StampPath }.Concat(ExpectedTargetFiles).All(File.Exists) &&
            StaleTargetFiles.All(PathValue => !File.Exists(PathValue)) &&
            File.ReadAllText(p.StampPath) == Stamp(resolved)) return p;
        return Generate(resolved);
    }

    private static GeneratedSolution Paths(SharpProjectDescriptor project)
    {
        var name = SafeName(project.DisplayName);
        var dir = Path.Combine(project.Root, "Intermediate", "ProjectFiles");
        var Files = TargetFiles(dir, name, "Editor", "Debug");
        return new(Path.Combine(project.Root, name + ".sln"), Files.Project,
            Files.Source, Files.LaunchSettings,
            Path.Combine(dir, "Directory.Build.props"), Path.Combine(dir, "SharpEngine.Generated.props"),
            Path.Combine(dir, "SharpEngine.ProjectFiles.json"),
            Path.Combine(project.Root, $"after.{name}.sln.targets"));
    }

    internal static GeneratedTargetFiles TargetFiles(string DirectoryPath, string Name,
        string Target, string Configuration)
    {
        var Root = Path.Combine(DirectoryPath, "Targets", Configuration, Target);
        var Project = Path.Combine(Root, $"{Name}.{Configuration}.{Target}.Target.csproj");
        return new(Project, Path.ChangeExtension(Project, ".cs"),
            Path.Combine(Root, "Properties", "launchSettings.json"));
    }

    private static string Stamp(ResolvedSharpProject resolved) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, generatorVersion = GeneratorVersion,
        generatorBuildId = typeof(SolutionGenerator).Module.ModuleVersionId,
        projectManifestHash = Hash(resolved.Project.ManifestPath), engineManifestHash = Hash(resolved.Engine.ManifestPath),
        engineProjectsHash = EngineBuildGraph.Fingerprint(resolved.Engine.Root), engineRoot = resolved.Engine.Root,
        association = resolved.Project.EngineAssociation, modules = resolved.Project.Modules,
        targets = resolved.Project.Targets,
        plugins = PluginConfiguration.Read(resolved.Engine.ManifestPath, resolved.Project.ManifestPath).Effective.Select(plugin => new
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
            new XElement("SharpEngineNetworkingApi", root + @"Binaries\Win64\$(Configuration)\$(SharpEngineTarget)\SharpEngine.Networking.dll"),
            new XElement("SharpEngineComponentGenerator", root + @"Binaries\Win64\$(Configuration)\Tools\SharpEngine.ComponentGenerator.dll"),
            new XElement("CustomAfterMicrosoftCommonTargets", root + @"Build\SharpEngine.Components.targets"))));
    }

    private static Entry[] WriteTargets(string DirectoryPath, string DisplayName, EngineDescriptor Engine,
        string? Manifest, IReadOnlyList<string> Targets, string RenderBackends = "Both")
    {
        Directory.CreateDirectory(DirectoryPath);
        var Name = SafeName(DisplayName);
        WriteXml(Path.Combine(DirectoryPath, "Directory.Build.props"), new XElement("Project", new XElement("PropertyGroup",
            new XElement("BaseIntermediateOutputPath", Path.Combine(DirectoryPath, @"obj\$(MSBuildProjectName)\")),
            new XElement("OutputPath", Path.Combine(DirectoryPath, @"bin\$(MSBuildProjectName)\$(Configuration)\")),
            new XElement("AppendTargetFrameworkToOutputPath", "false"))));
        foreach (var StaleTarget in EngineBuildGraph.Targets.Except(Targets,
                     StringComparer.OrdinalIgnoreCase))
        {
            foreach (var Configuration in EngineBuildGraph.Configurations)
            {
                var Files = TargetFiles(DirectoryPath, Name, StaleTarget, Configuration);
                DeleteGeneratedFile(Files.Project);
                DeleteGeneratedFile(Files.Source);
                DeleteGeneratedFile(Files.LaunchSettings);
            }
        }
        // Remove only files owned by the previous generator, never user IDE settings.
        foreach (var LegacyTarget in EngineBuildGraph.Targets)
        {
            DeleteGeneratedFile(Path.Combine(DirectoryPath, $"{Name}.{LegacyTarget}.Target.csproj"));
            DeleteGeneratedFile(Path.Combine(DirectoryPath, $"{Name}.{LegacyTarget}.Target.cs"));
        }
        DeleteGeneratedFile(Path.Combine(DirectoryPath, "Properties", "launchSettings.json"));
        var Result = new List<Entry>();
        foreach (var Target in Targets)
        {
            foreach (var Configuration in EngineBuildGraph.Configurations)
            {
                Result.Add(WriteTarget(DirectoryPath, Name, Engine, Manifest, Target, Configuration, RenderBackends));
            }
        }
        return Result.ToArray();
    }

    private static void DeleteGeneratedFile(string PathValue)
    {
        if (File.Exists(PathValue))
        {
            File.Delete(PathValue);
        }
    }

    private static Entry WriteTarget(string DirectoryPath, string Name, EngineDescriptor Engine,
        string? Manifest, string Target, string Configuration, string RenderBackends)
    {
        var ExecutableName = Target == "Server" ? "DedicatedServer.exe" : "Engine.exe";
        var Files = TargetFiles(DirectoryPath, Name, Target, Configuration);
        var Script = Path.Combine(Engine.Root, "Tools", Manifest is null ? "SharpEngineBuild.ps1" : "SharpEngineProject.ps1");
        var Common = $"-EngineRoot {Quote(Engine.Root)} -Target {Target} -Configuration {Configuration} -Platform Win64 -RenderBackends $(SharpEngineRenderBackends)";
        if (Manifest is not null)
        {
            Common += " -ProjectFile " + Quote(Manifest);
        }
        WriteXml(Files.Project, new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("OutputType", "Exe"), new XElement("TargetFramework", "net8.0-windows"),
                    new XElement("ImplicitUsings", "enable"), new XElement("Nullable", "enable"),
                    new XElement("EnableDefaultCompileItems", "false"), new XElement("AssemblyName", $"{Name}.{Configuration}.{Target}.Target"),
                    new XElement("ProjectGuid", Id(Files.Project)),
                    new XElement("SharpEngineRenderBackends",
                        new XAttribute("Condition", "'$(SharpEngineRenderBackends)' == ''"), RenderBackends)),
                new XElement("ItemGroup", new XElement("Compile", new XAttribute("Include", Files.Source))),
                new XElement("Target", new XAttribute("Name", "BuildSharpEngineTarget"), new XAttribute("AfterTargets", "Build"),
                    new XAttribute("Condition", "'$(DesignTimeBuild)' != 'true'"), new XElement("Exec", new XAttribute("Command",
                        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File {Quote(Script)} -Command Build {Common}"))),
                new XElement("Target", new XAttribute("Name", "CleanSharpEngineTarget"), new XAttribute("AfterTargets", "Clean"),
                    new XAttribute("Condition", "'$(DesignTimeBuild)' != 'true'"), new XElement("Exec", new XAttribute("Command",
                        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File {Quote(Script)} -Command Clean {Common}")))));
        WriteText(Files.Source, $$"""
                using System.Diagnostics;
                const string Configuration = "{{Configuration}}";
                var Executable = Path.Combine({{JsonSerializer.Serialize(Engine.Root)}}, "Binaries", "Win64", Configuration, "{{Target}}", "{{ExecutableName}}");
                // This bootstrapper builds before the engine and must not depend on its assemblies.
                if (!File.Exists(Executable))
                {
                    Console.Error.WriteLine($"Build this Target first: {Executable}");
                    return 1;
                }
                var Start = new ProcessStartInfo(Executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = {{JsonSerializer.Serialize(Manifest is null ? Engine.Root : Path.GetDirectoryName(Manifest))}},
                };
                {{(Manifest is null ? "" : "Start.ArgumentList.Add(\"--project\");" + Environment.NewLine + "Start.ArgumentList.Add(" + JsonSerializer.Serialize(Manifest) + ");")}}
                foreach (var Argument in args)
                {
                    Start.ArgumentList.Add(Argument);
                }
                Start.Environment["SHARPENGINE_ROOT"] = {{JsonSerializer.Serialize(Engine.Root)}};
                using var ProcessInstance = Process.Start(Start) ?? throw new IOException("Could not start engine Target");
                ProcessInstance.WaitForExit();
                return ProcessInstance.ExitCode;
                """ + Environment.NewLine);
        var Profiles = new Dictionary<string, object>();
        Profiles[$"{Configuration} {Target}"] = new
        {
            commandName = "Executable",
            executablePath = Path.Combine(ProjectContracts.ResolveBinaries(Engine, "Win64", Configuration, Target),
                ExecutableName),
            commandLineArgs = Manifest is null ? "" : "--project " + Quote(Manifest),
            workingDirectory = Manifest is null ? Engine.Root : Path.GetDirectoryName(Manifest),
            environmentVariables = new Dictionary<string, string>
            {
                ["SHARPENGINE_ROOT"] = Engine.Root,
            },
        };
        WriteText(Files.LaunchSettings, JsonSerializer.Serialize(new { profiles = Profiles },
            new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        return new($"{Configuration} {Target} Target", Files.Project, Id(Files.Project), false,
            "BuildTarget", Target, Configuration);
    }

    private static void WriteSolution(string path, EngineDescriptor engine, Entry[] wrappers, SharpProjectDescriptor? project)
    {
        var solutionDirectory = Path.GetDirectoryName(path)!;
        var TargetNames = wrappers.Select(Wrapper => Wrapper.Target)
            .Where(Target => Target is not null).Select(Target => Target!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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

        foreach (var Wrapper in wrappers)
        {
            AddEntry(Wrapper);
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
        foreach (var t in TargetNames) b.AppendLine($"\t\t{c} {t}|Win64 = {c} {t}|Win64");
        b.AppendLine("\tEndGlobalSection\n\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var e in entries)
        foreach (var c in EngineBuildGraph.Configurations)
        foreach (var t in TargetNames)
        {
            var key = $"\t\t{e.Id}.{c} {t}|Win64";
            var value = (e.Configuration ?? c) + (e.Native ? "|x64" : "|Any CPU");
            b.AppendLine($"{key}.ActiveCfg = {value}");
            if (e.Target == t && e.Configuration == c)
            {
                b.AppendLine($"{key}.Build.0 = {value}");
            }
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
        WriteSolutionLaunchTargets(path, wrappers);
    }

    private static void WriteSolutionLaunchTargets(string SolutionPath, Entry[] Wrappers)
    {
        // MSBuild omits aliases for unchecked projects. Rider can explicitly launch
        // one of those projects while a different solution configuration is active.
        var Root = new XElement("Project");
        foreach (var Wrapper in Wrappers)
        {
            foreach (var Operation in new[] { "Build", "Rebuild", "Clean" })
            {
                var Name = $@"{Wrapper.Folder}\{Wrapper.Name}";
                var Properties = $"Configuration={Wrapper.Configuration};Platform=AnyCPU";
                var Node = new XElement("Target", new XAttribute("Name", Name + ":" + Operation));
                if (Operation != "Clean")
                {
                    Node.Add(new XElement("MSBuild", new XAttribute("Projects", Wrapper.Path),
                        new XAttribute("Targets", "Restore"), new XAttribute("Properties", Properties)));
                }
                Node.Add(new XElement("MSBuild", new XAttribute("Projects", Wrapper.Path),
                    new XAttribute("Targets", Operation), new XAttribute("Properties", Properties)));
                Root.Add(Node);
                if (Operation == "Build")
                {
                    Root.Add(new XElement("Target", new XAttribute("Name", Name),
                        new XAttribute("DependsOnTargets", Name + ":Build")));
                }
            }
        }
        WriteXml(Path.Combine(Path.GetDirectoryName(SolutionPath)!,
            "after." + Path.GetFileName(SolutionPath) + ".targets"), Root);
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
        foreach (var uri in PluginConfiguration.Read(engine.ManifestPath, project.ManifestPath).Effective)
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
