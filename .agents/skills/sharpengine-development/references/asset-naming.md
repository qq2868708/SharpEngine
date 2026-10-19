# Asset File Naming Contract

Use this reference when adding a new source asset type, importer output, or
asset migration.

## New suffixes

The resource type suffix is lowercase and starts with the literal `s` prefix.
The suffix is the part that identifies the source asset format, whether it is a
standalone extension or the type portion of a compound text extension.

| Resource kind | New source suffix | Legacy examples |
| --- | --- | --- |
| World | `.sworld` | none |
| Prefab | `.sprefab` | none |
| Mesh | `.smesh` | `.mesh.json` |
| Static mesh | `.sstaticmesh` | `.staticmesh.json` |
| Skeleton | `.sskeleton` | `.skeleton.json`, `.sharpskeleton` |
| Skeletal mesh | `.sskeletalmesh` | `.skeletalmesh.json`, `.sharpskeletalmesh` |
| Skeletal animation | `.sskeletalanimation` | `.skeletalanimation.json`, `.sharpskeletalanimation` |

For a JSON-backed source file, keep the `s`-prefixed resource suffix in the
compound name, for example `Character.sskeletalmesh.json`. Do not use
`Character.sharpskeletalmesh` for a newly introduced type.

## Contract boundaries

The new suffix is the lowercase semantic resource type prefixed with `s`. The
suffix is a file naming contract and is separate from:

- the manifest `type` value, which stays a semantic PascalCase value such as
  `SkeletalMesh`;
- the stable `assetId`, which is an identity and must not be derived from a
  file name guess;
- importer names, which may retain the `SharpEngine.*` engine namespace; and
- C# runtime type names such as `SkeletalMeshAsset`.

Do not rename those contracts merely to make a file suffix look consistent.

## Legacy migration

Treat an existing `.sharp...` suffix as a readable compatibility alias. Before
changing it, trace manifest registration, editor discovery, save/load, hot
reload, Cook dependency discovery, and every serialized reference. A migration
must:

1. register the new `s`-prefixed suffix;
2. continue reading the old suffix for the supported compatibility window;
3. preserve the stable asset ID and update references explicitly;
4. write only the new suffix for newly saved assets; and
5. add diagnostics and manual/API documentation for the old and new forms.

Do not silently rename or delete old files. If a migration is not part of the
request, leave existing files and manifest entries unchanged, regardless of
whether the old suffix was bare or used the `sharp` prefix.
