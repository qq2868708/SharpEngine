using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: SharpEngine.ProjectBuild [build] <project.sharpproject> [--target Editor|Game] [--configuration Debug|Development|Shipping] [--platform Win64]");
    return 2;
}

var offset = args[0].Equals("build", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
if (args.Length <= offset) return 2;
var projectManifest = Path.GetFullPath(args[offset]);
var target = Option("--target", "Editor");
var configuration = Option("--configuration", "Debug");
var platform = Option("--platform", "Win64");
if (target is not ("Editor" or "Game")) Fail("SEBUILD001", $"Unknown target '{target}'");
if (configuration is not ("Debug" or "Development" or "Shipping"))
    Fail("SEBUILD002", $"Unknown configuration '{configuration}'");
if (platform != "Win64") Fail("SEBUILD003", $"Unsupported platform '{platform}'");
if (!File.Exists(projectManifest)) Fail("SEBUILD004", $"Project manifest not found: {projectManifest}");

using var projectDocument = JsonDocument.Parse(File.ReadAllText(projectManifest));
var projectJson = projectDocument.RootElement;
if (projectJson.GetProperty("schemaVersion").GetInt32() != 3)
    Fail("SEBUILD005", "Project manifest schemaVersion must be 3");
var projectRoot = Path.GetDirectoryName(projectManifest)!;
var projectId = RequiredString(projectJson, "projectId");
var engine = projectJson.GetProperty("engine");
var engineRoot = ResolveEngineRoot(engine, Option("--engine-root", string.Empty));
var engineManifestPath = Path.Combine(engineRoot,
    engine.TryGetProperty("manifest", out var engineManifestValue)
        ? engineManifestValue.GetString() ?? "engine.json"
        : "engine.json");
if (!File.Exists(engineManifestPath))
    Fail("SEBUILD009", $"Engine manifest not found: {engineManifestPath}");

string resolvedEngineId;
string resolvedEngineVersion;
string binariesTemplate;
string intermediateTemplate;
using (var engineDocument = JsonDocument.Parse(File.ReadAllText(engineManifestPath)))
{
    var manifest = engineDocument.RootElement;
    if (!manifest.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 2)
        Fail("SEBUILD012", "Engine manifest schemaVersion must be 2");
    var requestedId = RequiredString(engine, "id");
    var requestedVersion = RequiredString(engine, "version");
    resolvedEngineId = RequiredString(manifest, "engineId");
    resolvedEngineVersion = RequiredString(manifest, "version");
    var paths = manifest.GetProperty("paths");
    binariesTemplate = RequiredString(paths, "binaries");
    intermediateTemplate = RequiredString(paths, "intermediate");
    if (!string.Equals(requestedId, resolvedEngineId, StringComparison.Ordinal))
        Fail("SEBUILD010", $"Engine id mismatch: requested '{requestedId}', resolved '{resolvedEngineId}'");
    if (!string.Equals(requestedVersion, resolvedEngineVersion, StringComparison.Ordinal))
        Fail("SEBUILD011", $"Engine version mismatch: requested '{requestedVersion}', resolved '{resolvedEngineVersion}'");
}

var engineOutputDirectory = ResolveLayoutPath(engineRoot, binariesTemplate,
    platform, configuration, target);
var managedApi = Path.Combine(ResolveLayoutPath(engineRoot, binariesTemplate,
    platform, configuration, "Editor"), "SharpEngine.Editor.dll");
var modulesApi = Path.Combine(engineOutputDirectory, "SharpEngine.Runtime.dll");
if ((target == "Editor" && !File.Exists(managedApi)) || !File.Exists(modulesApi))
    Fail("SEBUILD006", $"SharpEngine managed APIs are not built: {managedApi}, {modulesApi}");

var outputDirectory = ResolveLayoutPath(projectRoot, binariesTemplate,
    platform, configuration, target);
var intermediateDirectory = ResolveLayoutPath(projectRoot, intermediateTemplate,
    platform, configuration, target);
var projectBuildDirectory = Path.Combine(intermediateDirectory, "ProjectBuild");
Directory.CreateDirectory(projectBuildDirectory);
// CLI and Editor builds share the same output and intermediate directories.
using var buildLock = AcquireBuildLock(Path.Combine(projectBuildDirectory, "build.lock"));
var validationOutput = Path.Combine(projectBuildDirectory, "Validation");
var publishInputs = Path.Combine(projectBuildDirectory, "PublishInputs");
var packageDirectory = Path.Combine(projectBuildDirectory, "Package");
var moduleIntermediate = Path.Combine(intermediateDirectory, "Modules");
PrepareDirectory(validationOutput);
PrepareDirectory(publishInputs);
PrepareDirectory(packageDirectory);
Directory.CreateDirectory(moduleIntermediate);

var projectPaths = projectJson.GetProperty("modules").EnumerateArray()
    .Select(value => Path.GetFullPath(Path.Combine(projectRoot,
        value.GetString() ?? string.Empty)))
    .ToArray();
var sourceRoot = Path.GetFullPath(Path.Combine(projectRoot, "Source"))
    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
foreach (var path in projectPaths)
    if (!path.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
        Fail("SEMOD014", $"Module projects must remain under Source: {path}");
if (projectPaths.Length == 0) Fail("SEMOD001", "A project requires at least one Module");
if (projectPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != projectPaths.Length)
    Fail("SEMOD002", "The project contains a duplicate Module project path");

var plugins = ReadPlugins(projectJson, projectRoot, engineRoot);
var pluginModulePaths = plugins.SelectMany(plugin => plugin.Modules.Select(path =>
    new ModuleSource(path))).ToArray();
var allModuleSources = projectPaths.Select(path =>
    new ModuleSource(path)).Concat(pluginModulePaths).ToArray();
if (allModuleSources.Select(source => source.ProjectPath)
        .Distinct(StringComparer.OrdinalIgnoreCase).Count() != allModuleSources.Length)
    Fail("SEMOD002", "The project contains a duplicate Module project path");

var modules = allModuleSources.Select(source =>
{
    return EvaluateModule(source.ProjectPath, configuration,
        platform, target, validationOutput, moduleIntermediate, managedApi, modulesApi);
}).ToArray();
foreach (var module in modules)
    if (!string.Equals(Path.GetDirectoryName(module.TargetPath), validationOutput,
            StringComparison.OrdinalIgnoreCase))
        Fail("SEMOD015", $"Module '{module.Id}' ignored the unified output directory: {module.TargetPath}");
var duplicateAssembly = modules.GroupBy(module => module.AssemblyName,
        StringComparer.OrdinalIgnoreCase)
    .FirstOrDefault(group => group.Count() > 1);
if (duplicateAssembly is not null)
    Fail("SEMOD019", $"Modules must not share assembly name '{duplicateAssembly.Key}'");

var byPath = modules.ToDictionary(module => module.ProjectPath,
    StringComparer.OrdinalIgnoreCase);
var byId = new Dictionary<string, ModuleInfo>(StringComparer.OrdinalIgnoreCase);
foreach (var module in modules)
{
    if (string.IsNullOrWhiteSpace(module.Id))
        Fail("SEMOD003", $"Module type is not declared in {module.ProjectPath}: SharpEngineModuleId is required");
    if (module.Type is not ("Runtime" or "Editor"))
        Fail("SEMOD004", $"Module '{module.Id}' must explicitly declare Runtime or Editor");
    if (!module.AssemblyReferences.Contains("SharpEngine.Runtime",
            StringComparer.OrdinalIgnoreCase))
        Fail("SEMOD016", $"Module '{module.Id}' must directly reference SharpEngine.Runtime");
    if (module.Type == "Runtime" && module.AssemblyReferences.Contains(
            "SharpEngine.Editor", StringComparer.OrdinalIgnoreCase))
        Fail("SEMOD017", $"Runtime Module '{module.Id}' cannot reference SharpEngine.Editor editor API");
    if (module.Type == "Editor" && !module.AssemblyReferences.Contains(
            "SharpEngine.Editor", StringComparer.OrdinalIgnoreCase))
        Fail("SEMOD018", $"Editor Module '{module.Id}' must directly reference SharpEngine.Editor");
    if (!byId.TryAdd(module.Id, module))
        Fail("SEMOD005", $"Duplicate Module id '{module.Id}'");
}

foreach (var module in modules)
{
    foreach (var reference in module.ProjectReferences)
    {
        if (byPath.TryGetValue(reference, out var dependency))
        {
            module.Dependencies.Add(dependency);
            if (module.Type == "Runtime" && dependency.Type == "Editor")
                Fail("SEMOD006", $"Runtime Module '{module.Id}' cannot depend on Editor Module '{dependency.Id}'");
            continue;
        }

        var referencedModuleId = QueryProperty(reference, configuration,
            "SharpEngineModuleId");
        if (!string.IsNullOrWhiteSpace(referencedModuleId))
            Fail("SEMOD007", $"Module '{module.Id}' references Module '{referencedModuleId}' which is missing from the project manifest");
    }
}

var ordered = TopologicalOrder(modules);
var selected = ordered.Where(module => target == "Editor" || module.Type == "Runtime").ToArray();
foreach (var module in selected)
{
    Console.WriteLine($"[Validate] {module.Id} ({module.Type})");
    BuildModule(module.ProjectPath, configuration, platform, target,
        validationOutput, moduleIntermediate, managedApi, modulesApi, projectRoot);
}

var typeOwners = new Dictionary<string, string>(StringComparer.Ordinal);
var componentOwners = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var module in selected)
{
    if (!File.Exists(module.TargetPath))
        Fail("SEMOD008", $"Module output is missing: {module.TargetPath}");
    var metadata = InspectAssembly(module.TargetPath);
    foreach (var component in metadata.ComponentTypes)
    {
        if (module.Type != "Runtime") Fail("SECOMP002", $"Editor Module '{module.Id}' cannot declare scene component {component.Key}");
        if (component.Key.StartsWith("engine.", StringComparison.Ordinal)) Fail("SECOMP003", "Project component IDs cannot use the engine namespace");
        if (!componentOwners.TryAdd(component.Key, module.Id)) Fail("SECOMP004", $"Duplicate component ID {component.Key} in Modules {componentOwners[component.Key]} and {module.Id}");
    }
    foreach (var typeName in metadata.UserTypes)
    {
        if (typeOwners.TryGetValue(typeName, out var owner))
            Fail("SEMOD009", $"Duplicate project type '{typeName}' in Modules '{owner}' and '{module.Id}'");
        typeOwners.Add(typeName, module.Id);
    }
    if (metadata.ModuleEntries.Count != 1)
        Fail("SEMOD010", $"Module '{module.Id}' must define exactly one direct ISharpEngineModule implementation; found {metadata.ModuleEntries.Count}");
    module.EntryType = metadata.ModuleEntries[0];
}

var inputDirectories = new Dictionary<ModuleInfo, string>();
for (var index = 0; index < selected.Length; index++)
{
    var module = selected[index];
    var inputDirectory = Path.Combine(publishInputs,
        $"{index:D3}-{SafeSegment(module.AssemblyName)}");
    Directory.CreateDirectory(inputDirectory);
    Console.WriteLine($"[Stage] {module.Id} ({module.Type})");
    BuildModule(module.ProjectPath, configuration, platform, target,
        inputDirectory, moduleIntermediate, managedApi, modulesApi, projectRoot);
    inputDirectories.Add(module, inputDirectory);
}

var knownModules = modules.ToDictionary(module => module.AssemblyName + ".dll",
    StringComparer.OrdinalIgnoreCase);
var selectedModules = selected.ToDictionary(module => module.AssemblyName + ".dll",
    StringComparer.OrdinalIgnoreCase);
var engineAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "SharpEngine.Editor.dll",
    "SharpEngine.Runtime.dll",
};
var published = new Dictionary<string, PublishedFile>(StringComparer.OrdinalIgnoreCase);
foreach (var (owner, directory) in inputDirectories)
{
    foreach (var path in Directory.EnumerateFiles(directory, "*.dll",
                 SearchOption.TopDirectoryOnly))
    {
        var name = Path.GetFileName(path);
        if (engineAssemblies.Contains(name)) continue;
        if (knownModules.TryGetValue(name, out var module) &&
            !selectedModules.ContainsKey(name))
            Fail("SEMOD020", $"Selected Module '{owner.Id}' unexpectedly publishes excluded Module '{module.Id}'");
        MergePublishedFile(published, path,
            selectedModules.ContainsKey(name) ? "module" : "dependency");
    }
}

