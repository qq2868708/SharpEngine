"""Stage canonical assets and independent Blender reference samples for reimport.

Blender --background --factory-startup --python this_file
Publication is performed only after the C# runtime comparison succeeds.
"""
import hashlib
import json
from pathlib import Path
import shutil
import struct

import bpy
import numpy as np
from mathutils import Matrix, Vector

Root = Path(__file__).resolve().parents[2]
SourceFolder = Root / 'Resources/SourceAssets/UEFNMannequin'
Blend = SourceFolder / 'UEFNMannequin.blend'
Output = Root / 'Saved/Validation/SkeletalReimport-20261020'
Stage = Output / 'Stage'
Stage.mkdir(parents=True, exist_ok=True)
Metadata = json.loads((SourceFolder / 'animations.json').read_text())
BlendHash = hashlib.sha256(Blend.read_bytes()).hexdigest().upper()
bpy.ops.wm.open_mainfile(filepath=str(Blend), load_ui=False, use_scripts=False)
Rig = bpy.data.objects['UEFNMannequin_Rig']
Mesh = bpy.data.objects['UEFNMannequin_Mesh']
Scene = bpy.context.scene
for Modifier in Mesh.modifiers:
    if Modifier.type == 'ARMATURE' and Modifier.use_deform_preserve_volume:
        raise ValueError('Dual-quaternion skinning cannot be imported as linear blend skinning')
# Proper rotation: Blender Z-up/-Y forward -> engine Y-up/-Z forward.
# Mesh, bind matrices, sampled pose matrices and reference vertices all use C.
Conversion = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
InverseConversion = Conversion.inverted()
Bones = list(Rig.data.bones)
BoneIndices = {Bone.name: Index for Index, Bone in enumerate(Bones)}
BindModels = {Bone.name: Conversion @ Rig.matrix_world @ Bone.matrix_local @ InverseConversion for Bone in Bones}

def Transform(MatrixValue):
    Position, Rotation, Scale = MatrixValue.decompose()
    Rotation.normalize()
    Rebuilt = Matrix.LocRotScale(Position, Rotation, Scale)
    Error = max(abs(MatrixValue[Row][Column] - Rebuilt[Row][Column]) for Row in range(4) for Column in range(4))
    if Error > 1e-5:
        raise ValueError(f'Bone transform contains unrepresentable shear: {Error}')
    return {'position': list(Position), 'rotation': [Rotation.x, Rotation.y, Rotation.z, Rotation.w], 'scale': list(Scale)}

BoneRecords = []
for Bone in Bones:
    Local = BindModels[Bone.parent.name].inverted() @ BindModels[Bone.name] if Bone.parent else BindModels[Bone.name]
    Trs = Transform(Local)
    BoneRecords.append({'boneId': Bone.name, 'name': Bone.name, 'parentBoneId': Bone.parent.name if Bone.parent else None,
        'bindPosition': Trs['position'], 'bindRotation': Trs['rotation'], 'bindScale': Trs['scale']})
Positions = [list(Conversion @ Mesh.matrix_world @ Vertex.co) for Vertex in Mesh.data.vertices]
Triangles = [Index for Polygon in Mesh.data.polygons for Index in Polygon.vertices]
if any(len(Polygon.vertices) != 3 for Polygon in Mesh.data.polygons):
    raise ValueError('Only triangulated source topology is supported')
Influences = []
for Vertex in Mesh.data.vertices:
    Items = [(BoneIndices[Mesh.vertex_groups[Group.group].name], float(Group.weight)) for Group in Vertex.groups
        if Group.weight > 0 and Mesh.vertex_groups[Group.group].name in BoneIndices]
    Items.sort(key=lambda Item: (-Item[1], Item[0]))
    if not Items or len(Items) > 12:
        raise ValueError(f'Vertex {Vertex.index} has unsupported influence count {len(Items)}')
    Total = sum(Weight for Index, Weight in Items)
    Influences.append({'boneIndices': [Index for Index, Weight in Items], 'weights': [Weight / Total for Index, Weight in Items]})

