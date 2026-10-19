"""Blender --background --python Tools/Assets/BuildEngineCube.py.

Create temporary authoring artifacts outside Content, export FBX, reopen both
artifacts and import geometry from the FBX (never from the pre-export mesh).
Only the imported formal JSON asset is written to Content.
"""
import json
import hashlib
from pathlib import Path
import sys

import bpy
from mathutils import Vector

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
from ImportStaticFbx import import_static_fbx

ROOT = Path(__file__).resolve().parents[2]
VALIDATION = ROOT / 'Saved/Validation/EngineCube'
SOURCES = VALIDATION / 'Sources'
BLEND = SOURCES / 'Cube.blend'
FBX = SOURCES / 'Cube.fbx'
REPORT = ROOT / 'Saved/Validation/EngineCube/readback.json'
FORMAL_ASSET = ROOT / 'Content/Assets/Meshes/Cube.mesh.json'

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.context.preferences.filepaths.save_version = 0
bpy.context.scene.unit_settings.system = 'METRIC'
bpy.context.scene.unit_settings.scale_length = 1.0
bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0, 0, 0))
cube = bpy.context.object
cube.name = 'Cube'
cube.data.name = 'Cube'
material = bpy.data.materials.new('Default')
material.diffuse_color = (1, 1, 1, 1)
cube.data.materials.append(material)
for polygon in cube.data.polygons:
    polygon.use_smooth = False
cube['SharpEngine.AssetId'] = 'engine://meshes/cube'
cube['SharpEngine.CoordinateSystem'] = 'Y-up, right-handed, -Z forward; Blender Z-up'
cube['SharpEngine.Unit'] = '1 Blender metre = 1 engine unit'
SOURCES.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
bpy.ops.wm.open_mainfile(filepath=str(BLEND))
cube = bpy.data.objects['Cube']
assert tuple(cube.scale) == (1, 1, 1) and all(abs(value - 1) < 1e-6 for value in cube.dimensions)
assert tuple(cube.location) == (0, 0, 0) and tuple(cube.rotation_euler) == (0, 0, 0)
assert cube.data.uv_layers and len(cube.data.materials) == 1
bpy.ops.export_scene.fbx(filepath=str(FBX), use_selection=True, object_types={'MESH'},
                         axis_forward='-Z', axis_up='Y', global_scale=1.0,
                         apply_unit_scale=True, apply_scale_options='FBX_SCALE_UNITS',
                         use_mesh_modifiers=True, mesh_smooth_type='FACE',
                         use_triangles=True, bake_anim=False, add_leaf_bones=False)
payload = import_static_fbx(FBX, ROOT, FORMAL_ASSET,
                            'engine://meshes/cube', 'Cube', 'engine://materials/default',
                            primitive='Cube')
source_sha256 = hashlib.sha256(FBX.read_bytes()).hexdigest().upper()
assert len(payload['positions']) == 8 and len(payload['indices']) == 36
for axis in range(3):
    assert abs(min(point[axis] for point in payload['positions']) + 0.5) < 1e-5
    assert abs(max(point[axis] for point in payload['positions']) - 0.5) < 1e-5
for offset in range(0, 36, 3):
    a, b, c = (Vector(payload['positions'][index]) for index in payload['indices'][offset:offset + 3])
    assert (b - a).cross(c - a).dot((a + b + c) / 3) > 0
imported = next(obj for obj in bpy.context.scene.objects if obj.type == 'MESH')
assert imported.data.uv_layers and len(imported.data.materials) == 1
assert all(not polygon.use_smooth for polygon in imported.data.polygons)
report = dict(schemaVersion=1, assetId=payload['assetId'], blenderVersion=bpy.app.version_string,
              blendPath=BLEND.relative_to(ROOT).as_posix(), sourcePath=FBX.relative_to(ROOT).as_posix(),
              sourceSha256=source_sha256, positions=8, triangles=12,
              bounds=[[min(point[axis] for point in payload['positions']) for axis in range(3)],
                      [max(point[axis] for point in payload['positions']) for axis in range(3)]],
              boundsTolerance=1e-5, unit='metre', coordinateSystem='Y-up',
              uvLayers=len(imported.data.uv_layers), materialSlots=len(imported.data.materials),
              outwardWinding=True, flatShading=True, reopenedBlend=True, importedFromFbx=True)
REPORT.parent.mkdir(parents=True, exist_ok=True)
REPORT.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
print('[EngineCube] PASS:', json.dumps(report))