foreach (var file in published.Values.OrderBy(file => file.Name,
             StringComparer.OrdinalIgnoreCase))
    File.Copy(file.SourcePath, Path.Combine(packageDirectory, file.Name), true);
if (configuration != "Shipping")
{
    foreach (var module in selected)
    {
        var pdbName = module.AssemblyName + ".pdb";
        var pdbPath = Path.Combine(inputDirectories[module], pdbName);
        if (File.Exists(pdbPath))
            File.Copy(pdbPath, Path.Combine(packageDirectory, pdbName), true);
    }
}

var runtimeManifest = new
{
    schemaVersion = 2,
    projectId,
    projectManifestHash = FileHash(projectManifest),
    engineId = resolvedEngineId,
    engineVersion = resolvedEngineVersion,
    target,
    configuration,
    platform,
    files = published.Values.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
        .Select(file => new
        {
            name = file.Name,
            kind = file.Kind,
            contentHash = file.ContentHash,
        }).ToArray(),
    plugins = plugins.Select(plugin => new
    {
        id = plugin.Id,
        version = plugin.Version,
        source = plugin.Source,
        manifestHash = plugin.ManifestHash,
        assetManifest = plugin.AssetManifest,
        assetManifestHash = plugin.AssetManifestHash,
    }).ToArray(),
    modules = selected.Select(module =>
    {
        var assembly = module.AssemblyName + ".dll";
        if (!published.TryGetValue(assembly, out var file))
            Fail("SEMOD008", $"Published Module output is missing: {assembly}");
        return new
        {
            id = module.Id,
            type = module.Type,
            assembly,
            contentHash = file.ContentHash,
            entryType = module.EntryType,
            dependencies = module.Dependencies
                .Where(selected.Contains).Select(value => value.Id).ToArray(),
        };
    }).ToArray(),
};
var runtimeManifestPath = Path.Combine(packageDirectory, "modules.runtime.json");
File.WriteAllText(runtimeManifestPath, JsonSerializer.Serialize(runtimeManifest,
    new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
    new UTF8Encoding(false));
PublishDirectory(packageDirectory, outputDirectory, projectBuildDirectory);
Console.WriteLine($"[Build] PASS: {selected.Length} Modules -> " +
                  Path.Combine(outputDirectory, "modules.runtime.json"));
return 0;

string Option(string name, string fallback)
{
    for (var index = offset + 1; index < args.Length - 1; index++)
        if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[index + 1];
    return fallback;
}

static string RequiredString(JsonElement value, string property)
{
    if (!value.TryGetProperty(property, out var element) ||
        element.ValueKind != JsonValueKind.String ||
        string.IsNullOrWhiteSpace(element.GetString()))
        Fail("SEBUILD007", $"Required string property is missing: {property}");
    return element.GetString()!;
}

static IReadOnlyList<PluginInfo> ReadPlugins(JsonElement project, string projectRoot,
    string engineRoot)
{
    if (!project.TryGetProperty("plugins", out var pluginArray)) return [];
    if (pluginArray.ValueKind != JsonValueKind.Array)
        Fail("SEPLUGIN001", "Project property 'plugins' must be an array");
    var result = new List<PluginInfo>();
    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in pluginArray.EnumerateArray())
    {
        var source = item.GetString() ?? string.Empty;
        var manifestPath = ResolvePluginManifest(source, projectRoot, engineRoot);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
            Fail("SEPLUGIN002", $"Plugin manifest schemaVersion must be 1: {source}");
        var id = RequiredString(root, "id");
        var version = RequiredString(root, "version");
        if (id.Length == 0 || !char.IsLetterOrDigit(id[0]) ||
            id.Any(character => !char.IsLetterOrDigit(character) &&
                character is not ('.' or '_' or '-')))
            Fail("SEPLUGIN015", $"Plugin '{id}' has an invalid id");
        if (!ids.Add(id)) Fail("SEPLUGIN003", $"Duplicate Plugin id '{id}'");
        if (!root.TryGetProperty("modules", out var modules) ||
            modules.ValueKind != JsonValueKind.Array)
            Fail("SEPLUGIN004", $"Plugin '{id}' must declare a modules array");
        var pluginRoot = Path.GetDirectoryName(manifestPath)!;
        string? assetManifest = null;
        string? assetManifestHash = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            if (assets.ValueKind != JsonValueKind.Object ||
                !assets.TryGetProperty("manifest", out var manifest) ||
                manifest.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(manifest.GetString()))
                Fail("SEPLUGIN012", $"Plugin '{id}' assets.manifest must be a non-empty path");
            assetManifest = manifest.GetString()!;
            var normalizedAssetManifest = assetManifest.Replace('\\', '/');
            if (Path.IsPathRooted(assetManifest) ||
                !normalizedAssetManifest.StartsWith("Content/", StringComparison.OrdinalIgnoreCase) ||
                normalizedAssetManifest.Split('/').Any(segment =>
                    segment.Length == 0 || segment is "." or ".."))
                Fail("SEPLUGIN013", $"Plugin '{id}' assets.manifest must remain under Content");
            var assetManifestPath = Path.GetFullPath(Path.Combine(pluginRoot,
                assetManifest.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(assetManifestPath))
                Fail("SEPLUGIN014", $"Plugin asset manifest was not found: {assetManifestPath}");
            assetManifestHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(assetManifestPath))).ToLowerInvariant();
        }
        var modulePaths = modules.EnumerateArray().Select(value =>
        {
            var relative = value.GetString() ?? string.Empty;
            if (Path.IsPathRooted(relative) || string.IsNullOrWhiteSpace(relative))
                Fail("SEPLUGIN005", $"Plugin '{id}' has an invalid Module path: {relative}");
            var rootPrefix = Path.GetFullPath(pluginRoot).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(Path.Combine(pluginRoot, relative));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                Fail("SEPLUGIN006", $"Plugin '{id}' Module path escapes its root: {relative}");
            return fullPath;
        }).ToArray();
        result.Add(new PluginInfo(id, version, source,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant(),
            modulePaths, assetManifest, assetManifestHash));
    }
    return result;
}

