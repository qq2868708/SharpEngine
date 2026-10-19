# Data assets

SharpEngine data assets are versioned text resources defined by C# data types.
They use a stable asset ID and a stable type ID, independently of their file
path and CLR type name. New source files use the `.sdataasset` suffix.

```csharp
public abstract class WeaponData : DataAsset
{
    [DataProperty(Minimum = 0)]
    public double Damage { get; init; } = 20;
}

[DataAssetType("Game.Rifle", SchemaVersion = 1)]
public class RifleData : WeaponData
{
    public int MagazineSize { get; init; } = 30;
    public AssetRef<StaticMeshAsset> Mesh { get; init; }
}
```

Register each concrete definition with `DataAssetTypeRegistry.Register<RifleData>()` or
discover definitions from a project assembly with `DataAssetTypeRegistry.Discover`.
When an editor module calls `EditorRuntime.RegisterComponentModule`, annotated
data-asset definitions in that assembly are also registered and published in the
`assets.describe` `dataAssetTypes` list for Inspector and AI discovery.
The descriptor exposes the stable type ID, schema version, serialized properties
and a JSON Schema for tooling. Public readable/writable properties are serialized;
`JsonIgnore` excludes a property and `JsonPropertyName` preserves a chosen field ID.
`sealed` is optional. An abstract data-asset base can hold shared fields and
behavior, while each concrete derived type declares its own `DataAssetType` ID.
Inherited public properties are included in the derived type's schema and JSON.
Create a concrete derived value with `DataAssetFactory.Create<RifleData>` and
load it through a base contract with `DataAssetTypeRegistry.Load<WeaponData>`.

Each source resource has a `schemaVersion`, `kind`, `assetId`, and `name` envelope.
The remaining fields are the type's data properties. The asset manifest's `type`
must equal the declared stable type ID, for example `Game.Rifle`.

```json
{
  "schemaVersion": 1,
  "kind": "Game.Rifle",
  "assetId": "project://weapons/rifle",
  "name": "Rifle",
  "damage": 35,
  "mesh": "project://meshes/rifle"
}
```

Use `DataAssetSerializer.Load<RifleData>(Registry, AssetId)` for a known concrete
type, or register concrete types and use
`DataAssetTypeRegistry.Load<WeaponData>(Registry, AssetId)` when the manifest
type selects a derived asset through a base contract.
Loading checks the manifest type, serialized ID, schema version, fields, declared
constraints and direct asset references. Unknown fields and unsupported versions
are rejected. `Serialize` writes every declared property, including defaults.
`Save` validates first and replaces the registered file through a temporary file;
read-only assets cannot be saved. Asset references serialize as stable ID strings.

This first-stage API supplies type definitions, schemas, loading and explicit
source-file persistence. Content creation menus, a generic Inspector document,
Undo/Redo transactions, migration chains, recursive dependency discovery and a
full Cook pipeline are not provided by this API yet. When the Editor applies a
project Runtime Module reload, it discovers the concrete data-asset types again
and atomically replaces the editor registry; a failed reload restores the prior
registry. A source save is not an editor transaction, and these helpers must not
be used to bypass an editor-owned authoritative document. Runtime consumers
should keep mutable instance state separate from shared configuration assets.
