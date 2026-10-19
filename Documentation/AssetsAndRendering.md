# Mesh assets and rendering

SharpEngine keeps imported model source separate from the data used by the
runtime renderer. `StaticMeshAsset`, `SkeletonAsset`, and `SkeletalMeshAsset`
are versioned JSON descriptions containing the stable asset ID, geometry or
bone data, and import provenance. `SkeletalAnimationAsset` contains animation
tracks and is referenced by `SkeletalMeshComponent` at runtime.

For an FBX import, `sourcePath` is a project or engine relative path and
`sourceSha256` is the SHA-256 of the original bytes. The FBX is authoritative;
the JSON mesh, skeleton, animation and runtime caches are derived data. The
runtime never reconstructs an FBX from triangles or bones.

`AssetContent.ExportOriginalFbx(registry, assetId, destination)` resolves the
source path, verifies that the current source hash still matches the imported
hash, and copies the source bytes unchanged. It rejects missing sources,
changed sources, path traversal, invalid hashes, and assets without a source
contract. This is an exact source export, rather than a re-encoded or
approximate export.

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

The sample `Robot` assets retain one fixture FBX and use its hash in the static
mesh, skeleton, and skeletal mesh JSON. Validation covers deterministic asset
round trips, exact FBX byte export, source hash mismatch rejection, component
registration, World JSON round trip, RuntimeWorld reconstruction, and viewport
rendering/picking.

The engine Cube retains `engine://meshes/cube` and the `Mesh` descriptor type
for existing MeshRenderer references. Its canonical static-mesh JSON is derived
by importing the actual `Content/Assets/Sources/Cube.fbx`; both mesh loading
paths verify `sourcePath` and `sourceSha256`. `Cube.blend` is the editable DCC
source, and the FBX is the authoritative exchange source for engine import.
Both are registered as read-only engine assets.

Rebuild with Blender 5.2:

```powershell
& $Blender --background --python-exit-code 1 --python Tools/Assets/BuildEngineCube.py
```

The cube is centred at the origin, one metre (one engine unit) on every side,
with identity object transforms. Blender uses Z-up; FBX export uses Y-up and
-Z forward. The importer converts Blender's FBX readback to right-handed Y-up
engine coordinates and bakes mesh transforms. The source FBX contains outward
triangles, flat face normals, UVs and one Default material slot. The current
runtime JSON schema consumes positions, indices and the default material;
UVs and source normals remain in Blender/FBX, and are not yet runtime vertex
attributes. Rebuilding checks the saved Blender file, imports the exported FBX,
and writes readback evidence to `Saved/Validation/EngineCube/readback.json`.

To import another static FBX already inside its owning asset root, run Blender
with `Tools/Assets/ImportStaticFbx.py -- --input <fbx> --source-root <root>
--output <mesh.json> --asset-id <stable-id> --name <name>`. Register its output
and source in the owning manifest explicitly. Reimporting unchanged FBX bytes
produces the same JSON; regenerating the FBX can change its source hash.
