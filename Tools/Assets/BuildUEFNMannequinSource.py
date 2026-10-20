"""Create the editable Blender source from the original Epic FBX.

Run with Blender --background --factory-startup --python this_file.
This does not modify Runtime assets under Content.
"""
import hashlib
import json
from pathlib import Path
import shutil

import bpy
from mathutils import Matrix, Vector

Root = Path(__file__).resolve().parents[2]
Output = Root / 'Resources/SourceAssets/UEFNMannequin'
Source = Output / 'Original/skm_uefn_mannequin.FBX'
if not Source.exists():
    Source = Path('C:/ProgramData/Epic/EpicGamesLauncher/VaultCache/FabLibrary/UEFN_Mannequin-18699cc7/fbx/skm_uefn_mannequin.FBX')
ExpectedHash = '0E5769FDF6E0B703785E6C849669EF5D0F04614440BDAB492C79D989C9D45E95'
BlendPath = Output / 'UEFNMannequin_BindPose.blend'
if BlendPath.exists():
    raise FileExistsError(f'Preserve the existing source: {BlendPath}')
ActualHash = hashlib.sha256(Source.read_bytes()).hexdigest().upper()
if ActualHash != ExpectedHash:
    raise ValueError('Original FBX hash does not match the audited source')
Output.mkdir(parents=True, exist_ok=True)
(Output / 'Original').mkdir(exist_ok=True)
DestinationSource = Output / 'Original' / Source.name
if Source.resolve() != DestinationSource.resolve():
    shutil.copy2(Source, DestinationSource)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.context.preferences.filepaths.save_version = 0
Scene = bpy.context.scene
Scene.unit_settings.system = 'METRIC'
Scene.unit_settings.scale_length = 1.0
Scene.render.fps = 30
bpy.ops.import_scene.fbx(filepath=str(Source), use_anim=False,
    automatic_bone_orientation=False, use_prepost_rot=True)
Rig = next(Object for Object in bpy.data.objects if Object.type == 'ARMATURE')
Mesh = next(Object for Object in bpy.data.objects if Object.type == 'MESH')
BeforeBones = {}
for Bone in Rig.data.bones:
    Position, Rotation, Scale = (Rig.matrix_world @ Bone.matrix_local).decompose()
    BeforeBones[Bone.name] = Matrix.LocRotScale(Position, Rotation, Vector((1, 1, 1)))
BeforeVertices = [Mesh.matrix_world @ Vertex.co for Vertex in Mesh.data.vertices]
RigWorld = Rig.matrix_world.copy()
MeshWorld = Mesh.matrix_world.copy()

# Normalize authoring objects to metres without reinterpreting bone rotations.
# Transform both rest bones and mesh geometry by their actual object matrices.
Rig.parent = None
Rig.data.transform(RigWorld)
Rig.matrix_world = Matrix.Identity(4)
Mesh.data.transform(MeshWorld)
Mesh.parent = Rig
Mesh.matrix_parent_inverse = Matrix.Identity(4)
Mesh.matrix_world = Matrix.Identity(4)
Rig.name = 'UEFNMannequin_Rig'
Rig.data.name = 'UEFNMannequin_Skeleton'
Mesh.name = 'UEFNMannequin_Mesh'
Mesh.data.name = 'UEFNMannequin_Geometry'
for Object in list(bpy.data.objects):
    if Object.type == 'EMPTY':
        bpy.data.objects.remove(Object, do_unlink=True)

# The FBX's implicit armature-object root becomes one explicit animation bone.
# The existing 87 bone names, global rest matrices, and all skin weights survive.
bpy.context.view_layer.objects.active = Rig
Rig.select_set(True)
Mesh.select_set(False)
bpy.ops.object.mode_set(mode='EDIT')
RootBone = Rig.data.edit_bones.new('root')
RootBone.head = (0, 0, 0)
RootBone.tail = (0, 0, 0.12)
RootBone.use_deform = False
for Bone in Rig.data.edit_bones:
    if Bone != RootBone and Bone.parent is None:
        Bone.parent = RootBone
        Bone.use_connect = False
bpy.ops.object.mode_set(mode='OBJECT')
Rig.show_in_front = True
Rig.data.display_type = 'STICK'
Rig['SourceFbxSha256'] = ActualHash
Rig['AuthoringForward'] = '-Y'
Rig['AuthoringUp'] = '+Z'
Rig['LengthUnit'] = 'metre'
Rig['RootMotionBone'] = 'root'
Rig['SourceSchemaVersion'] = 1
for Bone in Rig.pose.bones:
    Bone.rotation_mode = 'QUATERNION'
    Bone.matrix_basis = Matrix.Identity(4)
bpy.context.view_layer.update()

BoneError = max(max(abs((Rig.matrix_world @ Rig.data.bones[Name].matrix_local)[Row][Column] - Before[Row][Column])
    for Row in range(4) for Column in range(4)) for Name, Before in BeforeBones.items())
VertexError = max(((Mesh.matrix_world @ Vertex.co) - Before).length
    for Vertex, Before in zip(Mesh.data.vertices, BeforeVertices))
if BoneError > 1e-4 or VertexError > 1e-5:
    raise ValueError(f'Normalization changed the source: bones={BoneError}, mesh={VertexError}')
Evaluated = Mesh.evaluated_get(bpy.context.evaluated_depsgraph_get())
EvaluatedMesh = Evaluated.to_mesh()
RestSkinError = max(((Evaluated.matrix_world @ Vertex.co) - Before).length
    for Vertex, Before in zip(EvaluatedMesh.vertices, BeforeVertices))
Evaluated.to_mesh_clear()
if RestSkinError > 1e-5:
    raise ValueError(f'Normalization changed rest-pose skinning: {RestSkinError}')
Report = {
    'schemaVersion': 1,
    'sourceFbx': 'Original/' + Source.name,
    'sourceSha256': ActualHash,
    'blenderVersion': bpy.app.version_string,
    'bindPoseBlend': BlendPath.name,
    'animatedBlend': 'UEFNMannequin.blend',
    'originalBones': len(BeforeBones),
    'authoringBones': len(Rig.data.bones),
    'rootBone': 'root',
    'vertices': len(Mesh.data.vertices),
    'triangles': sum(len(Polygon.vertices) - 2 for Polygon in Mesh.data.polygons),
    'maximumInfluences': max(sum(Group.weight > 0 for Group in Vertex.groups) for Vertex in Mesh.data.vertices),
    'maximumBindMatrixChange': BoneError,
    'maximumVertexPositionChange': VertexError,
    'maximumRestSkinningChange': RestSkinError,
    'lengthUnit': 'metre',
    'forward': '-Y',
    'up': '+Z',
    'fps': 30,
}
Scene['SourceAuthoring'] = 'Original Epic FBX; full source weights; explicit root bone'
Scene.frame_set(1)
bpy.ops.wm.save_as_mainfile(filepath=str(BlendPath))
(Output / 'source.json').write_text(json.dumps(Report, indent=2) + '\n', encoding='utf-8')
print('[UEFNSource]', json.dumps(Report))
