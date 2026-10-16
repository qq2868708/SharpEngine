using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

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
if (projectJson.GetProperty("schemaVersion").GetInt32() != 2)
    Fail("SEBUILD005", "Project manifest schemaVersion must be 2");
var projectRoot = Path.GetDirectoryName(projectManifest)!;
var projectId = RequiredString(projectJson, "projectId");
var engine = projectJson.GetProperty("engine");
var engineRoot = ResolveEngineRoot(projectRoot, engine);
var engineManifestPath = Path.Combine(engineRoot,
    engine.TryGetProperty("manifest", out var engineManifestValue)
        ? engineManifestValue.GetString() ?? "Engine/engine.json"
        : "Engine/engine.json");
if (!File.Exists(engineManifestPath))
    Fail("SEBUILD009", $"Engine manifest not found: {engineManifestPath}");
string resolvedEngineId;
string resolvedEngineVersion;
using (var engineDocument = JsonDocument.Parse(File.ReadAllText(engineManifestPath)))
{
    var requestedId = RequiredString(engine, "id");
    var requestedVersion = RequiredString(engine, "version");
    resolvedEngineId = RequiredString(engineDocument.RootElement, "engineId");
    resolvedEngineVersion = RequiredString(engineDocument.RootElement, "version");
    if (!string.Equals(requestedId, resolvedEngineId, StringComparison.Ordinal))
        Fail("SEBUILD010", $"Engine id mismatch: requested '{requestedId}', resolved '{resolvedEngineId}'");
    if (!string.Equals(requestedVersion, resolvedEngineVersion, StringComparison.Ordinal))
        Fail("SEBUILD011", $"Engine version mismatch: requested '{requestedVersion}', resolved '{resolvedEngineVersion}'");
}
var managedApi = Path.Combine(engineRoot, "x64", configuration, "Managed",
    "SharpEngine.Managed.dll");
var modulesApi = Path.Combine(engineRoot, "x64", configuration, "Managed",
    "SharpEngine.Modules.dll");
if (!File.Exists(managedApi) || !File.Exists(modulesApi))
    Fail("SEBUILD006", $"SharpEngine managed APIs are not built: {managedApi}, {modulesApi}");

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

var modules = projectPaths.Select(path => EvaluateModule(path, configuration)).ToArray();
var outputDirectory = Path.GetFullPath(Path.Combine(projectRoot, "Binaries", "Managed"));
foreach (var module in modules)
    if (!string.Equals(Path.GetDirectoryName(module.TargetPath), outputDirectory,
            StringComparison.OrdinalIgnoreCase))
        Fail("SEMOD015", $"Module '{module.Id}' output must be under {outputDirectory}: {module.TargetPath}");
var byPath = modules.ToDictionary(module => module.ProjectPath,
    StringComparer.OrdinalIgnoreCase);