static string ResolvePluginManifest(string source, string projectRoot, string engineRoot)
{
    var separator = source.IndexOf("://", StringComparison.Ordinal);
    if (separator <= 0) Fail("SEPLUGIN007", $"Invalid Plugin URI: {source}");
    var scheme = source[..separator];
    var relative = source[(separator + 3)..].Replace('/', Path.DirectorySeparatorChar);
    if (!relative.StartsWith("Plugins" + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        Fail("SEPLUGIN008", $"Plugin URI must remain under Plugins: {source}");
    var root = scheme.Equals("project", StringComparison.OrdinalIgnoreCase)
        ? projectRoot
        : scheme.Equals("engine", StringComparison.OrdinalIgnoreCase)
            ? engineRoot
            : null;
    if (root is null) Fail("SEPLUGIN009", $"Unknown Plugin URI scheme: {scheme}");
    var pluginsRoot = Path.GetFullPath(Path.Combine(root, "Plugins"));
    var rootPrefix = pluginsRoot.TrimEnd(Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var fullPath = Path.GetFullPath(Path.Combine(pluginsRoot,
        relative[("Plugins" + Path.DirectorySeparatorChar).Length..]));
    if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
        !fullPath.EndsWith(".sharpplugin", StringComparison.OrdinalIgnoreCase))
        Fail("SEPLUGIN010", $"Plugin manifest path is invalid: {source}");
    if (!File.Exists(fullPath)) Fail("SEPLUGIN011", $"Plugin manifest not found: {fullPath}");
    return fullPath;
}

static string ResolveEngineRoot(JsonElement engine, string explicitEngineRoot)
{
    if (!string.IsNullOrWhiteSpace(explicitEngineRoot))
        return Path.GetFullPath(explicitEngineRoot);
    var environmentPath = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT");
    if (!string.IsNullOrWhiteSpace(environmentPath))
        return Path.GetFullPath(environmentPath);
    var association = RequiredString(engine, "association");
    if (association.Length > 128 ||
        (!Guid.TryParse(association, out _) &&
         !Regex.IsMatch(association, "^[A-Za-z0-9][A-Za-z0-9._-]*$",
             RegexOptions.CultureInvariant)))
        Fail("SEBUILD015", $"Engine association '{association}' is invalid");
    using var key = Registry.CurrentUser.OpenSubKey(
        @"Software\SharpEngine\Builds", writable: false);
    if (key?.GetValue(association) is not string registeredRoot ||
        string.IsNullOrWhiteSpace(registeredRoot))
        Fail("SEBUILD014", $"Engine association '{association}' is not registered");
    return Path.GetFullPath(registeredRoot);
}

static string ResolveLayoutPath(string root, string template, string platform,
    string configuration, string target)
{
    var relative = template.Replace("{platform}", platform, StringComparison.Ordinal)
        .Replace("{configuration}", configuration, StringComparison.Ordinal)
        .Replace("{target}", target, StringComparison.Ordinal);
    if (relative.Contains('{') || relative.Contains('}') || Path.IsPathRooted(relative))
        Fail("SEBUILD013", $"Invalid layout path template '{template}'");
    var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var result = Path.GetFullPath(Path.Combine(fullRoot, relative));
    if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        Fail("SEBUILD013", $"Layout path escapes its root: '{template}'");
    return result;
}

static ModuleInfo EvaluateModule(string projectPath, string configuration,
    string platform, string target, string outputRoot, string intermediateRoot,
    string managedApi, string modulesApi)
{
    if (!File.Exists(projectPath)) Fail("SEMOD011", $"Module project not found: {projectPath}");
    var arguments = new List<string>
    {
        "msbuild", projectPath,
        "-getProperty:SharpEngineModuleId,SharpEngineModuleType,AssemblyName,TargetPath",
        "-getItem:ProjectReference,Reference", "-nologo",
    };
    arguments.AddRange(BuildProperties(configuration, platform, target,
        outputRoot, intermediateRoot, managedApi, modulesApi));
    var output = Run("dotnet", arguments,
        Path.GetDirectoryName(projectPath)!, capture: true);
    using var document = JsonDocument.Parse(output);
    var properties = document.RootElement.GetProperty("Properties");
    var references = document.RootElement.GetProperty("Items").GetProperty("ProjectReference")
        .EnumerateArray().Select(item => Path.GetFullPath(
            item.GetProperty("FullPath").GetString()!)).ToArray();
    var assemblyReferences = document.RootElement.GetProperty("Items").GetProperty("Reference")
        .EnumerateArray().Select(item => item.GetProperty("Identity").GetString() ?? string.Empty)
        .Where(value => value.Length > 0).ToArray();
    return new ModuleInfo
    {
        ProjectPath = Path.GetFullPath(projectPath),
        Id = properties.GetProperty("SharpEngineModuleId").GetString() ?? string.Empty,
        Type = properties.GetProperty("SharpEngineModuleType").GetString() ?? string.Empty,
        AssemblyName = properties.GetProperty("AssemblyName").GetString() ?? string.Empty,
        TargetPath = Path.GetFullPath(properties.GetProperty("TargetPath").GetString()!),
        ProjectReferences = references,
        AssemblyReferences = assemblyReferences,
    };
}

static void BuildModule(string projectPath, string configuration, string platform,
    string target, string outputRoot, string intermediateRoot, string managedApi,
    string modulesApi, string workingDirectory)
{
    var arguments = new List<string> { "build", projectPath, "--nologo" };
    arguments.AddRange(BuildProperties(configuration, platform, target,
        outputRoot, intermediateRoot, managedApi, modulesApi));
    Run("dotnet", arguments, workingDirectory, capture: false);
}

static IReadOnlyList<string> BuildProperties(string configuration, string platform,
    string target, string outputRoot, string intermediateRoot, string managedApi,
    string modulesApi) =>
[
    $"-p:Configuration={configuration}",
    $"-p:SharpEnginePlatform={platform}",
    $"-p:SharpEngineTarget={target}",
    $"-p:SharpEngineOutputRoot={EnsureTrailingSeparator(outputRoot)}",
    $"-p:SharpEngineIntermediateRoot={EnsureTrailingSeparator(intermediateRoot)}",
    $"-p:SharpEngineEditorApi={managedApi}",
    $"-p:SharpEngineRuntimeApi={modulesApi}",
    $"-p:SharpEngineComponentGenerator={Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(modulesApi))!, "Tools", "SharpEngine.ComponentGenerator.dll")}",
    $"-p:CustomAfterMicrosoftCommonTargets={Path.GetFullPath(Path.Combine(Path.GetDirectoryName(modulesApi)!, "..", "..", "..", "..", "Build", "SharpEngine.Components.targets"))}",
    "-p:SharpEngineUnifiedBuild=true",
];

static string EnsureTrailingSeparator(string path) =>
    Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;

static string QueryProperty(string projectPath, string configuration, string property)
{
    var output = Run("dotnet", ["msbuild", projectPath, $"-getProperty:{property}",
        $"-p:Configuration={configuration}", "-nologo"],
        Path.GetDirectoryName(projectPath)!, capture: true);
    if (string.IsNullOrWhiteSpace(output)) return string.Empty;
    using var document = JsonDocument.Parse(output);
    return document.RootElement.GetProperty("Properties").GetProperty(property)
        .GetString() ?? string.Empty;
}

static IReadOnlyList<ModuleInfo> TopologicalOrder(IReadOnlyList<ModuleInfo> modules)
{
    var result = new List<ModuleInfo>();
    var state = new Dictionary<ModuleInfo, byte>();
    void Visit(ModuleInfo module)
    {
        if (state.TryGetValue(module, out var value))
        {
            if (value == 1) Fail("SEMOD012", $"Module dependency cycle includes '{module.Id}'");
            return;
        }
        state[module] = 1;
        foreach (var dependency in module.Dependencies) Visit(dependency);
        state[module] = 2;
        result.Add(module);
    }
    foreach (var module in modules) Visit(module);
    return result;
}

static FileStream AcquireBuildLock(string path)
{
    var timer = Stopwatch.StartNew();
    while (true)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(60)) { Thread.Sleep(100); }
    }
}

