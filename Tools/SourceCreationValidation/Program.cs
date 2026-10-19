using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Editor;
using SharpEngine.Host;

var repository = Path.GetFullPath(args[0]);
var engine = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
if (engine.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
    engine.StartsWith(Path.Combine(repository, "Tools") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new Exception("Run with -p:SharpEngineRoot=<isolated engine directory>/ to isolate reload outputs");
var root = Path.Combine(engine, "Projects", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
File.Copy(Path.Combine(repository, "Templates", "BasicProject", "Directory.Build.props"), Path.Combine(root, "Directory.Build.props"));
Console.WriteLine("Fixture: " + root);
foreach (var folder in new[] { "Config", "Content" }) CopyTree(Path.Combine(repository, folder), Path.Combine(engine, folder));
File.Copy(Path.Combine(repository, "engine.json"), Path.Combine(engine, "engine.json"), true);
Directory.CreateDirectory(Path.Combine(engine, "Build"));
File.Copy(Path.Combine(repository, "Build", "SharpEngine.Components.targets"), Path.Combine(engine, "Build", "SharpEngine.Components.targets"), true);
CopyTree(Path.Combine(repository, "Tools", "ProjectBuild"), Path.Combine(engine, "Tools", "ProjectBuild"));
Directory.CreateDirectory(Path.Combine(root, "Content", "Assets"));
File.WriteAllText(Path.Combine(root, "Content", "Assets", "assets.json"), "{\"schemaVersion\":1,\"assets\":[]}");
var manifest = Path.Combine(root, "Sample.sharpproject");
File.WriteAllText(manifest, """
{"schemaVersion":3,"projectId":"reload-validation","displayName":"ReloadValidation","engine":{"id":"SharpEngine","version":"0.2.0-dev","association":"validation","manifest":"engine.json"},"modules":["Source/Game/Game.csproj","Source/Game.Editor/Game.Editor.csproj"],"assets":{"manifest":"Content/Assets/assets.json"}}
""");
foreach (var name in new[] { "Game", "Game.Editor" })
{
    var path = Path.Combine(root, "Source", name); Directory.CreateDirectory(path);
    var editor = name.EndsWith("Editor");
    File.WriteAllText(Path.Combine(path, name + ".csproj"), $$"""
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
<SharpEngineModuleId>{{name}}</SharpEngineModuleId><SharpEngineModuleType>{{(editor ? "Editor" : "Runtime")}}</SharpEngineModuleType>
<RootNamespace>Sample.{{name}}</RootNamespace><AssemblyName>{{name}}</AssemblyName>
<OutputPath>$(SharpEngineOutputRoot)</OutputPath><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
</PropertyGroup><ItemGroup><Reference Include="SharpEngine.Runtime"><HintPath>$(SharpEngineRuntimeApi)</HintPath><Private>false</Private></Reference>
{{(editor ? "<Reference Include=\"SharpEngine.Editor\"><HintPath>$(SharpEngineEditorApi)</HintPath><Private>false</Private></Reference>" : "")}}
</ItemGroup></Project>
""");
    File.WriteAllText(Path.Combine(path, "Module.cs"), $$"""
using SharpEngine.Modules;
using SharpEngine.Settings;

namespace Sample.{{name}};

public class Module : ISharpEngineModule
{
    public void Startup(ModuleContext Context)
    {
    }

    public void Shutdown()
    {
    }
}
""");
    if (!editor) File.AppendAllText(Path.Combine(path, "Module.cs"), """

[DevelopmentSettings("sample.reload", SettingsScope.Project)]
public sealed class ReloadSettings : DevelopmentSettings
{
    [Setting("speed")] public int Speed { get; set; } = 3;
}
""");
}
Environment.SetEnvironmentVariable("SHARPENGINE_ROOT", engine);
Environment.SetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST", manifest);
var assertions = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; Console.WriteLine("PASS " + message); }
var sources = new CSharpSourceCreation(manifest, engine, "Debug", "Win64");
var component = sources.Create(new("Source/Game/Game.csproj", "Actors/AI", "Brain", "component"));
Check(component.Success && component.Namespace == "Sample.Game.Actors.AI" && Guid.TryParse(component.ComponentTypeId, out _), "component uses evaluated namespace and generated UUID");
Check(!File.ReadAllText(component.Path!).Contains("OnInitialize"), "minimal component has no lifecycle stubs");
Check(!sources.Create(new("Source/Game/Game.csproj", "Actors/AI", "Brain", "component")).Success, "existing source is never overwritten");
Check(!sources.Create(new("Source/Game.Editor/Game.Editor.csproj", "", "Bad", "component")).Success, "Editor Modules reject scene components");
Check(sources.Create(new("Source/Game.Editor/Game.Editor.csproj", "Tools", "Helper", "class")).Success, "Editor plain class is supported");
var editorProject = Path.Combine(root, "Source", "Game.Editor", "Game.Editor.csproj");
File.WriteAllText(editorProject, File.ReadAllText(editorProject).Replace("</Project>", "<ItemGroup><Compile Remove=\"Excluded/**/*.cs\" /></ItemGroup></Project>"));
var excluded = sources.Create(new("Source/Game.Editor/Game.Editor.csproj", "Excluded", "Ignored", "class"));
Check(!excluded.Success && !File.Exists(Path.Combine(root, "Source", "Game.Editor", "Excluded", "Ignored.cs")),
    "excluded Compile item is rejected and its new file rolled back");
foreach (var folder in new[] { "../escape", "bad-name", "class", "/Absolute" })
    Check(!sources.Create(new("Source/Game/Game.csproj", folder, "Other", "class")).Success, "invalid directory rejected: " + folder);
Check(!sources.Create(new("Source/Game/Game.csproj", "", "class", "class")).Success, "C# reserved type name rejected");
var runtime = new EditorRuntime(0, 1440, 900, 0);
runtime.Initialize();
runtime.CompleteComponentRegistration();
var input = InputRuntimeService.Load(runtime.Assets!);
using var service = new ProjectCodeService(runtime, input, null);
WaitFor("Idle");
Check(runtime.Components!.Find(component.ComponentTypeId!) is not null, "automatic build loads new component with shared base type");
if (args.Contains("--settings-only"))
{
    JsonNode Page() => JsonNode.Parse(runtime.DescribeSettingsJson())!["pages"]!.AsArray().Single(page => page!["id"]!.GetValue<string>() == "sample.reload")!;
    runtime.InvokeUiAction("editor.settings-change:project:sample.reload:speed:7");
    Check(Page()["values"]!["speed"]!.GetValue<int>() == 7 && Page()["dirty"]!.GetValue<bool>(), "project setting is dirty before reload");
    File.AppendAllText(component.Path!, "\n// reload with dirty project settings\n");
    WaitFor("Idle");
    Check(Page()["values"]!["speed"]!.GetValue<int>() == 7 && Page()["dirty"]!.GetValue<bool>(), "new settings CLR type retains unsaved values and dirty state");
    runtime.InvokeUiAction("editor.settings-undo:project:sample.reload");
    Check(Page()["values"]!["speed"]!.GetValue<int>() == 3 && !Page()["dirty"]!.GetValue<bool>(), "settings Undo survives replacement");
    runtime.InvokeUiAction("editor.settings-redo:project:sample.reload");
    Check(Page()["values"]!["speed"]!.GetValue<int>() == 7, "settings Redo survives replacement");
    service.Dispose(); runtime.Shutdown();
    Console.WriteLine($"PASS {assertions} source/settings reload assertions");
    return;
}
var added = runtime.EditComponent(new JsonObject { ["context"] = "authoring", ["expectedRevision"] = runtime.DocumentRevision,
    ["operation"] = "add", ["entityId"] = runtime.World!.Entities[0].Id, ["typeId"] = component.ComponentTypeId });
Check(added["success"]!.GetValue<bool>(), "newly loaded component can be added to an unsaved World");
var originalSource = File.ReadAllText(component.Path!);
var originalDocument = runtime.World.Serialize();
File.WriteAllText(component.Path!, originalSource.Replace("{\n}", "{\n    public int Health;\n}"));
WaitFor("ReloadFailed");
Check(runtime.ProjectCodeAvailable && runtime.World.Serialize() == originalDocument,
    "incompatible state schema rejects replacement and preserves the prior generation and document");
File.WriteAllText(component.Path!, originalSource);
WaitFor("Idle");
var moduleSource = Path.Combine(root, "Source", "Game.Editor", "Module.cs");
File.WriteAllText(moduleSource, EditorModule(false));
WaitFor("Idle");
Check(runtime.Commands.Commands.Any(command => command.Id == "validation.reload"), "Editor Module registered command");
runtime.InvokeUiAction("editor.component-create-entity");
var before = runtime.World!.Serialize();
var dirty = runtime.IsDirty;
File.WriteAllText(moduleSource, EditorModule(true));
WaitFor("ReloadFailed");
Check(!runtime.ProjectCodeAvailable && !runtime.Commands.Commands.Any(command => command.Id == "validation.reload"), "failed Startup cleans partial registration and disables Play");
Check(runtime.World.Serialize() == before && runtime.IsDirty == dirty, "failed reload preserves unsaved document");
File.WriteAllText(moduleSource, EditorModule(false));
WaitFor("Idle");
Check(runtime.ProjectCodeAvailable && runtime.Commands.Commands.Count(command => command.Id == "validation.reload") == 1, "editing after failed reload recovers without restart or duplicate command");
Check(runtime.World.Serialize() == before && runtime.IsDirty == dirty, "successful reload preserves unsaved document");
var entityCount = runtime.World.Entities.Count;
runtime.InvokeUiAction("editor.undo");
Check(runtime.World.Entities.Count == entityCount - 1, "Undo history survives generation replacement");
runtime.InvokeUiAction("editor.redo");
Check(runtime.World.Serialize() == before, "Redo restores the exact pre-reload document");
runtime.InvokeUiAction("editor.play");
var oldRegistry = runtime.Components;
File.AppendAllText(component.Path!, "\n// change while Playing\n");
WaitFor("Ready");
Check(runtime.PlayState == EditorPlayState.Playing && ReferenceEquals(runtime.Components, oldRegistry), "Play keeps old generation while build completes");
runtime.InvokeUiAction("editor.pause"); service.Tick();
Check(Status() == "Ready" && ReferenceEquals(runtime.Components, oldRegistry), "Paused also retains the old generation");
runtime.InvokeUiAction("editor.stop");
service.Tick();
Check(Status() == "Idle" && runtime.ProjectCodeAvailable, "Stop applies pending generation");
runtime.InvokeUiAction("editor.component-focus");
File.AppendAllText(component.Path!, "\n// edit while inspector has uncommitted input\n");
WaitFor("Ready");
Check(runtime.ProjectCodeAvailable, "reload waits for uncommitted Inspector input without disabling the old generation");
runtime.InvokeUiAction("editor.component-blur"); service.Tick();
Check(Status() == "Idle", "finishing input permits the pending reload");
File.AppendAllText(component.Path!, "\nthis is invalid C#\n");
var previousGeneration = Generation(runtime, component.ComponentTypeId!);
WaitFor("BuildFailed");
Check(runtime.ProjectCodeAvailable, "compile failure keeps prior valid generation available");
File.WriteAllText(component.Path!, File.ReadAllText(component.Path!).Replace("this is invalid C#", "// fixed"));
WaitFor("Idle");
Check(runtime.ProjectCodeAvailable, "compile failure recovers after source edit");
for (var attempt = 0; attempt < 10 && previousGeneration.IsAlive; attempt++)
{ GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(50); }
Check(!previousGeneration.IsAlive, "old project generation is collectible after successful replacement");
File.WriteAllText(moduleSource, EditorModule(false).Replace("public void Shutdown() { }", "public void Shutdown() { throw new System.Exception(\"expected Shutdown failure\"); }"));
WaitFor("Idle");
File.WriteAllText(moduleSource, EditorModule(false));
WaitFor("ReloadFailed");
Check(!runtime.ProjectCodeAvailable && !runtime.Commands.Commands.Any(command => command.Id == "validation.reload"),
    "Shutdown failure aborts activation but still clears engine registrations");
File.AppendAllText(moduleSource, "\n// retry after Shutdown failure\n");
WaitFor("Idle");
Check(runtime.ProjectCodeAvailable, "a later edit recovers after Shutdown failure without restart");
service.Dispose(); runtime.Shutdown();
Console.WriteLine($"PASS {assertions} source/reload assertions. Evidence: {root}");

string Status() => JsonNode.Parse(runtime.DescribeProjectCodeJson())!["state"]!.GetValue<string>();
void WaitFor(string expected)
{
    var timer = Stopwatch.StartNew();
    var sawBuild = false;
    string previous = "";
    while (timer.Elapsed < TimeSpan.FromSeconds(100))
    {
        service.Tick();
        var state = Status();
        if (state != previous) { Console.WriteLine("STATE " + state); previous = state; }
        sawBuild |= state == "Building";
        if (sawBuild && state == expected) return;
        if (sawBuild && state is "BuildFailed" or "ReloadFailed" && state != expected)
            throw new Exception(runtime.DescribeProjectCodeJson());
        Thread.Sleep(30);
    }
    throw new Exception("Timeout: " + runtime.DescribeProjectCodeJson());
}
static string EditorModule(bool fail) => $$"""
using SharpEngine.Modules;
using SharpEngine.Editor;
namespace Sample.Game.Editor;
public class Module : ISharpEngineModule {
 public void Startup(ModuleContext c) {
   var editor = ((EditorModuleContext)c).Editor;
   editor.Commands.Register(new EditorCommand("validation.reload", "Reload Test", _ => {}));
   {{(fail ? "editor.Runtime.InvokeUiAction(\"editor.component-create-entity\"); throw new System.Exception(\"expected Startup failure\");" : "")}}
 }
 public void Shutdown() { }
}
""";
static void CopyTree(string source, string destination)
{
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(source, file);
        if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")) continue;
        var target = Path.Combine(destination, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference Generation(EditorRuntime runtime, string typeId) =>
    new(AssemblyLoadContext.GetLoadContext(runtime.Components!.Find(typeId)!.RuntimeType.Assembly)!);