Skeleton = {'schemaVersion': 2, 'kind': 'skeleton', 'assetId': 'engine://skeletons/uefn-mannequin',
    'name': 'UEFN Mannequin Skeleton', 'previewMeshId': 'engine://meshes/uefn-mannequin', 'bones': BoneRecords}
SkeletalMesh = {'schemaVersion': 2, 'kind': 'skeletal-mesh', 'assetId': 'engine://meshes/uefn-mannequin',
    'name': 'UEFN Mannequin Skeletal Mesh', 'skeletonId': Skeleton['assetId'], 'bones': BoneRecords,
    'positions': Positions, 'indices': Triangles, 'influences': Influences, 'defaultMaterial': 'engine://materials/default'}
Files = {}
def Write(Relative, Data):
    PathValue = Stage / Relative
    PathValue.parent.mkdir(parents=True, exist_ok=True)
    PathValue.write_text(json.dumps(Data, indent=2, allow_nan=False) + '\n', encoding='utf-8')
    Files[Data['assetId']] = Relative
Write('Content/Assets/Skeletons/UEFNMannequin.sharpskeleton', Skeleton)
Write('Content/Assets/Meshes/UEFNMannequin.sharpskeletalmesh', SkeletalMesh)

def Sample(Time):
    Frame = 1 + Time * 30
    IntegerFrame = int(Frame)
    Scene.frame_set(IntegerFrame, subframe=Frame - IntegerFrame)
    bpy.context.view_layer.update()
    EvaluatedRig = Rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
    return {Bone.name: Conversion @ EvaluatedRig.matrix_world @ EvaluatedRig.pose.bones[Bone.name].matrix @ InverseConversion for Bone in Bones}

SampleRate = 240
GoldenRate = 480
with (Output / 'blender-reference.bin').open('wb') as Reference:
    Reference.write(struct.pack('<4sIIII', b'SEBG', 1, len(Bones), len(Positions), len(Metadata['actions'])))
    for Clip in Metadata['actions']:
        Action = bpy.data.actions[Clip['name']]
        Rig.animation_data.action = Action
        Rig.animation_data.action_slot = Action.slots[0]
        Duration = (Clip['lastFrame'] - 1) / 30
        Tracks = [{'trackId': Bone.name + '-track', 'boneId': Bone.name, 'keyframes': []} for Bone in Bones]
        for Step in range(round(Duration * SampleRate) + 1):
            Time = min(Duration, Step / SampleRate)
            Models = Sample(Time)
            for Index, Bone in enumerate(Bones):
                Local = Models[Bone.parent.name].inverted() @ Models[Bone.name] if Bone.parent else Models[Bone.name]
                Tracks[Index]['keyframes'].append({'time': Time, **Transform(Local)})
        for Track in Tracks:
            First = Track['keyframes'][0]
            Constant = all(max(abs(Key[Field][Index] - First[Field][Index])
                for Index in range(len(First[Field]))) <= 1e-6
                for Key in Track['keyframes'] for Field in ['position', 'rotation', 'scale'])
            if Constant:
                Track['keyframes'] = [First]
        Animation = {'schemaVersion': 2, 'kind': 'skeletal-animation',
            'assetId': 'engine://animations/uefn-mannequin-' + Clip['name'].lower(),
            'name': 'UEFN Mannequin ' + Clip['name'], 'duration': Duration, 'sampleRate': SampleRate,
            'skeletonId': Skeleton['assetId'], 'bones': BoneRecords, 'tracks': Tracks, 'notifies': []}
        # Preserve authored Notify records only if they still fit the new clip.
        Existing = Root / ('Content/Assets/Animations/UEFNMannequin' + Clip['name'] + '.sharpskeletalanimation')
        Previous = json.loads(Existing.read_text())
        Notifies = Previous.get('notifies', [])
        if any(Notify['time'] + Notify['duration'] > Duration + 1e-9 for Notify in Notifies):
            raise ValueError('An existing Notify exceeds the reimported duration: ' + Clip['name'])
        Animation['rootMotion'] = Previous.get('rootMotion', {'enabled': False,
            'boneId': next(Bone['boneId'] for Bone in BoneRecords if Bone['parentBoneId'] is None)})
        Animation['notifies'] = Notifies
        Write('Content/Assets/Animations/UEFNMannequin' + Clip['name'] + '.sharpskeletalanimation', Animation)
        Name = Clip['name'].encode('utf-8')
        Count = round(Duration * GoldenRate) + 1
        Reference.write(struct.pack('<I', len(Name)) + Name + struct.pack('<I', Count))
        for Step in range(Count):
            Time = min(Duration, Step / GoldenRate)
            Models = Sample(Time)
            Graph = bpy.context.evaluated_depsgraph_get()
            Evaluated = Mesh.evaluated_get(Graph)
            EvaluatedMesh = Evaluated.to_mesh()
            Vertices = np.array([list(Conversion @ Evaluated.matrix_world @ Vertex.co) for Vertex in EvaluatedMesh.vertices], dtype='<f4')
            Evaluated.to_mesh_clear()
            Reference.write(struct.pack('<d', Time))
            Matrices = np.array([np.array(Models[Bone.name]).T for Bone in Bones], dtype='<f4')
            Reference.write(Matrices.tobytes())
            Reference.write(Vertices.tobytes())
        print('[BlenderImport]', Clip['name'], 'keys per bone', len(Tracks[0]['keyframes']), 'reference samples', Count)

