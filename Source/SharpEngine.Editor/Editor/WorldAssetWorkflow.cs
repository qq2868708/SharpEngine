using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.World;

namespace SharpEngine.Editor;

internal sealed record EditorWorldLoadResult(
    WorldDocument Document, bool IsUntitled, string? AssetId);

internal sealed record EditorWorldSaveResult(string Path, string AssetId);

internal static class WorldAssetWorkflow
{
    public static EditorWorldLoadResult LoadInitial(string engineRoot, string? projectRoot,
        string? projectManifest, AssetRegistry assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (projectRoot is not null && projectManifest is not null)
        {
            var manifest = ReadObject(projectManifest);
            if (manifest["world"] is JsonObject world && world.ContainsKey("startup"))
            {
                var startup = world["startup"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(startup))
                    throw new InvalidDataException("Project world.startup must be a non-empty .sworld path");
                var path = ResolveUnder(projectRoot, startup, "Project world.startup");
                RequireWorldExtension(path);
                if (!File.Exists(path))
                    throw new FileNotFoundException("Configured startup World does not exist", path);
                var document = WorldDocument.Load(path);
                return new(document, false, FindAssetId(assets, path));
            }

            var preset = RequireDefaultWorld(assets);
            var presetPath = assets.ResolvePath(preset) ?? throw new InvalidDataException(
                $"Default World '{preset.Id}' has no source path");
            var source = WorldDocument.Load(presetPath);
            var clone = WorldDocument.Parse(source.Serialize(),
                "untitled:default-world");
            return new(clone, true, null);
        }

        var engineWorld = RequireDefaultWorld(assets);
        var enginePath = assets.ResolvePath(engineWorld) ?? throw new InvalidDataException(
            $"Default World '{engineWorld.Id}' has no source path");
        return new(WorldDocument.Load(enginePath), false, engineWorld.Id);
    }

    public static EditorWorldSaveResult SaveUntitled(WorldDocument world, string projectRoot,
        string projectManifestPath, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(world);
        var fullProjectRoot = Path.GetFullPath(projectRoot);
        var contentRoot = Path.Combine(fullProjectRoot, "Content");
        var target = ResolveUnder(contentRoot, Path.GetRelativePath(contentRoot,
            Path.GetFullPath(targetPath)), "World save target");
        RequireWorldExtension(target);
        var contentRelative = Path.GetRelativePath(contentRoot, target)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (contentRelative.StartsWith("../", StringComparison.Ordinal) ||
            Path.IsPathRooted(contentRelative))
            throw new InvalidDataException("World save target must remain under project Content");
        var idPath = contentRelative[..^".sworld".Length].ToLowerInvariant();
        if (idPath.Split('/').Any(segment => segment.Length == 0 || segment.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '_' and not '-' and not '.')))
            throw new InvalidDataException(
                "World file name must use letters, digits, '.', '_' or '-' so it can form a stable asset id");
        var assetId = $"project://{idPath}";

        var projectManifest = ReadObject(projectManifestPath);
        var assetManifestRelative = projectManifest["assets"]?["manifest"]?.GetValue<string>() ??
            "Content/Assets/assets.json";
        var assetManifestPath = ResolveUnder(fullProjectRoot, assetManifestRelative,
            "Project asset manifest");
        var assetManifest = ReadObject(assetManifestPath);
        if (assetManifest["schemaVersion"]?.GetValue<int>() != 1 ||
            assetManifest["assets"] is not JsonArray assetArray)
            throw new InvalidDataException("Project asset manifest requires schemaVersion=1 and an assets array");

