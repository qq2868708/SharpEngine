using System.Text.Json;
using SharpEngine.Assets;

var FailureCount = 0;

void Check(bool Condition, string Message)
{
    if (!Condition)
    {
        Console.Error.WriteLine($"FAIL: {Message}");
        FailureCount++;
    }
    else
    {
        Console.WriteLine($"PASS: {Message}");
    }
}

var Root = Path.Combine(Path.GetTempPath(), "SharpEngineDataAssetValidation-" + Guid.NewGuid().ToString("N"));
var EngineRoot = Path.Combine(Root, "Engine");
var ProjectRoot = Path.Combine(Root, "Project");
try
{
    Directory.CreateDirectory(Path.Combine(EngineRoot, "Content", "Assets"));
    Directory.CreateDirectory(Path.Combine(ProjectRoot, "Content", "Assets", "Weapons"));
    File.WriteAllText(Path.Combine(EngineRoot, "engine.json"), """
{
  "schemaVersion": 2,
  "assets": { "manifest": "Content/Assets/assets.json" }
}
""");
    File.WriteAllText(Path.Combine(EngineRoot, "Content", "Assets", "assets.json"), """
{
  "schemaVersion": 1,
  "assets": []
}
""");
    File.WriteAllText(Path.Combine(ProjectRoot, "Project.sharpproject"), """
{
  "schemaVersion": 3,
  "assets": { "manifest": "Content/Assets/assets.json" }
}
""");
    File.WriteAllText(Path.Combine(ProjectRoot, "Content", "Assets", "assets.json"), """
{
  "schemaVersion": 1,
  "assets": [
    {
      "id": "weapons/rifle",
      "type": "Game.Weapon",
      "name": "Rifle",
      "path": "Content/Assets/Weapons/Rifle.sdataasset"
    }
  ]
}
""");

    var Registry = AssetRegistry.Load(EngineRoot, ProjectRoot);
    var Rifle = DataAssetFactory.Create<RifleData>("project://weapons/rifle", "Rifle");
    Rifle.Damage = 35;
    Rifle.AttackSpeed = 4;
    Rifle.MagazineSize = 30;
    DataAssetSerializer.Save(Registry, "project://weapons/rifle", Rifle);

    var Types = new DataAssetTypeRegistry();
    Types.Register<RifleData>();
    var Loaded = Types.Load<WeaponData>(Registry, "project://weapons/rifle");
    Check(Loaded is RifleData RifleAsset && RifleAsset.Damage == 35 &&
        RifleAsset.AttackSpeed == 4 && RifleAsset.MagazineSize == 30,
        "derived data asset round trips inherited and local values");
    Check(Loaded.AssetId == "project://weapons/rifle" && Loaded.Name == "Rifle",
        "asset envelope preserves stable ID and name");
    Check(DataAssetSchemaProbe(), "type registry discovers the annotated C# definition");
    Check(Types.TryGetAssignable<WeaponData>("Game.Weapon", out var WeaponDescriptor) &&
        WeaponDescriptor?.RuntimeType == typeof(RifleData),
        "type registry resolves a concrete type through its abstract base");
    Check(Types.Descriptors.Single().Schema()["properties"]?["damage"] is not null &&
        Types.Descriptors.Single().Schema()["properties"]?["magazineSize"] is not null,
        "type descriptor exposes JSON Schema for Inspector and AI tooling");

    var InvalidJson = DataAssetSerializer.Serialize(Rifle).Replace(
        "\"damage\": 35", "\"damage\": -1", StringComparison.Ordinal);
    try
    {
        DataAssetSerializer.Parse<RifleData>(InvalidJson, "project://weapons/rifle");
        Check(false, "range validation rejects invalid values");
    }
    catch (InvalidDataException)
    {
        Check(true, "range validation rejects invalid values");
    }
}
finally
{
    if (Directory.Exists(Root))
    {
        Directory.Delete(Root, true);
    }
}

return FailureCount == 0 ? 0 : 1;

bool DataAssetSchemaProbe()
{
    var Registry = new DataAssetTypeRegistry();
    Registry.Register<RifleData>();
    return Registry.TryGet("Game.Weapon", out var Descriptor) &&
        Descriptor is not null && Descriptor.SchemaVersion == 1 &&
        Descriptor.RuntimeType == typeof(RifleData) &&
        Descriptor.Properties.Any(Property => Property.JsonName == "damage") &&
        Descriptor.Properties.Any(Property => Property.JsonName == "magazineSize");
}

public abstract class WeaponData : DataAsset
{
    [DataProperty(Minimum = 0, Required = true)]
    public double Damage
    {
        get;
        set;
    }

    [DataProperty(Minimum = 0, Required = true)]
    public double AttackSpeed
    {
        get;
        set;
    }
}

[DataAssetType("Game.Weapon", SchemaVersion = 1)]
public class RifleData : WeaponData
{
    public int MagazineSize
    {
        get;
        set;
    }
}
