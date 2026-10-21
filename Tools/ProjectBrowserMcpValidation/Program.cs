using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

var Configuration = args.FirstOrDefault() ?? "Shipping";
DirectoryInfo? RootDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (RootDirectory is not null && !File.Exists(Path.Combine(RootDirectory.FullName, "engine.json")))
{
    RootDirectory = RootDirectory.Parent;
}
var Root = RootDirectory?.FullName ?? throw new DirectoryNotFoundException("Engine root was not found");
var Evidence = Path.Combine(Root, "Saved", "Validation", "PluginMcp", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Evidence);
var Native = Path.Combine(Root, "Binaries", "Win64", Configuration, "Editor", "Engine.exe");
var Selector = Path.Combine(Root, "Binaries", "Win64", Configuration, "Tools", "SharpEngine.ProjectSelector.exe");
var Assertions = 0;
void Check(bool Condition, string Message)
{
    Assertions++;
    if (!Condition)
    {
        throw new InvalidOperationException(Message);
    }
}
JsonObject AwaitState(Func<JsonObject> Read, Func<JsonObject, bool> Ready)
{
    var Deadline = DateTime.UtcNow.AddSeconds(180);
    while (DateTime.UtcNow < Deadline)
    {
        var State = Read();
        if (Ready(State))
        {
            return State;
        }
        Thread.Sleep(100);
    }
    throw new TimeoutException("MCP state did not reach the expected committed result");
}
void BuildProject(string Project)
{
    var Start = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = Root,
    };
    foreach (var Argument in new[]
             {
                 "run", "--project", Path.Combine(Root, "Tools", "ProjectBuild", "SharpEngine.ProjectBuild.csproj"),
                 "--", "build", Project, "--engine-root", Root, "--configuration", Configuration, "--target", "Editor",
             })
    {
        Start.ArgumentList.Add(Argument);
    }
    using var Build = Process.Start(Start)!;
    var Output = Build.StandardOutput.ReadToEndAsync();
    var Errors = Build.StandardError.ReadToEndAsync();
    Build.WaitForExit();
    var Text = Output.GetAwaiter().GetResult() + Errors.GetAwaiter().GetResult();
    File.WriteAllText(Path.Combine(Evidence, "project-build.txt"), Text);
    Check(Build.ExitCode == 0, "The validation project must build: " + Text);
}
string Hash(string PathValue) { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PathValue))); }

using var BrowserClient = new McpClient(Selector, "--mcp-stdio");
BrowserClient.TraceTo(Path.Combine(Evidence, "browser-mcp.ndjson"));
BrowserClient.Call("initialize");
var Bootstrap = BrowserClient.DiscoverTools();
Check(Bootstrap.Count == 3 && Bootstrap.All(Tool => Tool!["name"]!.GetValue<string>().StartsWith("projectBrowser.")),
    "An unbound connection must only expose explicit session selection and launch");
Check(BrowserClient.Tool("projectBrowser.sessions.list")["sessions"] is JsonArray,
    "The caller must discover sessions before choosing a target");
