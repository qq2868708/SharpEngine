"""Reopen the saved Blender source, validate actual Actions and export preview geometry."""
import hashlib
import json
from pathlib import Path

import bpy
import numpy as np
from mathutils import Matrix, Vector

Root = Path(__file__).resolve().parents[2]
Output = Root / 'Resources/SourceAssets/UEFNMannequin'
Evidence = Root / 'Saved/Validation/UEFNMannequinAuthoring-20261020'
Evidence.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.open_mainfile(filepath=str(Output / 'UEFNMannequin.blend'), load_ui=False, use_scripts=False)
Rig = bpy.data.objects['UEFNMannequin_Rig']
Mesh = bpy.data.objects['UEFNMannequin_Mesh']
Scene = bpy.context.scene
Metadata = json.loads((Output / 'animations.json').read_text())
Triangles = np.array([list(Polygon.vertices) for Polygon in Mesh.data.polygons], dtype=np.int32)
if Triangles.shape[1] != 3:
    raise ValueError('Expected the original triangulated source')
Original = np.array([Vertex.co[:] for Vertex in Mesh.data.vertices])
Edges = np.array(sorted({tuple(sorted((Triangle[Corner], Triangle[(Corner + 1) % 3])))
    for Triangle in Triangles for Corner in range(3)}))
RestLengths = np.linalg.norm(Original[Edges[:, 0]] - Original[Edges[:, 1]], axis=1)
ValidEdges = RestLengths > 0.005
Report = {'schemaVersion': 1, 'blendSha256': hashlib.sha256((Output / 'UEFNMannequin.blend').read_bytes()).hexdigest().upper(),
    'blenderVersion': bpy.app.version_string, 'actions': [], 'errors': []}
with bpy.data.libraries.load(str(Output / 'UEFNMannequin_BindPose.blend'), link=False) as (Available, Loaded):
    Loaded.objects = ['UEFNMannequin_Mesh']
Baseline = Loaded.objects[0]
MaximumWeightError = 0.0
for Vertex, OriginalVertex in zip(Mesh.data.vertices, Baseline.data.vertices):
    CurrentWeights = {Mesh.vertex_groups[Group.group].name: Group.weight for Group in Vertex.groups}
    SourceWeights = {Baseline.vertex_groups[Group.group].name: Group.weight for Group in OriginalVertex.groups}
    MaximumWeightError = max(MaximumWeightError, max(abs(CurrentWeights.get(Name, 0) - SourceWeights.get(Name, 0))
        for Name in CurrentWeights.keys() | SourceWeights.keys()))
Report['maximumSourceWeightChange'] = MaximumWeightError
Report['maximumInfluences'] = max(sum(Group.weight > 0 for Group in Vertex.groups) for Vertex in Mesh.data.vertices)
if MaximumWeightError != 0:
    Report['errors'].append('Animation authoring changed source skin weights')
if set(Action.name for Action in bpy.data.actions) != {'Idle', 'Walk', 'Run', 'Jump'}:
    Report['errors'].append('Expected exactly four Actions')
if len(Rig.data.bones) != 88 or len(Mesh.data.vertices) != 6009 or len(Triangles) != 9324:
    Report['errors'].append('Original bone/geometry counts changed')
PreviewArrays = {'triangles': Triangles}
for Clip in Metadata['actions']:
    Action = bpy.data.actions[Clip['name']]
    Rig.animation_data.action = Action
    Rig.animation_data.action_slot = Action.slots[0]
    Positions = []
    Roots = []
    Models = []
    MaximumScaleError = 0.0
    MaximumStretch = 0.0
    MinimumZ = 100.0
    ContactMotions = []
    Previous = {}
    for Frame in range(Clip['firstFrame'], Clip['lastFrame'] + 1):
        Scene.frame_set(Frame)
        bpy.context.view_layer.update()
        Graph = bpy.context.evaluated_depsgraph_get()
        Evaluated = Mesh.evaluated_get(Graph)
        EvaluatedMesh = Evaluated.to_mesh()
        Vertices = np.array([(Evaluated.matrix_world @ Vertex.co)[:] for Vertex in EvaluatedMesh.vertices])
        Evaluated.to_mesh_clear()
        if not np.all(np.isfinite(Vertices)):
            Report['errors'].append(f'{Action.name} frame {Frame}: non-finite vertices')
        Positions.append(Vertices)
        RootPosition = Rig.pose.bones['root'].matrix.translation.copy()
        Roots.append(np.array(RootPosition[:]))
        Models.append(np.array([np.array(Bone.matrix) for Bone in Rig.pose.bones]))
        MinimumZ = min(MinimumZ, float(np.min(Vertices[:, 2])))
        CurrentLengths = np.linalg.norm(Vertices[Edges[:, 0]] - Vertices[Edges[:, 1]], axis=1)
        MaximumStretch = max(MaximumStretch, float(np.max(CurrentLengths[ValidEdges] / RestLengths[ValidEdges])))
        for Bone in Rig.pose.bones:
            MaximumScaleError = max(MaximumScaleError, max(abs(Value - 1) for Value in Bone.scale))
        for Side in ['l', 'r']:
            Ankle = Rig.pose.bones['foot_' + Side].matrix.translation.copy()
            Contact = Clip['contacts'][Frame - 1][Side]
            if Contact and Side in Previous and Previous[Side][0]:
                ContactMotions.append((Ankle - Previous[Side][1]).length)
            Previous[Side] = (Contact, Ankle)
    RelativeFirst = Positions[0] - Roots[0]
    RelativeLast = Positions[-1] - Roots[-1]
    LoopError = float(np.max(np.linalg.norm(RelativeLast - RelativeFirst, axis=1)))
    ContactError = max(ContactMotions, default=0.0)
    if MaximumScaleError > 1e-4:
        Report['errors'].append(f'{Action.name}: bone scale changed')
    if Clip['loop'] and LoopError > 1e-4:
        Report['errors'].append(f'{Action.name}: loop seam {LoopError} metres')
    if ContactError > 0.002:
        Report['errors'].append(f'{Action.name}: planted ankle drift {ContactError} metres/frame')
    if MinimumZ < -0.01:
        Report['errors'].append(f'{Action.name}: floor penetration {MinimumZ} metres')
    Report['actions'].append({'name': Action.name, 'framesChecked': len(Positions),
        'rootTravel': (Roots[-1] - Roots[0]).tolist(),
        'maximumRootHeight': float(np.max(np.array(Roots)[:, 2])),
        'rootRelativeEndpointVertexError': LoopError,
        'maximumPlantedAnkleDriftPerFrame': ContactError,
        'maximumBoneScaleError': MaximumScaleError,
        'minimumVertexHeight': MinimumZ,
        'maximumEdgeStretch': MaximumStretch,
        'curveCount': sum(len(Bag.fcurves) for Layer in Action.layers for Strip in Layer.strips for Bag in Strip.channelbags)})
    PreviewArrays[Action.name + '_positions'] = np.array(Positions, dtype=np.float32)
    PreviewArrays[Action.name + '_roots'] = np.array(Roots, dtype=np.float32)
    PreviewArrays[Action.name + '_bones'] = np.array(Models, dtype=np.float32)
np.savez_compressed(Evidence / 'evaluated-geometry.npz', **PreviewArrays)
(Output / 'validation.json').write_text(json.dumps(Report, indent=2) + '\n', encoding='utf-8')
print(json.dumps(Report, indent=2))
if Report['errors']:
    raise ValueError('Source validation failed')