static void PrepareDirectory(string path)
{
    if (Directory.Exists(path)) Directory.Delete(path, true);
    Directory.CreateDirectory(path);
}

static string SafeSegment(string value)
{
    var invalid = Path.GetInvalidFileNameChars();
    return new string(value.Select(character => invalid.Contains(character) ? '_' : character)
        .ToArray());
}

static void MergePublishedFile(Dictionary<string, PublishedFile> published,
    string path, string kind)
{
    var name = Path.GetFileName(path);
    var hash = FileHash(path);
    if (published.TryGetValue(name, out var existing))
    {
        if (!string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
            Fail("SEMOD021", $"Published dependency collision for '{name}': " +
                $"{existing.SourcePath} differs from {path}");
        if (kind == "module") existing.Kind = kind;
        return;
    }
    published.Add(name, new PublishedFile(name, path, hash, kind));
}

static void PublishDirectory(string packageDirectory, string outputDirectory,
    string workingDirectory)
{
    var backup = Path.Combine(workingDirectory, "Previous");
    if (Directory.Exists(backup)) Directory.Delete(backup, true);
    Directory.CreateDirectory(Path.GetDirectoryName(outputDirectory)!);
    var movedPrevious = false;
    try
    {
        if (Directory.Exists(outputDirectory))
        {
            MovePublishedDirectory(outputDirectory, backup);
            movedPrevious = true;
        }
        MovePublishedDirectory(packageDirectory, outputDirectory);
    }
    catch
    {
        if (movedPrevious && !Directory.Exists(outputDirectory) && Directory.Exists(backup))
            MovePublishedDirectory(backup, outputDirectory);
        throw;
    }
    if (Directory.Exists(backup)) Directory.Delete(backup, true);
}

static void MovePublishedDirectory(string source, string destination)
{
    // Scanners and recently closed compiler handles can temporarily prevent
    // directory replacement on Windows. Keep the rollback boundary intact.
    for (var attempt = 0; ; attempt++)
    {
        try { Directory.Move(source, destination); return; }
        catch (IOException error) when (attempt < 10 && (error.HResult & 0xffff) is 5 or 32 or 33)
        { Thread.Sleep(100); }
        catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(100); }
    }
}

