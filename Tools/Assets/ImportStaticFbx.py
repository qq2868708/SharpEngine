"""Run with Blender --background --python this_file -- --help.

Read an authoring FBX, then emit SharpEngine's canonical static-mesh JSON.
Source files belong in a local authoring or validation directory; they are not
written to Content. The optional sourcePath/sourceSha256 fields record import
provenance for validation. They do not promise byte-for-byte FBX export:
exported FBX only needs to be semantically equivalent to the canonical asset.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys

import bpy


def import_static_fbx(source, root, destination, asset_id, name, material,
                      include_source_provenance=False, primitive='Imported FBX',
                      include_source_contract=None):
    # Keep the old Python keyword as a compatibility alias while exposing the
    # semantic provenance name in new import scripts.
    if include_source_contract is not None:
        include_source_provenance = include_source_contract
    source, root, destination = Path(source).resolve(), Path(root).resolve(), Path(destination).resolve()
    relative = source.relative_to(root).as_posix()
    if source.suffix.lower() != '.fbx':
        raise ValueError('The source must be an FBX inside the owning asset root')
    original_hash = hashlib.sha256(source.read_bytes()).hexdigest().upper()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=str(source), use_anim=False, global_scale=1.0)
    objects = sorted((obj for obj in bpy.context.scene.objects if obj.type == 'MESH'), key=lambda obj: obj.name)
    if not objects or any(obj.type == 'ARMATURE' for obj in bpy.context.scene.objects):
        raise ValueError('A static FBX must contain meshes and no armature')
    positions, indices = [], []
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for obj in objects:
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        try:
            mesh.calc_loop_triangles()
            offset = len(positions)
            for vertex in mesh.vertices:
                point = evaluated.matrix_world @ vertex.co
                # Blender Z-up -> SharpEngine Y-up; right-handed, -Z forward.
                positions.append([round(float(value), 7) for value in (point.x, point.z, -point.y)])
            for triangle in mesh.loop_triangles:
                vertices = list(triangle.vertices)
                if evaluated.matrix_world.determinant() < 0:
                    vertices.reverse()
                indices.extend(offset + index for index in vertices)
        finally:
            evaluated.to_mesh_clear()
    if len(positions) < 3 or not indices or any(not math.isfinite(value) for point in positions for value in point):
        raise ValueError('FBX contains no valid finite triangle geometry')
    if hashlib.sha256(source.read_bytes()).hexdigest().upper() != original_hash:
        raise ValueError('FBX changed during import')
    payload = dict(schemaVersion=1, kind='static-mesh', assetId=asset_id, name=name,
                   primitive=primitive, coordinateSystem='Y-up', defaultMaterial=material,
                   positions=positions, indices=indices)
    if include_source_provenance:
        payload['sourcePath'] = relative
        payload['sourceSha256'] = original_hash
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + '.tmp')
    temporary.write_text(json.dumps(payload, indent=2) + '\n', encoding='utf-8')
    temporary.replace(destination)
    print(f'[StaticFbxImport] {asset_id}: vertices={len(positions)}, triangles={len(indices)//3}, sha256={original_hash}')
    return payload


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', required=True)
    parser.add_argument('--source-root', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--asset-id', required=True)
    parser.add_argument('--name', required=True)
    parser.add_argument('--material', default='engine://materials/default')
    parser.add_argument('--include-source-provenance', '--include-source-contract',
                        dest='include_source_provenance', action='store_true',
                        help='Record sourcePath/sourceSha256 provenance fields')
    parser.add_argument('--primitive', default='Imported FBX')
    options = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    import_static_fbx(options.input, options.source_root, options.output, options.asset_id,
                      options.name, options.material, options.include_source_provenance,
                      options.primitive)
