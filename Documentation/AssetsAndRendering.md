# Mesh assets and rendering

SharpEngine ships formal, engine-readable JSON assets in `Content`. `StaticMeshAsset`,
`SkeletonAsset`, and `SkeletalMeshAsset` contain the stable asset ID, geometry or
bone data, and the material references consumed by the runtime. `SkeletalAnimationAsset`
contains animation tracks and is referenced by `SkeletalMeshComponent` at runtime.

Blender files and FBX files are authoring inputs, not delivery assets. They must stay
outside `Content` (the validation scripts use `Saved/Validation/.../Sources`), and the
asset manifest registers only the imported JSON assets. `sourcePath` and
`sourceSha256` remain optional compatibility fields for private import workflows;
the checked-in engine assets omit them and load without a source file.

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
self-contained formal JSON asset by default. Use `--include-source-contract` only
for a private authoring check; do not commit that source or register it in the
shipping manifest.