var BrowserLaunch = BrowserClient.Tool("projectBrowser.session.launchIsolated", new JsonObject
{
    ["recentFile"] = Path.Combine(Evidence, "recent.json"),
});
using var Browser = Process.GetProcessById(BrowserLaunch["session"]!["processId"]!.GetValue<int>());
Check(BrowserLaunch["attached"]!.GetValue<bool>(), "The explicitly requested isolated browser must bind this connection");
string ProjectPath;
try
{
    var Client = BrowserClient;
    var Tools = Client.DiscoverTools();
    Check(Tools.Any(Tool => Tool!["name"]!.GetValue<string>() == "browser.describe") &&
        Tools.Any(Tool => Tool!["name"]!.GetValue<string>() == "ui.pointer"), "Attached tools must discover semantic state and the real input route");
    Check(Client.Call("resources/list")["resources"]!.AsArray().Count == 2 &&
        Client.Call("resources/templates/list")["resourceTemplates"]!.AsArray().Count == 1,
        "MCP must discover state and matching frame image resources");
    var State = Client.Tool("browser.describe");
    Check(!State["editorAssemblyLoaded"]!.GetValue<bool>() && !State["projectModulesLoaded"]!.GetValue<bool>(),
        "The projectless browser must not load Editor or project Modules");
    var Tree = Client.Ui().ToJsonString();
    Check(!Tree.Contains("browser-plugins"), "Plugin analysis must not appear on the startup screen");
    Client.Capture(Evidence, "browser-empty", "main");
    Client.Click("main", "browser-new");
    Client.Type("main", "browser-name-input", "McpProject");
    Client.Type("main", "browser-parent-input", Evidence);
    Client.Click("main", "browser-create");
    State = AwaitState(() => Client.Tool("browser.describe"), Value => !Value["busy"]!.GetValue<bool>());
    File.WriteAllText(Path.Combine(Evidence, "create-state.json"), State.ToJsonString());
    Check(State["diagnostic"]!.GetValue<string>() == "", "Project creation must complete through the shared semantic command: " + State.ToJsonString());
    ProjectPath = State["selectedProject"]!["manifestPath"]!.GetValue<string>();
    Check(File.Exists(ProjectPath), "The RmlUi create button must produce a real project manifest");
    var Before = Hash(ProjectPath);
    Client.Type("main", "browser-path-input", ProjectPath);
    Client.Click("main", "browser-load-path");
    State = Client.Tool("browser.describe");
    Check(State["selectedProject"]!["compatible"]!.GetValue<bool>() && Hash(ProjectPath) == Before,
        "Static preview must display a compatible project without modifying its manifest");
    Check(!State["editorAssemblyLoaded"]!.GetValue<bool>() && !State["projectModulesLoaded"]!.GetValue<bool>(),
        "Creating and previewing projects must not initialize their code");
    Client.Capture(Evidence, "browser-preview", "main");
    if (args.Contains("--layout-only", StringComparer.Ordinal))
    {
        BrowserLayoutValidation.Run(Client, Browser.Id, Evidence, Check);
    }
    Client.Click("main", "browser-window-close");
    Check(Browser.WaitForExit(10000),
        "The real RmlUi close button must complete native close-requested without an unknown-action error");
}
finally
{
    if (!Browser.HasExited)
    {
        Browser.Kill(true);
        Browser.WaitForExit();
    }
}
if (args.Contains("--layout-only", StringComparer.Ordinal))
{
    File.WriteAllText(Path.Combine(Evidence, "project-path.txt"), ProjectPath);
    Console.WriteLine($"PASS: {Assertions} Project Browser responsive layout assertions; evidence={Evidence}");
    return;
}
BuildProject(ProjectPath);
using var EditorClient = new McpClient(Native, "--mcp-stdio");
EditorClient.TraceTo(Path.Combine(Evidence, "editor-mcp.ndjson"));
EditorClient.Call("initialize");
EditorClient.DiscoverTools();
var EditorLaunch = EditorClient.Tool("editor.session.launchIsolated", new JsonObject
{
    ["projectManifest"] = ProjectPath,
});
using var Editor = Process.GetProcessById(EditorLaunch["session"]!["processId"]!.GetValue<int>());
Check(EditorLaunch["attached"]!.GetValue<bool>(), "The Editor must be explicitly launched and selected through MCP");
try
{
    var Client = EditorClient;
    var Tools = Client.DiscoverTools();
    Check(Tools.Any(Tool => Tool!["name"]!.GetValue<string>() == "plugins.describe") &&
        Tools.Any(Tool => Tool!["name"]!.GetValue<string>() == "plugins.request"), "Editor must self-describe its project Plugin commands");
    Client.Click("main", "menu-root-file");
    Client.Click("main", "menu-file-category-settings-command-core.project-plugins");
    File.WriteAllText(Path.Combine(Evidence, "plugins-open-state.json"), Client.Tool("plugins.describe").ToJsonString());
    File.WriteAllText(Path.Combine(Evidence, "plugins-open-ui.json"), Client.Ui().ToJsonString());
    Client.Click("settings.plugins", "plugins-analyze");
    var State = AwaitState(() => Client.Tool("plugins.describe"), Value => !Value["busy"]!.GetValue<bool>());
    Check(State["diagnostic"] is null && State["plugins"]!.AsArray().Count >= 2, "Editor Plugin analysis must discover engine Plugins");
    var Plugin = State["plugins"]!.AsArray().Select((Node, Index) => (Node, Index))
        .Single(Value => Value.Node!["id"]!.GetValue<string>() == "sharpengine.test.engine-plugin");
    var Before = Hash(ProjectPath);
    Client.Click("settings.plugins", "plugins-enable-" + Plugin.Index);
    State = AwaitState(() => Client.Tool("plugins.describe"), Value => !Value["busy"]!.GetValue<bool>());
    Check(State["pendingPlan"] is not null && Hash(ProjectPath) == Before, "Dependency preview must not save configuration");
    Check(Client.Element(Client.Ui(), "settings.plugins", "plugins-change-0")["text"]!.GetValue<string>()
        .Contains("sharpengine.test.engine-plugin"), "The full change must be visible in the actual rendered confirmation UI");
    Client.Capture(Evidence, "plugin-plan", "settings.plugins");
    Client.Click("settings.plugins", "plugins-cancel");
    Check(Hash(ProjectPath) == Before && Client.Tool("plugins.describe")["pendingPlan"] is null,
        "Cancelling through real RmlUi input must preserve the project file");
    Client.Click("settings.plugins", "plugins-enable-" + Plugin.Index);
    AwaitState(() => Client.Tool("plugins.describe"), Value => !Value["busy"]!.GetValue<bool>());
    Client.Click("settings.plugins", "plugins-confirm");
    State = AwaitState(() => Client.Tool("plugins.describe"), Value => !Value["busy"]!.GetValue<bool>());
    Check(State["diagnostic"] is null && State["restartRequired"]!.GetValue<bool>(),
        "Confirm must save the exact analyzed plan and require Editor restart: " + State.ToJsonString());
    Check(JsonNode.Parse(File.ReadAllText(ProjectPath))!["plugins"]!.AsArray().Any(Value =>
        Value!.GetValue<string>().Contains("EngineTestPlugin")), "Confirmed Plugin selection must persist to the project manifest");
    Check(Client.Tool("source.status")["restartRequired"]!.GetValue<bool>(),
        "Plugin configuration must block code activation until restart");
    Client.Capture(Evidence, "plugin-saved", "settings.plugins");
    Client.Tool("editor.action", new JsonObject
    {
        ["action"] = "window.close-requested",
    });
    Check(Editor.WaitForExit(10000), "The isolated validation Editor must close through its semantic command");
}
finally
{
    if (!Editor.HasExited)
    {
        Editor.Kill(true);
        Editor.WaitForExit();
    }
}
File.WriteAllText(Path.Combine(Evidence, "result.json"), new JsonObject
{
    ["schemaVersion"] = 1,
    ["assertions"] = Assertions,
    ["configuration"] = Configuration,
    ["inputRoute"] = "MCP ui.pointer/ui.key with observed frameId and expectedElementId",
}.ToJsonString());
Console.WriteLine($"PASS: {Assertions} real MCP Browser/Plugin assertions; evidence={Evidence}");
