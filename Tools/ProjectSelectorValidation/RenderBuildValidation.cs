using System.Text.Json;
using System.Xml.Linq;
using SharpEngine.ProjectSelector;

internal static class RenderBuildValidation
{
    internal static void Run(string EngineRoot, string TemporaryRoot, Action<bool, string> Check)
    {
        using var Legacy = JsonDocument.Parse("{}");
        Check(RenderBuildOptions.Read(Legacy.RootElement) == "Both",
            "Existing manifests must retain both render backends");
        foreach (var Value in new[] { "OpenGL", "Both" })
        {
            using var Document = JsonDocument.Parse(JsonSerializer.Serialize(new { renderBackends = Value }));
            Check(RenderBuildOptions.Read(Document.RootElement) == Value,
                "Render backend build selection must round-trip: " + Value);
        }
        foreach (var Json in new[] { "{\"renderBackends\":null}", "{\"renderBackends\":42}",
                     "{\"renderBackends\":\"Vulkan\"}", "{\"renderBackends\":\"opengl\"}" })
        {
            var Rejected = false;
            try
            {
                using var Document = JsonDocument.Parse(Json);
                _ = RenderBuildOptions.Read(Document.RootElement);
            }
            catch (InvalidDataException)
            {
                Rejected = true;
            }
            Check(Rejected, "Invalid render build settings must fail: " + Json);
        }

        var Root = Path.Combine(TemporaryRoot, "RenderBuild");
        Directory.CreateDirectory(Path.Combine(Root, "Source", "Game"));
        File.WriteAllText(Path.Combine(Root, "Source", "Game", "Game.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var ManifestPath = Path.Combine(Root, "Game.sharpproject");
        var Engine = ProjectContracts.ReadEngine(EngineRoot);
        foreach (var Value in new[] { "OpenGL", "Both" })
        {
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                projectId = "render-build",
                renderBackends = Value,
                engine = new
                {
                    id = Engine.Id,
                    version = Engine.Version,
                    association = "render-build-validation",
                },
                modules = new[] { "Source/Game/Game.csproj" },
            }));
            var Project = ProjectContracts.ReadProject(ManifestPath);
            var Generated = SolutionGenerator.EnsureCurrent(new ResolvedSharpProject(Project, Engine));
            var Wrapper = XDocument.Load(Generated.WrapperProjectPath);
            Check(Wrapper.Descendants("SharpEngineRenderBackends").Single().Value == Value,
                "Generated Rider target must follow manifest changes: " + Value);
            var Build = Wrapper.Descendants("Target").Single(Element =>
                Element.Attribute("Name")?.Value == "BuildSharpEngineTarget");
            Check(Build.Element("Exec")!.Attribute("Command")!.Value.Contains(
                    "-RenderBackends $(SharpEngineRenderBackends)", StringComparison.Ordinal),
                "Rider target must forward the overridable render build setting");
        }
    }
}
