using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Components;

namespace SharpEngine.Assets;

public sealed record ContentAssetType(string Type, string Label, string Category,
    string Extension, string Importer)
{
    public string CommandId
    {
        get
        {
            return "content-create-" + Uri.EscapeDataString(Type);
        }
    }
}

public sealed record CreateContentAssetRequest(int SchemaVersion, string Type,
    string Folder, string Name, string ExpectedRevision);

public sealed record CreateContentAssetResult(int SchemaVersion, bool Success,
    string Code, string Message, string? AssetId = null, string? Path = null,
    string? TransactionId = null, string? BeforeRevision = null, string? AfterRevision = null);

/// <summary>Validated source-file and manifest creation, shared by UI and MCP.
/// This is an explicit disk operation, independent of World Undo and startup settings.</summary>
internal sealed class ContentAssetCreation(AssetRegistry Assets, string ManifestPath,
    ComponentRegistry Components, DataAssetTypeRegistry DataTypes)
{
    internal static IReadOnlyList<ContentAssetType> Types(DataAssetTypeRegistry DataTypes)
    {
        var Result = new List<ContentAssetType>
        {
            new("Folder", "New Folder", "Folder", "", ""),
            new("World", "World", "Basic Assets", ".sworld", "SharpEngine.WorldJson"),
            new("Prefab", "Prefab", "Basic Assets", ".sprefab", "SharpEngine.PrefabJson"),
            new("Material", "Material", "Basic Assets", ".smaterial", "SharpEngine.MaterialJson"),
            new("UiDocument", "UI Screen", "User Interface", ".sui", "SharpEngine.UiDocumentJson"),
            new("RmlDocument", "RML Document", "User Interface", ".rml", "RmlUi.Document"),
            new("StyleSheet", "Style Sheet", "User Interface", ".rcss", "RmlUi.StyleSheet"),
            new("GlobalStyleSheet", "Shared UI Style", "User Interface", ".sglobalcss", "RmlUi.GlobalStyleSheet"),
            new("InputAction", "Input Action", "Input", ".sinputaction", "SharpEngine.InputActionJson"),
            new("InputMappingContext", "Input Mapping Context", "Input", ".sinputmappingcontext", "SharpEngine.InputMappingContextJson"),
        };
        Result.AddRange(DataTypes.Descriptors.OrderBy(Descriptor => Descriptor.TypeId, StringComparer.Ordinal)
            .Select(Descriptor => new ContentAssetType(Descriptor.TypeId, Descriptor.TypeId,
                "Data Assets", ".sdataasset", "SharpEngine.DataAssetJson")));
        return Result;
    }

    internal string Revision() { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ManifestPath))); }

    internal string FolderPath(string Folder)
    {
        var Root = Assets.ProjectRoot ?? throw new InvalidOperationException("Open a project before creating assets");
        if (Path.IsPathRooted(Folder) || Folder.Contains('\\') ||
            Folder.Split('/').Any(Part => Part is "." or "..") ||
            Folder.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase) ||
            Folder.Equals("Plugins", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose a project Content folder; engine and Plugin content are read-only creation targets");
        }
        var Content = Path.GetFullPath(Path.Combine(Root, "Content"));
        var Target = Path.GetFullPath(Path.Combine(Content, Folder));
        if (Target != Content && !Target.StartsWith(Content + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The target folder must remain inside project Content");
        }
        for (var Directory = new DirectoryInfo(Target); Directory is not null; Directory = Directory.Parent)
        {
            if (!Directory.Exists || (Directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The target must be an existing project folder without redirected ancestors");
            }
            if (Directory.FullName.Equals(Root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
        return Target;
    }

    internal CreateContentAssetResult Create(CreateContentAssetRequest Request)
    {
        var TransactionId = Guid.NewGuid().ToString("N");
        string? Before = null;
        try
        {
            if (Request.SchemaVersion != 1)
            {
                return new(1, false, "CONTENT_CREATE_SCHEMA", "Only creation schemaVersion 1 is supported");
            }
            var Definition = Types(DataTypes).SingleOrDefault(Type => Type.Type == Request.Type);
            if (Definition is null)
            {
                return new(1, false, "CONTENT_CREATE_TYPE", "The requested asset type is not registered");
            }
            var Directory = FolderPath(Request.Folder);
            ValidateName(Request.Name);
            Before = Revision();
            if (Before != Request.ExpectedRevision)
            {
                return new(1, false, "CONTENT_CREATE_CONFLICT", "The asset manifest changed; reopen the creation dialog or refresh content.describe",
                    BeforeRevision: Before, AfterRevision: Before);
            }
            var Target = Path.Combine(Directory, Request.Name + Definition.Extension);
            if (File.Exists(Target) || System.IO.Directory.Exists(Target))
            {
                return new(1, false, "CONTENT_CREATE_EXISTS", "A file or folder with this name already exists");
            }
            if (Definition.Type == "Folder")
            {
                System.IO.Directory.CreateDirectory(Target);
                return new(1, true, "CONTENT_CREATED", "Created " + Request.Name, Path: Target,
                    TransactionId: TransactionId, BeforeRevision: Before, AfterRevision: Before);
            }
            var BareId = Definition.Type switch
            {
                "InputAction" => "input/actions/" + Guid.NewGuid().ToString("N"),
                "InputMappingContext" => "input/contexts/" + Guid.NewGuid().ToString("N"),
                _ => "created/" + Guid.NewGuid().ToString("N"),
            };
            var Id = "project://" + BareId;
            var Source = SourceText(Definition.Type, Request.Name, Id);
            var Original = File.ReadAllText(ManifestPath);
            var Manifest = JsonNode.Parse(Original, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject ?? throw new InvalidDataException("The project asset manifest must be an object");
            if (Manifest["schemaVersion"]?.GetValue<int>() != AssetRegistry.ManifestSchemaVersion || Manifest["assets"] is not JsonArray)
            {
                throw new InvalidDataException("The project asset manifest must use schemaVersion 2 and contain assets");
            }
            var Entries = Manifest["assets"]!.AsArray();
            var RelativePath = Path.GetRelativePath(Path.Combine(Assets.ProjectRoot!, "Content"), Target).Replace('\\', '/');
            if (Entries.Any(Entry => string.Equals(Entry?["path"]?.GetValue<string>(), RelativePath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("This source path is already registered in the manifest");
            }
            Entries.Add(new JsonObject
            {
                ["id"] = BareId,
                ["type"] = Definition.Type,
                ["name"] = Request.Name,
                ["path"] = RelativePath,
                ["importer"] = Definition.Importer,
                ["tags"] = new JsonArray(),
            });
            var Staged = ManifestPath + "." + TransactionId + ".tmp";
            var ManifestText = Manifest.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            var After = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ManifestText)));
            var CreatedSource = false;
            try
            {
                File.WriteAllText(Staged, ManifestText, new UTF8Encoding(false));
                using (var Stream = new FileStream(Target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var Writer = new StreamWriter(Stream, new UTF8Encoding(false)))
                {
                    CreatedSource = true;
                    Writer.Write(Source);
                }
                if (Revision() != Before)
                {
                    throw new InvalidDataException("The asset manifest changed before commit; no asset was registered");
                }
                File.Move(Staged, ManifestPath, true);
            }
            catch
            {
                if (CreatedSource)
                {
                    File.Delete(Target);
                }
                throw;
            }
            finally
            {
                if (File.Exists(Staged))
                {
                    File.Delete(Staged);
                }
            }
            return new(1, true, "CONTENT_CREATED", "Created " + Request.Name, Id, Target,
                TransactionId, Before, After);
        }
        catch (Exception Error) when (Error is InvalidDataException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or JsonException or System.Reflection.TargetInvocationException)
        {
            return new(1, false, "CONTENT_CREATE_INVALID", Error.GetBaseException().Message,
                TransactionId: TransactionId, BeforeRevision: Before, AfterRevision: Before);
        }
    }

    private static void ValidateName(string Name)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException("Enter a file name");
        }
        var Stem = Name.Split('.')[0];
        string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (string.IsNullOrWhiteSpace(Name) || Name != Name.Trim() || Name.EndsWith('.') ||
            Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Reserved.Contains(Stem, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Enter a valid file name without a path or file extension");
        }
    }

    private string SourceText(string Type, string Name, string Id)
    {
        if (Type is "World" or "Prefab")
        {
            var Scene = new SceneData
            {
                Name = Name,
                IsPrefab = Type == "Prefab",
            };
            if (Scene.IsPrefab)
            {
                Scene.CreateEntity(Components, "Root");
            }
            return Scene.Serialize();
        }
        if (Type == "UiDocument")
        {
            var Title = System.Net.WebUtility.HtmlEncode(Name);
            var Rml = $"<template values=\"{{}}\"><div id=\"root\" style=\"display:block;width:100%;height:100%;background-color:#20252c;color:#e0e6ef;font-family:Segoe UI;\"><span id=\"title\">{Title}</span></div></template>";
            return UiDocumentAsset.Create(Name, Rml).Serialize() + Environment.NewLine;
        }
        if (Type == "RmlDocument")
        {
            var Title = System.Net.WebUtility.HtmlEncode(Name);
            return $"<rml><head><title>{Title}</title><style>body {{ background-color: #20252c; color: #e0e6ef; font-family: Segoe UI; }}</style></head><body><div id=\"root\"></div></body></rml>\n";
        }
        if (Type == "GlobalStyleSheet")
        {
            return ".shared-control\n{\n    color: #e0e6ef;\n}\n";
        }
        if (Type == "StyleSheet")
        {
            return "body\n{\n    color: #e0e6ef;\n}\n";
        }
        JsonObject? Root = Type switch
        {
            "Material" => new JsonObject
            {
                ["schemaVersion"] = 1,
                ["name"] = Name,
                ["shader"] = "Standard",
                ["baseColor"] = new JsonArray(0.8f, 0.8f, 0.8f, 1.0f),
                ["metallic"] = 0,
                ["roughness"] = 0.5f,
            },
            "InputAction" => new JsonObject
            {
                ["schemaVersion"] = 1,
                ["actionId"] = Id,
                ["valueType"] = "Button",
                ["continuous"] = false,
            },
            "InputMappingContext" => new JsonObject
            {
                ["schemaVersion"] = 1,
                ["contextId"] = Id,
                ["route"] = "Gameplay",
                ["priority"] = 0,
                ["activateByDefault"] = false,
                ["bindings"] = new JsonArray(),
            },
            _ => null,
        };
        if (Root is not null)
        {
            return Root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            });
        }
        var Registered = DataTypes.TryGet(Type, out var Descriptor);
        if (!Registered || Descriptor is null)
        {
            throw new InvalidDataException("The data asset type is no longer registered");
        }
        var Instance = (DataAsset)Activator.CreateInstance(Descriptor.RuntimeType)!;
        return DataAssetSerializer.Serialize(Instance, Id, Name, Registry: Assets);
    }
}
