using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;

static string Required(Dictionary<string, string> options, string name)
{
    if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        throw new ArgumentException($"Missing --{name}");
    return value;
}

var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
        throw new ArgumentException($"Expected --name value, got '{args[i]}'");
    options[args[i][2..]] = args[++i];
}

var projectRoot = Path.GetFullPath(Required(options, "project-root"));
var input = Path.GetFullPath(Required(options, "input"));
var name = options.TryGetValue("name", out var requestedName) && requestedName.Length > 0
    ? requestedName : Path.GetFileNameWithoutExtension(input);
var id = options.TryGetValue("id", out var requestedId) && requestedId.Length > 0
    ? requestedId.Replace('\\', '/') : $"meshes/{Slug(name)}";
if (id.StartsWith("project://", StringComparison.OrdinalIgnoreCase)) id = id[10..];

var manifestPath = Path.Combine(projectRoot, "Content", "Assets", "assets.json");
if (!File.Exists(manifestPath)) throw new FileNotFoundException("Project asset manifest was not found", manifestPath);
var root = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
    ?? throw new InvalidDataException("Project asset manifest is not a JSON object");
if (root["schemaVersion"]?.GetValue<int>() != AssetRegistry.ManifestSchemaVersion)
{
    throw new InvalidDataException("Asset manifest requires schemaVersion=2 and Content-relative paths");
}
var mesh = WavefrontObjImporter.Load(input, name);
var fileName = Slug(name) + ".mesh.json";
var RelativePath = $"Assets/Meshes/{fileName}";
var destination = Path.Combine(projectRoot, "Content", RelativePath.Replace('/', Path.DirectorySeparatorChar));
WavefrontObjImporter.WriteMeshJson(mesh, destination, Path.GetRelativePath(projectRoot, input).Replace('\\', '/'));

var assets = root["assets"]?.AsArray() ?? throw new InvalidDataException("Project asset manifest has no assets array");
foreach (var item in assets)
{
    if (item is JsonObject record && string.Equals(record["id"]?.GetValue<string>(), id,
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Asset id already exists: project://{id}");
}
assets.Add(new JsonObject
{
    ["id"] = id,
    ["type"] = "Mesh",
    ["name"] = name,
    ["path"] = RelativePath,
    ["importer"] = "SharpEngine.MeshJson",
    ["tags"] = new JsonArray("imported", "mesh"),
    ["metadata"] = new JsonObject
    {
        ["source"] = Path.GetRelativePath(projectRoot, input).Replace('\\', '/'),
        ["vertices"] = mesh.Positions.Count,
        ["triangles"] = mesh.Indices.Count / 3,
    },
});
File.WriteAllText(manifestPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"[MeshImport] imported {input}");
Console.WriteLine($"[MeshImport] asset=project://{id} path={destination} vertices={mesh.Positions.Count} triangles={mesh.Indices.Count / 3}");

static string Slug(string value)
{
    var chars = value.Trim().Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray();
    var result = new string(chars).Trim('-');
    return result.Length == 0 ? "mesh" : result.ToLowerInvariant();
}
