# Mesh assets and rendering

SharpEngine ships formal, engine-readable JSON assets in `Content`. `StaticMeshAsset`,
`SkeletonAsset`, and `SkeletalMeshAsset` contain the stable asset ID, geometry or
bone data, and the material references consumed by the runtime. `SkeletalAnimationAsset`
contains animation tracks and is referenced by `SkeletalMeshComponent` at runtime.

New source asset types use a lowercase `s` prefix in their resource suffix: the
semantic type is lowercased and prefixed with `s`, for example `.sskeleton`,
`.sskeletalmesh`, `.sskeletalanimation`, `.sprefab`, and `.sworld`. Existing bare
or `.sharp...` files remain legacy compatibility inputs and are not renamed
without an explicit migration that preserves asset IDs and old references. This
file-suffix rule does not rename manifest type values, importer names, or C# asset
types.

Blender files and FBX files are authoring inputs, not delivery assets. They must stay
outside `Content` (the validation scripts use `Saved/Validation/.../Sources`), and the
asset manifest registers the imported SharpEngine assets. `sourcePath` and
`sourceSha256` are optional provenance fields. When present, the loader verifies
that the source file still exists and has the recorded SHA-256; these fields do
not require an FBX export to preserve the source file's bytes.

FBX import and export use semantic equivalence. A static mesh preserves its
triangle geometry, coordinate system, and units. A skeletal mesh additionally
preserves the skeleton hierarchy, bind pose, vertex influences, and skinning
weights. A skeletal animation preserves its duration, sample rate, bone order,
and transform tracks. Exporters may rewrite FBX records, ordering, compression,
and metadata. Metadata that is not represented by a SharpEngine asset is not a
round-trip guarantee. `AssetContent.ExportFbx`, `StaticMeshAsset.ExportFbx`,
`SkeletonAsset.ExportFbx`, `SkeletalMeshAsset.ExportFbx`, and
`SkeletalAnimationAsset.ExportFbx` are the public semantic export names;
`ExportOriginalFbx` remains only as an obsolete source-copy entry.
The built-in runtime path copies a verified raw FBX source only. For
canonical engine assets, `ExportFbx` currently reports that FBX reconstruction
is unsupported and leaves the destination unchanged. After an FBX writer is
installed, it must reconstruct a new file from the current canonical asset data.
完整的字段范围、组合选择和回读容差见 [FBX 导入导出规范](FbxInterchange.md)。

The built-in components are:

- `engine.static-mesh`: `StaticMeshAsset`, optional `MaterialAsset`, and color.
- `engine.skeletal-mesh`: `SkeletalMeshAsset`, optional material and
  `SkeletalAnimationAsset`, animation time, loop flag, and color.
- `engine.mesh-renderer`: the existing legacy mesh component, retained for
  existing worlds and OBJ/`Mesh` assets.

Both new mesh components require one `engine.transform`, have stable type IDs,
schema version 1, and serialize through the same SceneData and RuntimeWorld
contracts as the existing components. `SceneRenderer` renders and picks all
three mesh component types. Skeletal meshes are rendered in bind pose or with
the selected animation sample; skinning data and pose matrices are rebuilt
from serialized asset/component state.

The built-in primitive set includes formal `Mesh` assets for `Cube`, `Plane`,
`Capsule`, and `Sphere`. Cube is a one-unit centred box. Capsule has radius
0.5 and total height 2. Sphere has radius 0.5. All use Y-up coordinates and
carry indexed triangle geometry directly in `Content/Assets/Meshes`.

Content Manager opens Mesh, StaticMesh, SkeletalMesh, Skeleton, and Material in
dedicated top-level asset editors. Mesh previews render the real indexed geometry;
Skeleton previews use the explicit `previewMeshId` when available and otherwise
draw the bind hierarchy. Imported geometry, indices, bone topology, and skinning
weights are read-only. A writable project Mesh/SkeletalMesh may change only its
`defaultMaterial`; a writable Skeleton may change only `previewMeshId`. Material
schema v1 fields (`shader`, `baseColor`, `metallic`, and `roughness`) are fully
authorable. Every accepted edit validates the complete source document and joins
the editor-local revision and Undo/Redo history before explicit atomic Save.

To rebuild the Cube import validation evidence with Blender 5.2:

```powershell
& $Blender --background --python-exit-code 1 --python Tools/Assets/BuildEngineCube.py
```

The script writes its temporary `.blend` and `.fbx` files under
`Saved/Validation/EngineCube/Sources`, reopens the Blender file, imports the
exported FBX, and writes only the formal Cube JSON to
`Content/Assets/Meshes/Cube.mesh.json`. Readback evidence is written to
`Saved/Validation/EngineCube/readback.json`; neither source format is registered
or copied into `Content`.

To import another static FBX, keep the input outside `Content` and run Blender
with `Tools/Assets/ImportStaticFbx.py -- --input <fbx> --source-root <root>
--output <mesh.json> --asset-id <stable-id> --name <name>`. The importer emits a
self-contained formal JSON asset by default. Add `--include-source-provenance`
when a private authoring check needs source hash validation;
`--include-source-contract` remains a compatibility spelling. Do not commit the
authoring source or register it as a delivery asset.