Manifest = json.loads((Root / 'Content/Assets/assets.json').read_text())
Selected = []
for Record in Manifest['assets']:
    Identifier = 'engine://' + Record['id']
    if Identifier in Files:
        Record['metadata'].update({'sourceFormat': 'Blender', 'sourceFile': Blend.name,
            'sourcePath': str(Blend.relative_to(Root)).replace('\\', '/'), 'sourceSha256': BlendHash,
            'coordinateSystem': 'Y-up, right-handed, -Z forward'})
        if Record['type'] == 'SkeletalMesh':
            Record['metadata']['influences'] = str(max(len(Influence['weights']) for Influence in Influences))
            Record['metadata']['schemaVersion'] = '2'
        if Record['type'] == 'SkeletalAnimation':
            ClipName = Record['name'].split()[-1]
            Clip = next(Item for Item in Metadata['actions'] if Item['name'] == ClipName)
            Record['metadata'].update({'duration': str(Clip['duration']), 'sampleRate': str(SampleRate), 'authoring': 'blender-action'})
        Selected.append(Record)
    elif Record['id'] in ['materials/default', 'worlds/main']:
        Selected.append(Record)
        Destination = Stage / 'Content' / Record['path']
        Destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(Root / 'Content' / Record['path'], Destination)
(Stage / 'engine.json').write_bytes((Root / 'engine.json').read_bytes())
StageManifest = dict(Manifest)
StageManifest['assets'] = Selected
(Stage / 'Content/Assets/assets.json').write_text(json.dumps(StageManifest, indent=2) + '\n', encoding='utf-8')
(Output / 'publication-manifest.json').write_text(json.dumps(Manifest, indent=2) + '\n', encoding='utf-8')
(Output / 'import.json').write_text(json.dumps({'schemaVersion': 1, 'sourceBlend': str(Blend.relative_to(Root)).replace('\\', '/'),
    'sourceSha256': BlendHash, 'sampleRate': SampleRate, 'referenceSampleRate': GoldenRate,
    'files': list(Files.values()), 'maximumInfluences': max(len(Influence['weights']) for Influence in Influences)}, indent=2) + '\n', encoding='utf-8')
print('[BlenderImport] staged', Stage)
