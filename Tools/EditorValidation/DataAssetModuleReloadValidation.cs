using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor;

internal static class DataAssetModuleReloadValidation
{
    internal static void Run(EditorRuntime Runtime, Action<bool, string> Require)
    {
        var Before = Runtime.DescribeAssetsJson();
        var Modules = new[]
        {
            (typeof(ReloadRifleData).Assembly, "validation.data-assets", "Runtime", "generation-1"),
        };
        using (var Preparation = Runtime.PrepareModuleReload(Modules))
        {
            Require(Runtime.DescribeAssetsJson() == Before,
                "Preparing project data-asset types leaves the committed asset snapshot intact");
            Preparation.Commit();
            var SnapshotJson = Runtime.DescribeAssetsJson();
            var Snapshot = JsonNode.Parse(SnapshotJson)!.AsObject();
            var Descriptor = Snapshot["dataAssetTypes"]!.AsArray().Single(Type =>
                Type?["typeId"]?.GetValue<string>() == "Validation.Reload.Rifle")!;
            Require(Descriptor["schema"]?["properties"]?["damage"]?["minimum"]?.GetValue<double>() == 0 &&
                Descriptor["schema"]?["properties"]?["magazineSize"]?["minimum"]?.GetValue<double>() == 1,
                "Committed runtime-module discovery publishes inherited and derived data-asset constraints");

            var ToolReply = JsonNode.Parse(McpEndpoint.ProcessLine(Runtime, """
                {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"assets.describe","arguments":{}}}
                """)!)!;
            Require(ToolReply["error"] is null &&
                ToolReply["result"]?["structuredContent"]?["dataAssetTypes"]?.AsArray().Any(Type =>
                    Type?["typeId"]?.GetValue<string>() == "Validation.Reload.Rifle") == true,
                "assets.describe returns project data-asset schemas through MCP");
            var ResourceReply = JsonNode.Parse(McpEndpoint.ProcessLine(Runtime, """
                {"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"sharpengine://assets"}}
                """)!)!;
            var ResourceText = ResourceReply["result"]?["contents"]?[0]?["text"]?.GetValue<string>();
            Require(ResourceReply["error"] is null && ResourceText == Runtime.DescribeAssetsJson(),
                "The asset MCP resource exposes the same committed snapshot as assets.describe");

            Require(JsonNode.Parse(Runtime.DescribeAssetsJson())!["dataAssetTypes"]!.AsArray().Count == 1,
                "The committed reload keeps its data-asset registry while its preparation is alive");
        }
        Require(Runtime.DescribeAssetsJson() == Before,
            "Disposing an unaccepted reload restores the previous data-asset registry");
        using (var Accepted = Runtime.PrepareModuleReload(Modules))
        {
            Accepted.Commit();
            Accepted.Accept();
        }
        Require(JsonNode.Parse(Runtime.DescribeAssetsJson())!["dataAssetTypes"]!.AsArray().Count == 1,
            "An accepted module reload retains its registered data-asset types");
    }
}

public abstract class ReloadWeaponData : DataAsset
{
    [DataProperty(Minimum = 0, Required = true)]
    public double Damage
    {
        get;
        set;
    }
}

[DataAssetType("Validation.Reload.Rifle", SchemaVersion = 1)]
public class ReloadRifleData : ReloadWeaponData
{
    [DataProperty(Minimum = 1, Required = true)]
    public int MagazineSize
    {
        get;
        set;
    }
}
