using System.Text.Json.Nodes;
using SharpEngine.Assets;

internal static class AssetIndexValidation
{
    internal static void Run(string Root, Action<bool, string> Check)
    {
        var First = CreateFixture(Path.Combine(Root, "AssetIndex", "First"));
        var Second = CreateFixture(Path.Combine(Root, "AssetIndex", "Second"));
        var Registry = First.Registry;
        foreach (var Id in new[] { "engine://ui/screen", "project://ui/screen", "project://plugins/test.plugin/ui/screen" })
        {
            var Asset = Registry.Find(Id)!;
            Check(Asset.RelativePath == "UI/Screen.sui" && Asset.Exists,
                "Engine, project and plugin assets use the same Content-relative path without identity collisions");
            var Owner = Asset.OwnerId is null
                ? Asset.Source == AssetSource.Engine ? First.Engine : First.Project
                : Path.Combine(First.Project, "Plugins", "Test");
            Check(Registry.ResolvePath(Asset) == Path.Combine(Owner, "Content", "UI", "Screen.sui"),
                "Each stable asset ID resolves inside its own Content mount");
        }
        Check(Registry.Diagnostics.Count == 0 && Registry.ToJson() == Second.Registry.ToJson(),
            "The exported index is independent of the installation and project directories");
        var Snapshot = JsonNode.Parse(Registry.ToJson())!;
        Check(Snapshot["schemaVersion"]!.GetValue<int>() == 2 &&
            Snapshot["engineRoot"] is null && Snapshot["projectRoot"] is null &&
            Snapshot["plugins"]![0]!["manifest"]!.GetValue<string>() == "project://Plugins/Test/Test.sharpplugin",
            "Index v2 exposes logical plugin locations and omits physical source roots");
        Check(!Registry.ToJson().Contains(Root, StringComparison.OrdinalIgnoreCase) &&
            Snapshot["assets"]![0]!["metadata"]!["external"]!.GetValue<string>() == "[external path]",
            "Index metadata cannot expose source machine paths");
        Check(Snapshot["assets"]![0]!["metadata"]!["literal"]!.GetValue<string>() == "text\\nvalue" &&
            Snapshot["assets"]![0]!["metadata"]!["spaced"]!.GetValue<string>() == "[external path]",
            "Path redaction handles spaces and preserves unrelated escaped text");

        var Manifest = Path.Combine(First.Project, "Content", "Assets", "assets.json");
        foreach (var Version in new[] { 1, 99 })
        {
            File.WriteAllText(Manifest, new JsonObject
            {
                ["schemaVersion"] = Version,
                ["assets"] = new JsonArray(),
            }.ToJsonString());
            var Invalid = AssetRegistry.Load(First.Engine, First.Project);
            Check(Invalid.Diagnostics.Any(Diagnostic => Diagnostic.IsError && Diagnostic.Message.Contains("schemaVersion=2", StringComparison.Ordinal)),
                "Old and future asset manifests fail explicitly instead of guessing path semantics");
        }
        foreach (var PathValue in new[] { "../secret.sui", "UI/../../secret.sui", "UI/./Screen.sui", "UI//Screen.sui", "C:/Private/User/secret.sui", "//server/private/secret.sui", "/private/secret.sui", "C:/Private/invalid\0.sui" })
        {
            File.WriteAllText(Manifest, ManifestText(PathValue, First.Project));
            var Invalid = AssetRegistry.Load(First.Engine, First.Project);
            var Text = Invalid.ToJson();
            var Diagnostics = JsonNode.Parse(Text)!["diagnostics"]!.ToJsonString();
            Check(Invalid.Find("project://ui/screen") is null && Invalid.Diagnostics.Any(Diagnostic => Diagnostic.IsError),
                "Asset index rejects traversal, noncanonical paths and physical absolute paths");
            Check(!Diagnostics.Contains("C:/Private", StringComparison.Ordinal) &&
                !Diagnostics.Contains("//server/private", StringComparison.Ordinal) &&
                !Diagnostics.Contains("/private/secret", StringComparison.Ordinal) &&
                !Diagnostics.Contains(Root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase),
                "Rejected asset paths are redacted in exported diagnostics");
        }
    }

    private static (AssetRegistry Registry, string Engine, string Project) CreateFixture(string Root)
    {
        var Engine = Path.Combine(Root, "Engine");
        var Project = Path.Combine(Root, "Project");
        var Plugin = Path.Combine(Project, "Plugins", "Test");
        foreach (var Owner in new[] { Engine, Project, Plugin })
        {
            Directory.CreateDirectory(Path.Combine(Owner, "Content", "Assets"));
            Directory.CreateDirectory(Path.Combine(Owner, "Content", "UI"));
            File.WriteAllText(Path.Combine(Owner, "Content", "UI", "Screen.sui"), "definition");
            File.WriteAllText(Path.Combine(Owner, "Content", "Assets", "assets.json"), ManifestText("UI/Screen.sui", Engine));
        }
        File.WriteAllText(Path.Combine(Engine, "engine.json"), """
            {"schemaVersion":2,"plugins":[],"assets":{"manifest":"Content/Assets/assets.json"}}
            """);
        File.WriteAllText(Path.Combine(Project, "Test.sharpproject"), """
            {"schemaVersion":3,"plugins":["project://Plugins/Test/Test.sharpplugin"],"assets":{"manifest":"Content/Assets/assets.json"}}
            """);
        File.WriteAllText(Path.Combine(Plugin, "Test.sharpplugin"), """
            {"schemaVersion":1,"id":"test.plugin","version":"1","modules":[],"assets":{"manifest":"Content/Assets/assets.json"}}
            """);
        return (AssetRegistry.Load(Engine, Project), Engine, Project);
    }

    private static string ManifestText(string PathValue, string Engine)
    {
        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["assets"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "ui/screen",
                    ["type"] = "UiDocument",
                    ["path"] = PathValue,
                    ["metadata"] = new JsonObject
                    {
                        ["source"] = Path.Combine(Engine, "Resources", "Source.blend"),
                        ["external"] = "Z:/Private/User/Source.blend",
                        ["spaced"] = "Z:\\Private User\\Source File.blend",
                        ["literal"] = "text\\nvalue",
                    },
                },
            },
        }.ToJsonString();
    }
}
