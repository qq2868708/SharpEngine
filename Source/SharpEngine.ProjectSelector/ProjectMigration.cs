using System.Text.Json.Nodes;

namespace SharpEngine.ProjectSelector;

internal static class ProjectMigration
{
    internal static void CreateStartupWorld(ResolvedSharpProject resolved)
    {
        var project = JsonNode.Parse(File.ReadAllText(resolved.Project.ManifestPath))!.AsObject();
        if (project["world"]?["startup"] is not null) return;
        var engine = JsonNode.Parse(File.ReadAllText(resolved.Engine.ManifestPath))!;
        var preset = engine["worldPresets"]!["default"]!.GetValue<string>().Replace("engine://", "", StringComparison.Ordinal);
        var assets = JsonNode.Parse(File.ReadAllText(Path.Combine(resolved.Engine.Root,
            engine["assets"]!["manifest"]!.GetValue<string>())))!;
        var source = assets["assets"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == preset)!["path"]!.GetValue<string>();
        var destination = Path.Combine(resolved.Project.Root, "Content", "Worlds", "Main.sworld");
        if (File.Exists(destination)) throw new IOException("Cannot initialize startup World: Main.sworld already exists");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(Path.Combine(resolved.Engine.Root, source), destination);
        var manifestPath = Path.Combine(resolved.Project.Root, project["assets"]!["manifest"]!.GetValue<string>());
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["assets"]!.AsArray().Add(new JsonObject { ["id"] = "worlds/main", ["type"] = "World",
            ["name"] = "Main", ["path"] = "Content/Worlds/Main.sworld" });
        File.WriteAllText(manifestPath, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Default) { WriteIndented = true }));
        project["world"] = new JsonObject { ["startup"] = "Content/Worlds/Main.sworld" };
        File.WriteAllText(resolved.Project.ManifestPath, project.ToJsonString(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Default) { WriteIndented = true }));
    }

    // Explicit migration only; normal Generate never edits authored project code.
    internal static void Migrate(ResolvedSharpProject resolved)
    {
        var root = resolved.Project.Root;
        var paths = Directory.EnumerateFiles(Path.Combine(root, "Source"), "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".props" or ".targets")
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin"))
            .Concat(new[] { Path.Combine(root, "Directory.Build.props") }).Where(File.Exists);
        foreach (var path in paths)
        {
            var old = File.ReadAllText(path);
            var migrated = old.Replace("SharpEngine.Modules", "SharpEngine.Runtime", StringComparison.Ordinal)
                .Replace("SharpEngine.Managed", "SharpEngine.Editor", StringComparison.Ordinal)
                .Replace("SharpEngineModulesApi", "SharpEngineRuntimeApi", StringComparison.Ordinal)
                .Replace("SharpEngineManagedApi", "SharpEngineEditorApi", StringComparison.Ordinal);
            if (old == migrated) continue;
            var backup = Path.Combine(root, "Saved", "Migrations", "RuntimeEditorSplit", Path.GetRelativePath(root, path));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            if (!File.Exists(backup)) File.Copy(path, backup);
            File.WriteAllText(path, migrated);
        }
    }
}
