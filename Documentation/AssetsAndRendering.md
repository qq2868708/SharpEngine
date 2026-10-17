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