static AssemblyMetadata InspectAssembly(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var reader = pe.GetMetadataReader();
    var types = new List<string>();
    var entries = new List<string>();
    var components = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var handle in reader.TypeDefinitions)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        if (name == "<Module>" || name.StartsWith('<')) continue;
        var fullName = FullTypeDefinitionName(reader, handle);
        types.Add(fullName);
        foreach (var attributeHandle in definition.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (InterfaceName(reader, constructor.Parent) != "SharpEngine.Components.ComponentTypeIdAttribute") continue;
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) Fail("SECOMP005", "Invalid component type metadata");
            var componentId = blob.ReadSerializedString() ?? throw new InvalidDataException("Empty component ID");
            if (!components.TryAdd(componentId, fullName)) Fail("SECOMP004", $"Duplicate component ID {componentId}");
        }
        var directlyImplementsModule = definition.GetInterfaceImplementations()
            .Select(reader.GetInterfaceImplementation)
            .Any(value => InterfaceName(reader, value.Interface) ==
                "SharpEngine.Modules.ISharpEngineModule");
        if (!directlyImplementsModule) continue;
        var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
        var isPublic = visibility is TypeAttributes.Public or TypeAttributes.NestedPublic;
        var hasDefaultConstructor = definition.GetMethods().Select(reader.GetMethodDefinition)
            .Any(method => reader.GetString(method.Name) == ".ctor" &&
                (method.Attributes & MethodAttributes.Public) != 0 &&
                !method.GetParameters().Select(reader.GetParameter)
                    .Any(parameter => parameter.SequenceNumber > 0));
        if (!isPublic || (definition.Attributes & TypeAttributes.Abstract) != 0 ||
            definition.GetGenericParameters().Count > 0 || !hasDefaultConstructor)
            Fail("SEMOD013", $"Module entry '{fullName}' must be public, concrete, non-generic, and have a public parameterless constructor");
        entries.Add(fullName);
    }
    return new AssemblyMetadata(types, entries, components);
}

