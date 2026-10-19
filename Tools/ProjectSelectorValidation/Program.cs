using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using SharpEngine.ProjectSelector;

var assertions = 0;
var engineRoot = FindEngineRoot();
var registryPath = @"Software\SharpEngine\Tests\" + Guid.NewGuid().ToString("N");
var temporary = Directory.CreateTempSubdirectory("SharpEngine-ProjectSelectorValidation-");
try
{
    ProjectFileAssociationValidation.Run(registryPath, temporary.FullName, Check);
    EngineFirstUseValidation.Run(registryPath, temporary.FullName, engineRoot, Check);
    if (args.Contains("--registration-only", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"PASS: {assertions} desktop registration assertions");
        return 0;
    }
    var store = new EngineAssociationStore(registryPath);
    const string association = "{11111111-2222-3333-4444-555555555555}";
    var registration = store.Register(engineRoot, association, installed: false);
    Check(registration.Association == association && SamePath(registration.Root, engineRoot),
        "Source Engine registration must preserve an explicit association");
    Check(SamePath(store.Resolve(association)!, engineRoot),
        "Association lookup must return the registered source Engine root");
    Check(store.List().Count == 1 && store.List()[0].Association == association,
        "Association listing must include the registered source Engine");
    Check(store.Register(engineRoot, null, installed: false).Association == association,
        "Registering the same source root must reuse its existing association");

    var projectRoot = Directory.CreateDirectory(Path.Combine(temporary.FullName,
        "ForeignProject")).FullName;
    var runtimeDirectory = Directory.CreateDirectory(Path.Combine(projectRoot,
        "Source", "ForeignGame")).FullName;
    var editorDirectory = Directory.CreateDirectory(Path.Combine(projectRoot,
        "Source", "ForeignGame.Editor")).FullName;
    File.WriteAllText(Path.Combine(runtimeDirectory, "ForeignGame.csproj"),
        "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    File.WriteAllText(Path.Combine(editorDirectory, "ForeignGame.Editor.csproj"),
        "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    var pluginDirectory = Directory.CreateDirectory(Path.Combine(projectRoot,
        "Plugins", "ForeignPlugin")).FullName;
    var pluginModuleDirectory = Directory.CreateDirectory(Path.Combine(pluginDirectory,
        "Source", "ForeignPlugin.Runtime")).FullName;
    var pluginAssetDirectory = Directory.CreateDirectory(Path.Combine(pluginDirectory,
        "Content", "Assets")).FullName;
    File.WriteAllText(Path.Combine(pluginAssetDirectory, "assets.json"),
        "{\"schemaVersion\":1,\"assets\":[]}\n");
    var pluginModulePath = Path.Combine(pluginModuleDirectory, "ForeignPlugin.Runtime.csproj");
    File.WriteAllText(pluginModulePath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    File.WriteAllText(Path.Combine(pluginDirectory, "ForeignPlugin.sharpplugin"),
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = "foreign.plugin",
            version = "1.0.0",
            modules = new[] { "Source/ForeignPlugin.Runtime/ForeignPlugin.Runtime.csproj" },
            assets = new { manifest = "Content/Assets/assets.json" },
        }, new JsonSerializerOptions { WriteIndented = true }));
    var manifestPath = Path.Combine(projectRoot, "ForeignGame.sharpproject");
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
    {
        schemaVersion = 3,
        projectId = "foreign-game",
        displayName = "ForeignGame",
        engine = new
        {
            id = "SharpEngine",
            version = "0.2.0-dev",
            association,
            manifest = "engine.json",
        },
        modules = new[]
        {
            "Source/ForeignGame/ForeignGame.csproj",
            "Source/ForeignGame.Editor/ForeignGame.Editor.csproj",
        },
        plugins = new[]
        {
            "project://Plugins/ForeignPlugin/ForeignPlugin.sharpplugin",
            "engine://Plugins/EngineTestPlugin/EngineTestPlugin.sharpplugin",
        },
    }, new JsonSerializerOptions { WriteIndented = true }));

    var project = ProjectContracts.ReadProject(manifestPath);
    var engine = ProjectContracts.ReadEngine(engineRoot, project.EngineManifest);
    ProjectContracts.ValidateBinding(project, engine);
    Check(project.Plugins.Count == 2 &&
          project.Plugins.Any(plugin => plugin.StartsWith("project://", StringComparison.Ordinal)) &&
          project.Plugins.Any(plugin => plugin.StartsWith("engine://", StringComparison.Ordinal)),
        "Project manifest must preserve project- and engine-level Plugin URIs");
    var enginePlugin = PluginContracts.Read(ProjectContracts.ResolvePluginManifest(project.Root,
        engine.Root, "engine://Plugins/EngineTestPlugin/EngineTestPlugin.sharpplugin"));
    Check(enginePlugin.Modules.Count == 2 &&
          enginePlugin.Modules.Any(module => module.Contains("EngineTestPlugin.Runtime", StringComparison.Ordinal)) &&
          enginePlugin.Modules.Any(module => module.Contains("EngineTestPlugin.Editor", StringComparison.Ordinal)),
        "Engine-level test Plugin must expose Runtime and Editor Modules");
    var foreignPlugin = PluginContracts.Read(ProjectContracts.ResolvePluginManifest(project.Root,
        engine.Root, "project://Plugins/ForeignPlugin/ForeignPlugin.sharpplugin"));
    Check(foreignPlugin.AssetManifest == "Content/Assets/assets.json",
        "Plugin manifests must validate and expose an optional Content asset manifest");
    var generated = SolutionGenerator.Generate(new ResolvedSharpProject(project, engine));
    var firstSolution = File.ReadAllText(generated.SolutionPath);
    var firstStamp = File.ReadAllText(generated.StampPath);
    var firstWrapper = File.ReadAllText(generated.WrapperProjectPath);
    var launchSettings = File.ReadAllText(generated.LaunchSettingsPath);
    Check(File.Exists(generated.SolutionPath) && File.Exists(generated.GeneratedPropsPath) &&
          File.Exists(generated.WrapperProjectPath) && File.Exists(generated.TargetSourcePath) &&
          File.Exists(generated.LaunchSettingsPath) && File.Exists(generated.TargetPropsPath) &&
          File.Exists(generated.StampPath),
        "Solution generation must atomically create every derived project file");
    Check(firstSolution.Contains("Debug Editor|Win64", StringComparison.Ordinal) &&
          firstSolution.Contains("Development Editor|Win64", StringComparison.Ordinal) &&
          firstSolution.Contains("Shipping Editor|Win64", StringComparison.Ordinal),
        "Generated Solution must expose only the approved Editor configurations");
    Check(firstSolution.Contains("Editor Target", StringComparison.Ordinal) &&
          firstSolution.Contains(Path.Combine(engineRoot, "Source", "Engine",
              "Engine.vcxproj"), StringComparison.OrdinalIgnoreCase),
        "Source Engine and generated Editor Target must coexist in the Solution");
    var engineProjects = EngineBuildGraph.Discover(engineRoot);
    Check(engineProjects.Any(project => project.ModuleId == "SharpEngine.Timeline.Runtime" &&
            project.ModuleType == "Runtime") &&
          engineProjects.Any(project => project.ModuleId == "SharpEngine.Timeline.Editor" &&
            project.ModuleType == "Editor"),
        "Engine discovery must expose Timeline Runtime and Editor as explicit Modules");
    Check(firstSolution.Contains(Path.Combine(engineRoot, "Source",
            "SharpEngine.Timeline.Runtime", "SharpEngine.Timeline.Runtime.csproj"),
            StringComparison.OrdinalIgnoreCase) &&
          firstSolution.Contains(Path.Combine(engineRoot, "Source",
            "SharpEngine.Timeline.Editor", "SharpEngine.Timeline.Editor.csproj"),
            StringComparison.OrdinalIgnoreCase),
        "Generated Engine Solution must include Timeline Runtime and Editor projects");
    string SolutionId(string name) => System.Text.RegularExpressions.Regex.Match(firstSolution,
        "= \"" + System.Text.RegularExpressions.Regex.Escape(name) + "\", \"[^\"]+\", \"([^\"]+)\"").Groups[1].Value;
    var componentGeneratorGuid = SolutionId("ComponentGenerator");
    foreach (var configuration in new[] { "Debug", "Development", "Shipping" })
    foreach (var target in new[] { "Editor", "Game" })
        Check(firstSolution.Contains(componentGeneratorGuid + "." + configuration +
                " " + target + "|Win64.ActiveCfg = " + configuration + "|Any CPU", StringComparison.Ordinal) &&
            firstSolution.Contains(SolutionId(target + " Target") + "." + configuration +
                " " + target + "|Win64.Build.0", StringComparison.Ordinal),
            "Each configuration must select its own unified Target: " + configuration + " " + target);
    var nativeEnginePath = Path.Combine(engineRoot, "Source", "Engine", "Engine.vcxproj");
    var nativeEngineDocument = XDocument.Load(nativeEnginePath);
    var nativeEngineGuid = nativeEngineDocument.Descendants().First(element =>
        element.Name.LocalName == "ProjectGuid").Value.ToUpperInvariant();
    var targetStart = firstSolution.IndexOf("\"Editor Target\"",
        StringComparison.Ordinal);
    var targetEnd = firstSolution.IndexOf("EndProject", targetStart, StringComparison.Ordinal);
    var targetBlock = firstSolution[targetStart..targetEnd];
    Check(!targetBlock.Contains("ProjectDependencies", StringComparison.Ordinal) &&
        EngineBuildGraph.Discover(engineRoot).Count(p => p.Role == "Host") == 1,
        "The unified builder must own the graph without a duplicate solution dependency list");
    var nativeEngineProject = File.ReadAllText(nativeEnginePath);
    Check(!nativeEngineProject.Contains("<ProjectReference", StringComparison.Ordinal) &&
          !nativeEngineProject.Contains("..\\SharpEngine.Host", StringComparison.Ordinal),
        "Native Engine must not expose or invoke managed projects outside the Solution graph");
    Check(firstSolution.Contains("{2150E333-8FDC-42A3-9474-1A3956D46DE8}",
              StringComparison.OrdinalIgnoreCase) &&
          !firstSolution.Contains("= \"Build\", \"Build\"", StringComparison.Ordinal),
        "Generated Solution must use real Solution folders without exposing Build plumbing");
    Check(firstSolution.Contains("= \"BuildTarget\", \"BuildTarget\"", StringComparison.Ordinal),
        "Generated Solution must expose a dedicated BuildTarget Solution folder");
    var buildTargetFolderGuid = SolutionId("BuildTarget");
    var projectModuleGuid = SolutionId("ForeignGame");
    var engineSourceFolderGuid = StableId(generated.SolutionPath + "Engine/Source");
    var projectSourceFolderGuid = StableId(generated.SolutionPath + "Project/Source");
    var enginePluginFolderGuid = StableId(generated.SolutionPath + "Engine/Plugins/EngineTestPlugin/Source");
    var projectPluginFolderGuid = StableId(generated.SolutionPath + "Project/Plugins/ForeignPlugin/Source");
    Check(firstSolution.Contains(SolutionId("Runtime") + " = " + engineSourceFolderGuid,
            StringComparison.Ordinal) &&
          firstSolution.Contains(projectModuleGuid + " = " + projectSourceFolderGuid,
            StringComparison.Ordinal) &&
          firstSolution.Contains(SolutionId("Editor Target") + " = " + buildTargetFolderGuid,
            StringComparison.Ordinal),
        "Engine Modules, project Modules, and build Targets must be nested in separate Solution folders");
    Check(firstSolution.Contains(SolutionId("EngineTestPlugin.Runtime") + " = " + enginePluginFolderGuid,
            StringComparison.Ordinal) &&
          firstSolution.Contains(SolutionId("ForeignPlugin.Runtime") + " = " + projectPluginFolderGuid,
            StringComparison.Ordinal) &&
          firstSolution.Contains("EngineTestPlugin.sharpplugin", StringComparison.Ordinal) &&
          firstSolution.Contains("ForeignPlugin.sharpplugin", StringComparison.Ordinal),
        "Engine and project Plugin Modules must be nested under their Plugin folders");
    Check(!firstSolution.Contains(projectModuleGuid + ".Debug Editor|Win64.Build.0",
            StringComparison.Ordinal),
        "User Module projects must not participate in Solution Build directly");
    Check(firstWrapper.Contains("-Command Build", StringComparison.Ordinal) &&
          firstWrapper.Contains("BuildSharpEngineTarget", StringComparison.Ordinal) &&
          !firstWrapper.Contains("MakeFileProj", StringComparison.Ordinal),
        "SDK Editor Target must own unified Build without NMake");
    Check(launchSettings.Contains("Debug Editor", StringComparison.Ordinal) &&
          launchSettings.Contains("Development Editor", StringComparison.Ordinal) &&
          launchSettings.Contains("Shipping Editor", StringComparison.Ordinal),
        "Editor Target must publish launch profiles for every approved configuration");
    var props = File.ReadAllText(generated.GeneratedPropsPath);
    Check(props.Contains("SharpEngineEditorApi", StringComparison.Ordinal) &&
          props.Contains("SharpEngineRuntimeApi", StringComparison.Ordinal) &&
          !props.Contains("SharpEngineUnifiedBuild>true", StringComparison.Ordinal),
        "Generated design-time props must resolve APIs without authorizing direct builds");
    var generatedProps = XDocument.Load(generated.GeneratedPropsPath);
    var generatorPath = generatedProps.Descendants("SharpEngineComponentGenerator").Single().Value;
    var componentTargets = generatedProps.Descendants("CustomAfterMicrosoftCommonTargets").Single().Value;
    Check(generatorPath == Path.Combine(engineRoot,
            @"Binaries\Win64\$(Configuration)\Tools\SharpEngine.ComponentGenerator.dll") &&
        componentTargets == Path.Combine(engineRoot, @"Build\SharpEngine.Components.targets"),
        "Runtime Module IDE builds must resolve the generator for the selected configuration");

    var sentinelTime = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(generated.SolutionPath, sentinelTime);
    _ = SolutionGenerator.EnsureCurrent(new ResolvedSharpProject(project, engine));
    Check(File.GetLastWriteTimeUtc(generated.SolutionPath) == sentinelTime,
        "Fresh project-file stamps must avoid rewriting the generated Solution");
    // Version 4 predates component-generator properties. Existing user projects
    // must be regenerated even when their project/engine manifests are unchanged.
    var legacyStamp = System.Text.Json.Nodes.JsonNode.Parse(firstStamp)!.AsObject();
    legacyStamp["generatorVersion"] = 4;
    File.WriteAllText(generated.StampPath, legacyStamp.ToJsonString());
    generatedProps.Descendants("SharpEngineComponentGenerator").Remove();
    generatedProps.Descendants("CustomAfterMicrosoftCommonTargets").Remove();
    generatedProps.Save(generated.GeneratedPropsPath);
    _ = SolutionGenerator.EnsureCurrent(new ResolvedSharpProject(project, engine));
    Check(File.ReadAllText(generated.GeneratedPropsPath) == props &&
        File.ReadAllText(generated.StampPath) == firstStamp,
        "Version 4 project files must regenerate the component-generator path and imports");
    // Version 9 launchers referenced SharpLog without an engine assembly reference.
    // Exercise migration of both cached launchers before compiling the generated code.
    var LegacyLoggingStamp = System.Text.Json.Nodes.JsonNode.Parse(firstStamp)!.AsObject();
    LegacyLoggingStamp["generatorVersion"] = 9;
    File.WriteAllText(generated.StampPath, LegacyLoggingStamp.ToJsonString());
    foreach (var Target in new[]
    {
        "Editor",
        "Game",
    })
    {
        var LegacySource = generated.TargetSourcePath.Replace(".Editor.Target.cs",
            $".{Target}.Target.cs", StringComparison.Ordinal);
        File.WriteAllText(LegacySource, "SharpLog.Error(\"ProjectSelector\", \"Build this Target first\");");
    }
    _ = SolutionGenerator.EnsureCurrent(new ResolvedSharpProject(project, engine));
    TargetWrapperCompileValidation.Run(generated.WrapperProjectPath, Check);
    _ = SolutionGenerator.Generate(new ResolvedSharpProject(project, engine));
    Check(firstSolution == File.ReadAllText(generated.SolutionPath) &&
          firstStamp == File.ReadAllText(generated.StampPath),
        "Repeated Solution generation must be deterministic");
    Check(store.Unregister(association) && store.Resolve(association) is null,
        "Unregister must remove only the selected Engine association");
    var installed = store.Register(engineRoot, null, installed: true);
    Check(installed.Association == "0.2" && store.Unregister(installed.Association),
        "Installed Engine registration must use a stable major.minor association");
    var launchIndex = Array.IndexOf(args, "--game-launch");
    if (launchIndex >= 0)
        TargetLaunchValidation.Run(engineRoot, args[launchIndex + 1],
            args.Length > launchIndex + 2 ? args[launchIndex + 2] : "Development", Check);
    Console.WriteLine($"PASS: {assertions} Project Selector assertions");
    return 0;
}
finally
{
    DeleteTemporaryDirectory(temporary);
    try { Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false); }
    catch (ArgumentException) { }
    using var testsKey = Registry.CurrentUser.OpenSubKey(
        @"Software\SharpEngine\Tests", writable: false);
    if (testsKey is not null && testsKey.GetSubKeyNames().Length == 0 &&
        testsKey.GetValueNames().Length == 0)
    {
        testsKey.Dispose();
        Registry.CurrentUser.DeleteSubKey(@"Software\SharpEngine\Tests",
            throwOnMissingSubKey: false);
    }
}

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++;
}

static void DeleteTemporaryDirectory(DirectoryInfo Temporary)
{
    // Child-process outputs can briefly remain locked on Windows after exit.
    for (var Attempt = 0; ; Attempt++)
    {
        try
        {
            Temporary.Delete(recursive: true);
            return;
        }
        catch (IOException) when (Attempt < 20)
        {
            Thread.Sleep(100);
        }
    }
}

static string FindEngineRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
         directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "engine.json")))
            return directory.FullName;
    throw new DirectoryNotFoundException("Could not locate the SharpEngine root");
}

static bool SamePath(string left, string right) =>
    string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

static string StableId(string value) => "{" + new Guid(SHA256.HashData(
    Encoding.UTF8.GetBytes(value.ToUpperInvariant())).AsSpan(0, 16)).ToString().ToUpperInvariant() + "}";