        JsonObject? matchingId = null;
        foreach (var node in assetArray)
        {
            if (node is not JsonObject item) continue;
            var rawId = item["id"]?.GetValue<string>() ?? string.Empty;
            var normalizedId = rawId.Contains("://", StringComparison.Ordinal)
                ? rawId : $"project://{rawId}";
            var path = item["path"]?.GetValue<string>()?.Replace('\\', '/') ?? string.Empty;
            if (string.Equals(normalizedId, assetId, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(path, $"Content/{contentRelative}", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Asset id '{assetId}' already belongs to '{path}'");
                matchingId = item;
            }
            else if (string.Equals(path, $"Content/{contentRelative}",
                         StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"World path 'Content/{contentRelative}' is already registered as '{normalizedId}'");
        }
        if (matchingId is null)
        {
            matchingId = new JsonObject();
            assetArray.Add(matchingId);
        }
        matchingId["id"] = idPath;
        matchingId["type"] = "World";
        matchingId["name"] = Path.GetFileNameWithoutExtension(target);
        matchingId["path"] = $"Content/{contentRelative}";
        matchingId["importer"] = "SharpEngine.WorldJson";
        matchingId["tags"] ??= new JsonArray("scene");

        var projectWorld = projectManifest["world"] as JsonObject ?? new JsonObject();
        projectWorld["startup"] = Path.GetRelativePath(fullProjectRoot, target)
            .Replace(Path.DirectorySeparatorChar, '/');
        projectManifest["world"] = projectWorld;

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var staged = new List<StagedFile>();
        try
        {
            staged.Add(StageWorld(world, target));
            staged.Add(StageText(assetManifestPath, assetManifest.ToJsonString(jsonOptions) + "\n"));
            staged.Add(StageText(projectManifestPath, projectManifest.ToJsonString(jsonOptions) + "\n"));
            Commit(staged);
        }
        finally
        {
            foreach (var file in staged) file.Cleanup();
        }
        world.SetSourcePath(target);
        return new(target, assetId);
    }

    private static AssetDescriptor RequireDefaultWorld(AssetRegistry assets)
    {
        if (assets.DefaultWorldId is null)
            throw new InvalidDataException("Engine manifest does not declare worldPresets.default");
        var asset = assets.Find(assets.DefaultWorldId);
        return asset is { Exists: true } &&
            string.Equals(asset.Type, "World", StringComparison.OrdinalIgnoreCase)
            ? asset : throw new InvalidDataException(
                $"Default World asset '{assets.DefaultWorldId}' is missing or invalid");
    }

    private static string? FindAssetId(AssetRegistry assets, string path) =>
        assets.Assets.FirstOrDefault(asset => assets.ResolvePath(asset) is { } candidate &&
            string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase))?.Id;

    private static JsonObject ReadObject(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("JSON file was not found", path);
        return JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions
        {
            PropertyNameCaseInsensitive = false,
        }, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        }) as JsonObject ?? throw new InvalidDataException($"JSON root must be an object: {path}");
    }

    private static string ResolveUnder(string root, string relative, string label)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException($"{label} must be a relative path");
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var check = Path.GetRelativePath(fullRoot, path);
        if (check == ".." || check.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || Path.IsPathRooted(check))
            throw new InvalidDataException($"{label} escapes its owning root");
        return path;
    }

    private static void RequireWorldExtension(string path)
    {
        if (!path.EndsWith(".sworld", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"World assets must use the .sworld extension: {path}");
    }

    private static StagedFile StageWorld(WorldDocument world, string target)
    {
        var stage = StagedFile.Create(target);
        Directory.CreateDirectory(Path.GetDirectoryName(stage.TemporaryPath)!);
        File.WriteAllText(stage.TemporaryPath, world.Serialize());
        _ = WorldDocument.Load(stage.TemporaryPath);
        return stage;
    }

    private static StagedFile StageText(string target, string content)
    {
        var stage = StagedFile.Create(target);
        Directory.CreateDirectory(Path.GetDirectoryName(stage.TemporaryPath)!);
        File.WriteAllText(stage.TemporaryPath, content, new System.Text.UTF8Encoding(false));
        _ = JsonNode.Parse(File.ReadAllText(stage.TemporaryPath));
        return stage;
    }

    private static void Commit(List<StagedFile> files)
    {
        var committed = new List<StagedFile>();
        try
        {
            foreach (var file in files)
            {
                file.Backup();
                File.Move(file.TemporaryPath, file.TargetPath, overwrite: true);
                committed.Add(file);
            }
        }
        catch
        {
            foreach (var file in committed.AsEnumerable().Reverse()) file.Rollback();
            throw;
        }
    }

    private sealed class StagedFile
    {
        private StagedFile(string targetPath)
        {
            TargetPath = Path.GetFullPath(targetPath);
            var directory = Path.GetDirectoryName(TargetPath)!;
            TemporaryPath = Path.Combine(directory,
                $".{Path.GetFileName(TargetPath)}.{Guid.NewGuid():N}.tmp");
            BackupPath = Path.Combine(directory,
                $".{Path.GetFileName(TargetPath)}.{Guid.NewGuid():N}.bak");
        }

        public string TargetPath { get; }
        public string TemporaryPath { get; }
        public string BackupPath { get; }
        public bool Existed { get; private set; }

        public static StagedFile Create(string targetPath) => new(targetPath);

        public void Backup()
        {
            Existed = File.Exists(TargetPath);
            if (Existed) File.Copy(TargetPath, BackupPath, overwrite: false);
        }

        public void Rollback()
        {
            if (Existed && File.Exists(BackupPath)) File.Copy(BackupPath, TargetPath, true);
            else if (!Existed && File.Exists(TargetPath)) File.Delete(TargetPath);
        }

        public void Cleanup()
        {
            if (File.Exists(TemporaryPath)) File.Delete(TemporaryPath);
            if (File.Exists(BackupPath)) File.Delete(BackupPath);
        }
    }
}
