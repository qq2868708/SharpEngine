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
| UI screen | `.sui` | `.rml` (bare RML, still readable) |
| Shared UI stylesheet | `.sglobalcss` | `.rcss` (legacy StyleSheet, still readable) |

`.sui` is a versioned JSON package whose `rml` member holds the authoritative RML
document. One screen is one asset with one identity and one atomic save, so a
package must not also register its inline RML as a second asset. A bare `.rml`
file remains a valid `RmlDocument` source and keeps working as a legacy
single-document asset; `.rcss` stays a `StyleSheet` text format. Neither text
format is being renamed.

New project UI private RCSS belongs in the `.sui` package's `rcss` text member.
Only confirmed cross-UI rules become a `GlobalStyleSheet` asset with suffix
`.sglobalcss` and importer `RmlUi.GlobalStyleSheet`. UI packages list stable IDs
in `globalStyleSheets`; registration never implies automatic global injection.
Preserve the existing asset ID when migrating an external shared rule file.

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
