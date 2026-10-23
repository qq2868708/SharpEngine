using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.Assets;
using SharpEngine.Editor;
using SharpEngine.Input;
using SharpEngine.World;

[DataAssetType("validation.creation-data")]
public sealed class CreationValidationData : DataAsset<CreationValidationData>
{
    public int Count
    {
        get;
        set;
    } = 7;
}

internal static class ContentCreationValidation
{
    internal static void Run(EditorRuntime Runtime, string ProjectRoot, Action<bool, string> Check)
    {
        Runtime.RegisteredDataAssetTypes.Register<CreationValidationData>();
        var WorldBefore = Runtime.World!.Serialize();
        var RevisionBefore = Runtime.DocumentRevision;
        var ProjectManifest = File.ReadAllText(Runtime.ProjectManifest!);
        var ManifestPath = Path.Combine(ProjectRoot, "Content/Assets/assets.json");
        var DirectoryPath = Path.GetFullPath(Path.Combine(ProjectRoot, "Content/CreationValidation"));
        Directory.CreateDirectory(DirectoryPath);
        string Revision()
        {
            return JsonNode.Parse(Runtime.DescribeContentJson())!["creation"]!["revision"]!.GetValue<string>();
        }
        CreateContentAssetResult Create(string Type, string Name, string? Expected = null, string Folder = "CreationValidation")
        {
            return Runtime.CreateContentAsset(new(1, Type, Folder, Name, Expected ?? Revision()));
        }
        Check(JsonNode.Parse(Runtime.DescribeContentJson())!["creation"]!["types"]!.AsArray()
            .Any(Type => Type!["type"]!.GetValue<string>() == "validation.creation-data"),
            "Creation discovery includes the current registered DataAsset types");
        var FolderResult = Create("Folder", "Nested");
        Check(FolderResult.Success && Directory.Exists(Path.Combine(DirectoryPath, "Nested")),
            "Folder creation uses the requested project Content directory");
        var StaleRevision = Revision();
        foreach (var Type in new[] { "World", "Prefab", "Material", "RmlDocument", "StyleSheet", "InputAction", "InputMappingContext", "validation.creation-data" })
        {
            var Name = Type.Replace('.', '_');
            var Result = Create(Type, Name);
            Check(Result.Success && Result.AssetId is not null && File.Exists(Result.Path),
                $"Creation saves and registers {Type}: {Result}");
            var Asset = Runtime.Assets!.Find(Result.AssetId)!;
            Check(Asset.Source == AssetSource.Project && Asset.Type == Type &&
                Path.GetDirectoryName(Result.Path) == DirectoryPath && Result.BeforeRevision != Result.AfterRevision &&
                Result.AfterRevision == Revision() && Result.TransactionId is not null,
                $"{Type} creation reports a committed manifest revision and stable identity");
            var Source = File.ReadAllText(Result.Path!);
            if (Type is "World" or "Prefab")
            {
                var World = WorldDocument.Load(Result.Path!, Runtime.Components!);
                Check(World.Data.IsPrefab == (Type == "Prefab") && World.Name == Name,
                    "New World/Prefab sources round-trip through the strict current reader");
            }
            else if (Type == "Material")
            {
                Check(AssetContent.LoadMaterial(Runtime.Assets, Asset.Id).Roughness == 0.5f,
                    "New materials use the runtime material reader");
            }
            else if (Type == "InputAction")
            {
                Check(InputRuntimeService.ParseActionAsset(Source, Asset).Id == Asset.Id,
                    "Created input action identity satisfies its strict parser");
            }
            else if (Type == "InputMappingContext")
            {
                Check(InputRuntimeService.ParseMappingContextAsset(Source, Asset, []).Bindings.Count == 0,
                    "New input contexts have an explicit empty binding set");
            }
            else if (Type == "RmlDocument")
            {
                Check(XDocument.Parse(Source).Root!.Name == "rml", "New UI document is valid RML text");
            }
            else if (Type == "validation.creation-data")
            {
                var Loaded = Runtime.RegisteredDataAssetTypes.Load<CreationValidationData>(Runtime.Assets, Asset.Id);
                Check(Loaded.Count == 7 && Loaded.Name == Name, "DataAsset creation serializes and validates the concrete type's defaults");
            }
        }
        var ManifestBefore = File.ReadAllText(ManifestPath);
        Check(Create("World", "Stale", StaleRevision).Code == "CONTENT_CREATE_CONFLICT" &&
            !File.Exists(Path.Combine(DirectoryPath, "Stale.sworld")), "A stale creation request cannot write a source file");
        Check(Create("Material", "Material").Code == "CONTENT_CREATE_EXISTS" &&
            File.ReadAllText(ManifestPath) == ManifestBefore, "Duplicate names never overwrite assets or their manifest entries");
        foreach (var Name in new[] { "../Outside", "CON", "Bad.", " ", "Other/Name" })
        {
            Check(!Create("World", Name).Success, "Creation rejects invalid names: " + Name);
        }
        Check(!Create("World", "Escape", Folder: "../").Success &&
            !Create("World", "Plugin", Folder: "Plugins/ValidationPlugin/Content").Success,
            "Creation rejects escaped and Plugin-owned folders");
        Check(!Create("UnknownType", "Unknown").Success, "Creation rejects unregistered types");
        Check(!Create("Validation.Reload.Rifle", "InvalidDefaults").Success &&
            !File.Exists(Path.Combine(DirectoryPath, "InvalidDefaults.sdataasset")),
            "Invalid DataAsset defaults are rejected before writing a source file");
        var McpReply = JsonNode.Parse(McpEndpoint.ProcessLine(Runtime, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 901,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "content.create",
                ["arguments"] = new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["type"] = "Folder",
                    ["folder"] = "CreationValidation",
                    ["name"] = "McpFolder",
                    ["expectedRevision"] = Revision(),
                },
            },
        }.ToJsonString())!)!;
        Check(McpReply["result"]?["structuredContent"]?["success"]?.GetValue<bool>() == true &&
            Directory.Exists(Path.Combine(DirectoryPath, "McpFolder")),
            "MCP content.create uses the same committed creation workflow");
        using (var Locked = new FileStream(ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check(!Create("Material", "Rollback").Success &&
                !File.Exists(Path.Combine(DirectoryPath, "Rollback.smaterial")),
                "A failed manifest commit rolls back the newly created source file");
        }
        Check(File.ReadAllText(ManifestPath) == ManifestBefore &&
            !Directory.EnumerateFiles(Path.GetDirectoryName(ManifestPath)!, "*.tmp").Any(),
            "Failure preserves the manifest and removes temporary files");
        Check(Runtime.World.Serialize() == WorldBefore && Runtime.DocumentRevision == RevisionBefore &&
            File.ReadAllText(Runtime.ProjectManifest!) == ProjectManifest,
            "Creating content does not replace the active World, dirty Authoring, or change startup configuration");
    }
}
