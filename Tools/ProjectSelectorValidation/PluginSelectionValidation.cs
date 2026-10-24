using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.Assets;
using SharpEngine.Plugins;
using SharpEngine.ProjectSelector;

internal static class PluginSelectionValidation
{
    internal static void Run(string TemporaryRoot, Action<bool, string> Check)
    {
        var Root = Path.Combine(TemporaryRoot, "PluginSelection");
        var EngineRoot = Path.Combine(Root, "Engine");
        var ProjectRoot = Path.Combine(Root, "Project");
        Directory.CreateDirectory(EngineRoot);
        Directory.CreateDirectory(ProjectRoot);
        var EngineManifest = Path.Combine(EngineRoot, "engine.json");
        var ProjectManifest = Path.Combine(ProjectRoot, "Game.sharpproject");
        var Modules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Uri(string Id) { return $"engine://Plugins/{Id}/{Id}.sharpplugin"; }
        string[] PluginIds = ["A", "B", "C", "Shared"];
        foreach (var Id in PluginIds)
        {
            var DirectoryPath = Path.Combine(EngineRoot, "Plugins", Id);
            Directory.CreateDirectory(DirectoryPath);
            var Module = Path.Combine(DirectoryPath, Id + ".csproj");
            Modules.Add(Id, Module);
            File.WriteAllText(Module, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(DirectoryPath, Id + ".sharpplugin"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                id = Id,
                version = "1.0.0",
                modules = new[]
                {
                    Id + ".csproj",
                },
                assets = new
                {
                    manifest = "Content/assets.json",
                },
            }));
            Directory.CreateDirectory(Path.Combine(DirectoryPath, "Content"));
            File.WriteAllText(Path.Combine(DirectoryPath, "Content", "assets.json"), "{\"schemaVersion\":2,\"assets\":[]}");
        }
        void References(string Module, params string[] Dependencies)
        {
            var Document = XDocument.Load(Module);
            Document.Root!.Elements("ItemGroup").Remove();
            Document.Root.Add(new XElement("ItemGroup", Dependencies.Select(Dependency =>
                new XElement("ProjectReference", new XAttribute("Include", Dependency)))));
            Document.Save(Module);
        }
        References(Modules["A"], Modules["Shared"]);
        References(Modules["B"], Modules["A"]);
        References(Modules["C"], Modules["B"]);
        var Source = Path.Combine(ProjectRoot, "Source");
        Directory.CreateDirectory(Source);
        var GameModule = Path.Combine(Source, "Game.csproj");
        File.WriteAllText(GameModule, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        string[] OwnerRoots = [EngineRoot, ProjectRoot];
        foreach (var OwnerRoot in OwnerRoots)
        {
            Directory.CreateDirectory(Path.Combine(OwnerRoot, "Content", "Assets"));
            File.WriteAllText(Path.Combine(OwnerRoot, "Content", "Assets", "assets.json"), "{\"schemaVersion\":2,\"assets\":[]}");
        }
        File.WriteAllText(EngineManifest, JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            engineId = "SharpEngine",
            version = "0.2.0-dev",
            paths = new
            {
                source = "Source",
                binaries = "Binaries/{platform}/{configuration}/{target}",
            },
            plugins = Modules.Keys.Select(Uri).ToArray(),
        }));
        File.WriteAllText(ProjectManifest, JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            projectId = "plugin-test",
            engine = new
            {
                id = "SharpEngine",
                version = "0.2.0-dev",
                association = "plugin-test-unregistered",
                manifest = "engine.json",
            },
            modules = new[]
            {
                "Source/Game.csproj",
            },
            targets = new[]
            {
                "Editor",
            },
            plugins = new[]
            {
                Uri("A"),
            },
            disabledPlugins = Array.Empty<string>(),
            custom = "preserve-me",
        }));
        ResolvedSharpProject Resolve()
        {
            return new ResolvedSharpProject(ProjectContracts.ReadProject(ProjectManifest),
                ProjectContracts.ReadEngine(EngineRoot));
        }
        IReadOnlyList<string> ReadReferences(string PathValue)
        {
            return XDocument.Load(PathValue).Descendants("ProjectReference")
                .Select(Element => Path.GetFullPath(Element.Attribute("Include")!.Value)).ToArray();
        }
        void ExpectFailure(Action Work, string Code)
        {
            try
            {
                Work();
                Check(false, "Expected " + Code);
            }
            catch (InvalidDataException Error)
            {
                Check(Error.Message.Contains(Code, StringComparison.Ordinal), "Expected diagnostic " + Code);
            }
        }
        var Selection = PluginConfiguration.Read(EngineManifest, ProjectManifest);
        Check(Selection.Effective.Count == 4, "Engine defaults and explicit project plugins must deduplicate");
        var Registry = AssetRegistry.LoadFromManifests(EngineManifest, ProjectManifest);
        Check(Registry.Plugins.Count == 4 && !Registry.Diagnostics.Any(Diagnostic => Diagnostic.IsError),
            "Assets must mount each effective Plugin exactly once");
        var Graph = PluginGraph.Load(Resolve(), ReadReferences);
        var Before = File.ReadAllBytes(ProjectManifest);
        var Disable = Graph.Plan("A", false);
        Check(Disable.Blockers.Count == 0 && Disable.Changes.Select(Change => Change.Id).SequenceEqual(["A", "B", "C"]),
            "Disabling A must cascade through C -> B -> A and retain the shared dependency");
        Check(Before.SequenceEqual(File.ReadAllBytes(ProjectManifest)), "Planning must not persist configuration");
        Graph.Apply(Disable, Disable.PlanId);
        Selection = PluginConfiguration.Read(EngineManifest, ProjectManifest);
        Check(Selection.Effective.SequenceEqual([Uri("Shared")]), "Project must override inherited Engine defaults");
        Check(JsonNode.Parse(File.ReadAllText(ProjectManifest))!["custom"]!.GetValue<string>() == "preserve-me",
            "Atomic Plugin changes must preserve unrelated project fields");
        Registry = AssetRegistry.LoadFromManifests(EngineManifest, ProjectManifest);
        Check(Registry.Plugins.Count == 1 && Registry.Plugins[0].Id == "Shared", "Disabled Plugin assets must not remain mounted");
        Graph = PluginGraph.Load(Resolve(), ReadReferences);
        var Enable = Graph.Plan("C", true);
        Check(Enable.Changes.Count == 3 && Enable.Changes.All(Change => Change.ClearsExplicitDisable),
            "Enabling C must preview all transitive dependencies and every revoked disable");
        Graph.Apply(Enable, Enable.PlanId);
        Graph = PluginGraph.Load(Resolve(), ReadReferences);
        var Stale = Graph.Plan("A", false);
        File.AppendAllText(Modules["Shared"], "\n");
        ExpectFailure(() => Graph.Apply(Stale, Stale.PlanId), "SEPLUGIN031");
        References(GameModule, Modules["C"]);
        Graph = PluginGraph.Load(Resolve(), ReadReferences);
        var Blocked = Graph.Plan("A", false);
        Check(Blocked.Blockers.Any(Value => Value.Contains("Project Module") && Value.Contains("C")),
            "Project-code dependencies must block a reverse cascade");
        ExpectFailure(() => Graph.Apply(Blocked, Blocked.PlanId), "SEPLUGIN030");
        var Helper = Path.Combine(Source, "Helper.csproj");
        File.WriteAllText(Helper, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        References(Helper, Modules["C"]);
        References(GameModule, Helper);
        Graph = PluginGraph.Load(Resolve(), ReadReferences);
        Check(Graph.Plan("A", false).Blockers.Count > 0,
            "A transitive reference through a helper project must still block disabling Plugin dependencies");
        References(GameModule);
        var CoreDirectory = Path.Combine(EngineRoot, "Source", "Core");
        Directory.CreateDirectory(CoreDirectory);
        var CoreModule = Path.Combine(CoreDirectory, "Core.csproj");
        File.WriteAllText(CoreModule, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><SharpEngineRole>Runtime</SharpEngineRole><SharpEngineModuleId>Core</SharpEngineModuleId><SharpEngineModuleType>Runtime</SharpEngineModuleType></PropertyGroup></Project>");
        References(CoreModule, Modules["C"]);
        Graph = PluginGraph.Load(Resolve(), ReadReferences);
        Check(Graph.Plan("A", false).Blockers.Any(Value => Value.Contains("Core Module")),
            "Core Module dependencies must block a reverse cascade");
        References(CoreModule);
        References(Modules["A"], Modules["C"]);
        Graph = PluginGraph.Load(Resolve(), ReadReferences);
        Check(Graph.Plan("C", true).Blockers.Any(Value => Value.Contains("SEPLUGIN029")),
            "Dependency cycles must be diagnosed before mutation");
        References(Modules["A"], Modules["Shared"]);
        var Evaluated = PluginGraph.Load(Resolve());
        Check(Evaluated.Plan("A", false).Changes.Count == 3,
            "Actual MSBuild evaluation must produce the transitive Plugin impact graph");
        var Repeated = PluginGraph.Load(Resolve());
        Check(Evaluated.Plan("A", false).PlanId == Repeated.Plan("A", false).PlanId,
            "Repeated evaluation of unchanged semantic inputs must preserve the confirmation plan id");
        var EvaluatedPlan = Evaluated.Plan("A", false);
        Repeated.Apply(EvaluatedPlan, EvaluatedPlan.PlanId);
        Check(PluginConfiguration.Read(EngineManifest, ProjectManifest).Effective.SequenceEqual([Uri("Shared")]),
            "A plan from an independent real MSBuild analysis must apply without a false stale rejection");
        var Browser = new ProjectBrowser(EngineRoot, Path.Combine(Root, "recent.json"));
        var Preview = Browser.Preview(ProjectManifest);
        Check(Preview.Compatible && Preview.EngineVersion == "0.2.0-dev", "Project preview must read saved metadata without loading modules");
        Browser.Remember(ProjectManifest);
        Browser.Remember(ProjectManifest);
        Check(Browser.Recent().Count == 1, "Recent projects must deduplicate paths");
        var CoverPath = Path.Combine(ProjectRoot, "cover.png");
        using (var Cover = new System.Drawing.Bitmap(32, 16))
        {
            Cover.Save(CoverPath, System.Drawing.Imaging.ImageFormat.Png);
        }
        var CoverBytes = File.ReadAllBytes(CoverPath);
        var Texture = Browser.CreateCoverTexture(CoverPath, Path.Combine(Root, "CoverCache"));
        var TextureBytes = File.ReadAllBytes(Texture);
        Check(TextureBytes.Length == 18 + 32 * 16 * 4 && TextureBytes[2] == 2 && TextureBytes[16] == 32,
            "Saved image previews must become renderer-compatible derived TGA textures");
        Check(CoverBytes.SequenceEqual(File.ReadAllBytes(CoverPath)), "Preview conversion must preserve the authored cover image");
        var Ui = new BrowserUiApplication(Browser, "Debug", () => ProjectManifest, () => Root, _ =>
        {
        }, Path.Combine(Root, "CoverCache"));
        Check(Ui.Render().Contains("browser-path-input") && !Ui.Render().Contains("browser-plugins"),
            "The startup browser must only offer project workflows; Plugin management belongs to Editor");
        Ui.Handle("window.close-requested");
        Check(Ui.ShouldClose, "Browser must handle the native close-requested event without an unknown-action error");
        var ProjectJson = JsonNode.Parse(File.ReadAllText(ProjectManifest))!.AsObject();
        ProjectJson["previewImage"] = "../escape.png";
        File.WriteAllText(ProjectManifest, ProjectJson.ToJsonString());
        Check(!Browser.Preview(ProjectManifest).Compatible, "Project preview must reject a cover outside its project");
        ProjectJson.Remove("previewImage");
        ProjectJson["engine"]!["version"] = "incompatible";
        File.WriteAllText(ProjectManifest, ProjectJson.ToJsonString());
        Check(!Browser.Preview(ProjectManifest).Compatible, "Incompatible projects must not be openable");
    }
}
