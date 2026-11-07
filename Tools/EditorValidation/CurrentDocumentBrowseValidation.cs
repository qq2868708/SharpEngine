using System.Text.Json.Nodes;
using SharpEngine.Editor;

internal static class CurrentDocumentBrowseValidation
{
    internal static void Run(string Root, string TemporaryRoot, Action<bool, string> Check)
    {
        var PreviousRoot = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT");
        var PreviousManifest = Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST");
        try
        {
            var Index = 0;
            foreach (var RelativePath in new[]
            {
                "Worlds/Main.sworld", "Worlds\\Main.sworld", "wOrLdS/mAiN.sWoRlD",
                "Worlds/Main.sworld.", "Worlds/Main.sworld ",
            })
            {
                var Folder = Path.Combine(TemporaryRoot, "DocumentBrowse", (Index++).ToString());
                Directory.CreateDirectory(Path.Combine(Folder, "Content", "Worlds"));
                Directory.CreateDirectory(Path.Combine(Folder, "Content", "Assets"));
                File.Copy(Path.Combine(Root, "Content", "Worlds", "Main.sworld"),
                    Path.Combine(Folder, "Content", "Worlds", "Main.sworld"));
                File.WriteAllText(Path.Combine(Folder, "Content", "Assets", "assets.json"), new JsonObject
                {
                    ["schemaVersion"] = 2,
                    ["assets"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "worlds/current",
                            ["type"] = "World",
                            ["path"] = RelativePath,
                        },
                    },
                }.ToJsonString());
                var Manifest = Path.Combine(Folder, "Browse.sharpproject");
                File.WriteAllText(Manifest, """
                    {"schemaVersion":3,"projectId":"document-browse-validation",
                     "displayName":"Document Browse Validation","engineVersion":"0.2.0-dev",
                     "engine":{"id":"SharpEngine","version":"0.2.0-dev","manifest":"engine.json","association":"document-browse-validation"},
                     "modules":[],"world":{"startup":"Content/Worlds/Main.sworld"}}
                    """);
                Environment.SetEnvironmentVariable("SHARPENGINE_ROOT", Root);
                Environment.SetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST", Manifest);
                var Runtime = new EditorRuntime(0, 900, 600, 0);
                try
                {
                    Runtime.Initialize();
                    var WorldSource = Runtime.World!.Serialize();
                    var Revision = Runtime.DocumentRevision;
                    Runtime.InvokeUiAction("editor.command:core.browse-current-level");
                    Check(Runtime.Content!.SelectedId == "project://worlds/current",
                        "Browse resolves the project owner with a shared engine filename and path alias: " + RelativePath);
                    Check(Runtime.World.Serialize() == WorldSource && Runtime.DocumentRevision == Revision && !Runtime.IsDirty,
                        "Browse remains observational for path alias: " + RelativePath);
                }
                finally
                {
                    Runtime.Shutdown();
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPENGINE_ROOT", PreviousRoot);
            Environment.SetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST", PreviousManifest);
        }
    }
}