var byId = new Dictionary<string, ModuleInfo>(StringComparer.OrdinalIgnoreCase);
foreach (var module in modules)
{
    if (string.IsNullOrWhiteSpace(module.Id))
        Fail("SEMOD003", $"Module type is not declared in {module.ProjectPath}: SharpEngineModuleId is required");
    if (module.Type is not ("Runtime" or "Editor"))
        Fail("SEMOD004", $"Module '{module.Id}' must explicitly declare Runtime or Editor");
    if (!module.AssemblyReferences.Contains("SharpEngine.Modules",
            StringComparer.OrdinalIgnoreCase))
        Fail("SEMOD016", $"Module '{module.Id}' must directly reference SharpEngine.Modules");
    if (module.Type == "Runtime" && module.AssemblyReferences.Contains(
            "SharpEngine.Managed", StringComparer.OrdinalIgnoreCase))
        Fail("SEMOD017", $"Runtime Module '{module.Id}' cannot reference SharpEngine.Managed editor API");
    if (module.Type == "Editor" && !module.AssemblyReferences.Contains(
            "SharpEngine.Managed", StringComparer.OrdinalIgnoreCase))
        Fail("SEMOD018", $"Editor Module '{module.Id}' must directly reference SharpEngine.Managed");
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
foreach (var module in ordered)
{
    Console.WriteLine($"[Build] {module.Id} ({module.Type})");
    Run("dotnet", ["build", module.ProjectPath, "--nologo", "-c", configuration,
        $"-p:SharpEngineManagedApi={managedApi}",
        $"-p:SharpEngineModulesApi={modulesApi}"], projectRoot, capture: false);
}

var typeOwners = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var module in ordered)
{
    if (!File.Exists(module.TargetPath))
        Fail("SEMOD008", $"Module output is missing: {module.TargetPath}");
    var metadata = InspectAssembly(module.TargetPath);
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

Directory.CreateDirectory(outputDirectory);
var runtimeManifest = new
{
    schemaVersion = 1,
    projectId,
    projectManifestHash = FileHash(projectManifest),
    engineId = resolvedEngineId,
    engineVersion = resolvedEngineVersion,
    target,
    configuration,
    platform,
    modules = selected.Select(module => new
    {
        id = module.Id,
        type = module.Type,
        assembly = Path.GetFileName(module.TargetPath),
        contentHash = FileHash(module.TargetPath),
        entryType = module.EntryType,
        dependencies = module.Dependencies
            .Where(selected.Contains).Select(value => value.Id).ToArray(),
    }).ToArray(),
};
var runtimeManifestPath = Path.Combine(outputDirectory, "modules.runtime.json");
var temporaryPath = runtimeManifestPath + ".tmp";
File.WriteAllText(temporaryPath, JsonSerializer.Serialize(runtimeManifest,
    new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
File.Move(temporaryPath, runtimeManifestPath, true);
Console.WriteLine($"[Build] PASS: {selected.Length} Modules -> {runtimeManifestPath}");
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
        string.IsNullOrWhiteSpace(element.GetString()))
        Fail("SEBUILD007", $"Required project property is missing: {property}");
    return element.GetString()!;
}

static string ResolveEngineRoot(string projectRoot, JsonElement engine)
{
    var environmentName = engine.TryGetProperty("pathEnvironment", out var environment)
        ? environment.GetString() : null;
    var environmentPath = string.IsNullOrWhiteSpace(environmentName)
        ? null : Environment.GetEnvironmentVariable(environmentName);
    var path = !string.IsNullOrWhiteSpace(environmentPath)
        ? environmentPath! : RequiredString(engine, "path");
    return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
}

static ModuleInfo EvaluateModule(string projectPath, string configuration)
{
    if (!File.Exists(projectPath)) Fail("SEMOD011", $"Module project not found: {projectPath}");
    var output = Run("dotnet", ["msbuild", projectPath,
        "-getProperty:SharpEngineModuleId,SharpEngineModuleType,AssemblyName,TargetPath",
        "-getItem:ProjectReference,Reference", $"-p:Configuration={configuration}", "-nologo"],
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

static string QueryProperty(string projectPath, string configuration, string property)
{
    var output = Run("dotnet", ["msbuild", projectPath, $"-getProperty:{property}",
        $"-p:Configuration={configuration}", "-nologo"],
        Path.GetDirectoryName(projectPath)!, capture: true);
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

static AssemblyMetadata InspectAssembly(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var reader = pe.GetMetadataReader();
    var types = new List<string>();
    var entries = new List<string>();
    foreach (var handle in reader.TypeDefinitions)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        if (name == "<Module>" || name.StartsWith('<')) continue;
        var fullName = FullTypeDefinitionName(reader, handle);
        types.Add(fullName);
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
    return new AssemblyMetadata(types, entries);
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

sealed record AssemblyMetadata(IReadOnlyList<string> UserTypes,
    IReadOnlyList<string> ModuleEntries);