static string InterfaceName(MetadataReader reader, EntityHandle handle) => handle.Kind switch
{
    HandleKind.TypeReference => FullTypeReferenceName(reader, (TypeReferenceHandle)handle),
    HandleKind.TypeDefinition => FullTypeDefinitionName(reader, (TypeDefinitionHandle)handle),
    _ => string.Empty,
};

static string FileHash(string path) =>
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static string FullTypeReferenceName(MetadataReader reader, TypeReferenceHandle handle)
{
    var type = reader.GetTypeReference(handle);
    var name = reader.GetString(type.Name);
    var ns = reader.GetString(type.Namespace);
    return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
}

static string FullTypeDefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
{
    var type = reader.GetTypeDefinition(handle);
    var name = reader.GetString(type.Name);
    var declaring = type.GetDeclaringType();
    if (!declaring.IsNil) return FullTypeDefinitionName(reader, declaring) + "+" + name;
    var ns = reader.GetString(type.Namespace);
    return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
}

static string Run(string fileName, IReadOnlyList<string> arguments,
    string workingDirectory, bool capture)
{
    var start = new ProcessStartInfo(fileName)
    {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = capture,
        RedirectStandardError = true,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException($"Could not start {fileName}");
    var standardOutput = capture ? process.StandardOutput.ReadToEndAsync() : null;
    var standardError = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    var error = standardError.GetAwaiter().GetResult();
    if (process.ExitCode != 0)
        Fail("SEBUILD008", $"{fileName} failed with exit code {process.ExitCode}: {error}");
    return standardOutput?.GetAwaiter().GetResult() ?? string.Empty;
}

[DoesNotReturn]
static void Fail(string code, string message)
{
    Console.Error.WriteLine($"{code}: {message}");
    Environment.Exit(1);
    throw new UnreachableException();
}

sealed class ModuleInfo
{
    public required string ProjectPath { get; init; }
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string AssemblyName { get; init; }
    public required string TargetPath { get; init; }
    public required string[] ProjectReferences { get; init; }
    public required string[] AssemblyReferences { get; init; }
    public List<ModuleInfo> Dependencies { get; } = new();
    public string EntryType { get; set; } = string.Empty;
}

sealed record ModuleSource(string ProjectPath);

sealed record PluginInfo(string Id, string Version, string Source,
    string ManifestHash, IReadOnlyList<string> Modules,
    string? AssetManifest, string? AssetManifestHash);

sealed class PublishedFile(string name, string sourcePath, string contentHash,
    string kind)
{
    public string Name { get; } = name;
    public string SourcePath { get; } = sourcePath;
    public string ContentHash { get; } = contentHash;
    public string Kind { get; set; } = kind;
}

sealed record AssemblyMetadata(IReadOnlyList<string> UserTypes,
    IReadOnlyList<string> ModuleEntries, IReadOnlyDictionary<string, string> ComponentTypes);
