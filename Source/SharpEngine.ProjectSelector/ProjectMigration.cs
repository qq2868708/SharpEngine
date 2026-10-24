using System.Text.Json.Nodes;

namespace SharpEngine.ProjectSelector;

internal static class ProjectMigration
{
    internal static void CreateStartupWorld(ResolvedSharpProject Resolved)
    {
        var Project = JsonNode.Parse(File.ReadAllText(Resolved.Project.ManifestPath))!.AsObject();
        if (Project["world"]?["startup"] is not null)
        {
            return;
        }
        var Engine = JsonNode.Parse(File.ReadAllText(Resolved.Engine.ManifestPath))!;
        var Preset = Engine["worldPresets"]!["default"]!.GetValue<string>().Replace("engine://", "", StringComparison.Ordinal);
        var Assets = JsonNode.Parse(File.ReadAllText(Path.Combine(Resolved.Engine.Root,
            Engine["assets"]!["manifest"]!.GetValue<string>())))!;
        var ManifestPath = Path.Combine(Resolved.Project.Root, Project["assets"]!["manifest"]!.GetValue<string>());
        var Manifest = JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();
        if (Assets["schemaVersion"]?.GetValue<int>() != 2 || Manifest["schemaVersion"]?.GetValue<int>() != 2)
        {
            throw new InvalidDataException("Asset manifests require schemaVersion=2 and Content-relative paths");
        }
        var Source = Assets["assets"]!.AsArray().Single(Asset => Asset!["id"]!.GetValue<string>() == Preset)!["path"]!.GetValue<string>();
        var Destination = Path.Combine(Resolved.Project.Root, "Content", "Worlds", "Main.sworld");
        if (File.Exists(Destination))
        {
            throw new IOException("Cannot initialize startup World: Main.sworld already exists");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        File.Copy(Path.Combine(Resolved.Engine.Root, "Content", Source), Destination);
        Manifest["assets"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "worlds/main",
            ["type"] = "World",
            ["name"] = "Main",
            ["path"] = "Worlds/Main.sworld",
        });
        var Options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Default)
        {
            WriteIndented = true,
        };
        File.WriteAllText(ManifestPath, Manifest.ToJsonString(Options));
        Project["world"] = new JsonObject
        {
            ["startup"] = "Content/Worlds/Main.sworld",
        };
        File.WriteAllText(Resolved.Project.ManifestPath, Project.ToJsonString(Options));
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
