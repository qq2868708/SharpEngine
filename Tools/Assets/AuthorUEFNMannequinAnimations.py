"""Author four editable, fully keyed Blender Actions on the audited source rig.

Blender --background --factory-startup --python this_file
All transforms are solved in Blender's metre/Z-up/-Y-forward authoring space.
Runtime Content assets are never read or modified by this tool.
"""
import json
import math
from pathlib import Path

import bpy
from mathutils import Matrix, Quaternion, Vector

Root = Path(__file__).resolve().parents[2]
Output = Root / 'Resources/SourceAssets/UEFNMannequin'
BlendPath = Output / 'UEFNMannequin.blend'
if BlendPath.exists():
    raise FileExistsError(f'Preserve the existing animation source: {BlendPath}')
bpy.ops.wm.open_mainfile(filepath=str(Output / 'UEFNMannequin_BindPose.blend'), load_ui=False)
bpy.context.preferences.filepaths.save_version = 0
Scene = bpy.context.scene
Rig = bpy.data.objects['UEFNMannequin_Rig']
Mesh = bpy.data.objects['UEFNMannequin_Mesh']
Bones = list(Rig.data.bones)
Rest = {Bone.name: Bone.matrix_local.copy() for Bone in Bones}
Parents = {Bone.name: Bone.parent.name if Bone.parent else None for Bone in Bones}
Local = {Bone.name: Rest[Bone.parent.name].inverted() @ Rest[Bone.name]
    if Bone.parent else Rest[Bone.name].copy() for Bone in Bones}
RestHeads = {Name: Transform.translation.copy() for Name, Transform in Rest.items()}
AxisX = Vector((1, 0, 0))
AxisY = Vector((0, 1, 0))
AxisZ = Vector((0, 0, 1))
IdentityScale = Vector((1, 1, 1))
ReachClamps = []
Actions = []

def Smooth(Value):
    Value = max(0.0, min(1.0, Value))
    return Value * Value * (3.0 - 2.0 * Value)

def Curve(Phase, Points):
    if Phase <= Points[0][0]:
        return Points[0][1]
    for (First, Value), (Last, Next) in zip(Points, Points[1:]):
        if Phase <= Last:
            return Value + (Next - Value) * Smooth((Phase - First) / (Last - First))
    return Points[-1][1]

def Inherited(Name, Desired):
    if Name not in Desired:
        Parent = Parents[Name]
        Desired[Name] = Inherited(Parent, Desired) @ Local[Name] if Parent else Local[Name].copy()
    return Desired[Name]

def SetSegment(Name, Child, Start, End, Desired):
    RestDirection = RestHeads[Child] - RestHeads[Name]
    Direction = End - Start
    Swing = RestDirection.rotation_difference(Direction)
    Rotation = Swing @ Rest[Name].to_quaternion()
    Desired[Name] = Matrix.LocRotScale(Start, Rotation, IdentityScale)

def SolveLimb(First, Second, End, Target, Hint, Desired, Clip, Frame):
    Start = Inherited(First, Desired).translation.copy()
    LengthA = (RestHeads[Second] - RestHeads[First]).length
    LengthB = (RestHeads[End] - RestHeads[Second]).length
    Delta = Target - Start
    Distance = Delta.length
    Limited = max(abs(LengthA - LengthB) + 0.0001, min(LengthA + LengthB - 0.0001, Distance))
    if abs(Limited - Distance) > 0.002:
        ReachClamps.append({'clip': Clip, 'frame': Frame, 'bone': First,
            'requestedDistance': Distance, 'reachableDistance': Limited})
    Direction = Delta.normalized()
    Target = Start + Direction * Limited
    Along = (LengthA * LengthA - LengthB * LengthB + Limited * Limited) / (2 * Limited)
    Perpendicular = Hint - Direction * Hint.dot(Direction)
    Perpendicular.normalize()
    Joint = Start + Direction * Along + Perpendicular * math.sqrt(max(0, LengthA * LengthA - Along * Along))
    SetSegment(First, Second, Start, Joint, Desired)
    SetSegment(Second, End, Joint, Target, Desired)
    Desired[End] = Desired[Second] @ Local[End]
    return Target

def FootCycle(Phase, Stance, Travel, Lift):
    Phase = Phase % 1.0
    Front = Travel * Stance * 0.5
    if Phase < Stance:
        return Front - Travel * Phase, 0.0, True
    Swing = (Phase - Stance) / (1.0 - Stance)
    Forward = -Front + 2 * Front * Smooth(Swing)
    Height = Lift * math.sin(math.pi * Swing) ** 1.35
    return Forward, Height, False

def Pose(Clip, Phase, Frame, Travel):
    Desired = {}
    RootPosition = Vector((0, -Travel * Phase, 0))
    PelvisDrop = -0.025
    Lean = 0.0
    Breath = math.sin(math.tau * Phase)
    if Clip == 'Idle':
        PelvisDrop += 0.0025 * Breath
    elif Clip == 'Walk':
        PelvisDrop = -0.060 + 0.012 * (1 - math.cos(2 * math.tau * Phase)) * 0.5
        Lean = math.radians(3)
    elif Clip == 'Run':
        PelvisDrop = -0.100 + 0.045 * (1 - math.cos(2 * math.tau * Phase)) * 0.5
        Lean = math.radians(10)
    else:
        RootPosition.y = 0
        Takeoff = 0.25
        Landing = 0.68
        if Takeoff < Phase < Landing:
            AirTime = (Phase - Takeoff) * 1.4
            AirDuration = (Landing - Takeoff) * 1.4
            RootPosition.z = 4.905 * AirTime * (AirDuration - AirTime)
        PelvisDrop = Curve(Phase, [(0, -0.025), (0.16, -0.17), (0.25, -0.012),
            (0.46, -0.07), (0.65, -0.025), (0.72, -0.15), (1, -0.025)])
        Lean = math.radians(Curve(Phase, [(0, 0), (0.16, 14), (0.25, -3), (0.5, 2), (0.72, 10), (1, 0)]))
    Desired['root'] = Matrix.Translation(RootPosition) @ Rest['root']
    PelvisPosition = RestHeads['pelvis'] + RootPosition + Vector((0, 0, PelvisDrop))
    PelvisRotation = Quaternion(AxisX, Lean * 0.4) @ Rest['pelvis'].to_quaternion()
    Desired['pelvis'] = Matrix.LocRotScale(PelvisPosition, PelvisRotation, IdentityScale)
    for Name in ['spine_01', 'spine_02', 'spine_03', 'spine_04', 'spine_05']:
        Transform = Inherited(Name, Desired)
        Angle = Lean * 0.12 + (math.radians(0.15) * Breath if Clip == 'Idle' else 0)
        Rotation = Quaternion(AxisX, Angle) @ Transform.to_quaternion()
        Desired[Name] = Matrix.LocRotScale(Transform.translation, Rotation, IdentityScale)
    Head = Inherited('head', Desired)
    Desired['head'] = Matrix.LocRotScale(Head.translation,
        Quaternion(AxisX, -Lean * 0.55) @ Head.to_quaternion(), IdentityScale)

    Contacts = {}
    for Side, Sign, Offset in [('l', 1, 0.0), ('r', -1, 0.5)]:
        Forward = 0.0
        Height = 0.0
        Contact = True
        if Clip in ['Walk', 'Run']:
            Forward, Height, Contact = FootCycle(Phase + Offset,
                0.60 if Clip == 'Walk' else 0.32, Travel, 0.10 if Clip == 'Walk' else 0.24)
        elif Clip == 'Jump':
            Height = Curve(Phase, [(0, 0), (0.25, 0), (0.46, 0.13), (0.64, 0), (1, 0)])
            Forward = Curve(Phase, [(0, 0), (0.25, 0), (0.46, 0.035), (0.64, 0), (1, 0)])
            Contact = Phase <= 0.25 or Phase >= 0.68
        AnkleRest = RestHeads['foot_' + Side]
        Ankle = RootPosition + Vector((Sign * 0.115, AnkleRest.y - Forward, AnkleRest.z + Height))
        Ankle = SolveLimb('thigh_' + Side, 'calf_' + Side, 'foot_' + Side,
            Ankle, Vector((0, -1, 0)), Desired, Clip, Frame)
        # Keep the original foot orientation during planted support, and retain
        # the complete foot/ball hierarchy. No skin weights are reassigned.
        Desired['foot_' + Side] = Matrix.LocRotScale(Ankle,
            Rest['foot_' + Side].to_quaternion(), IdentityScale)
        Contacts[Side] = Contact

        Shoulder = Inherited('upperarm_' + Side, Desired).translation
        Swing = math.cos(math.tau * (Phase + Offset))
        HandForward = -0.025
        HandDrop = 0.455
        if Clip == 'Idle':
            HandForward += 0.003 * Breath
        elif Clip == 'Walk':
            HandForward = -0.19 * Swing
            HandDrop = 0.415 - 0.015 * math.sin(math.tau * (Phase + Offset))
        elif Clip == 'Run':
            HandForward = 0.075 - 0.22 * Swing
            HandDrop = 0.22 + 0.035 * Swing
        else:
            HandForward = Curve(Phase, [(0, -0.025), (0.16, -0.23), (0.29, 0.26),
                (0.46, 0.22), (0.65, 0.08), (0.72, 0.22), (1, -0.025)])
            HandDrop = Curve(Phase, [(0, 0.455), (0.16, 0.30), (0.29, 0.13),
                (0.46, 0.12), (0.65, 0.28), (0.72, 0.27), (1, 0.455)])
        Wrist = Shoulder + Vector((Sign * 0.035, -HandForward, -HandDrop))
        SolveLimb('upperarm_' + Side, 'lowerarm_' + Side, 'hand_' + Side,
            Wrist, Vector((Sign * 0.2, 1, 0)), Desired, Clip, Frame)

    for Side in ['l', 'r']:
        for Marker, Target in [('ik_foot_' + Side, 'foot_' + Side),
            ('ik_hand_' + Side, 'hand_' + Side)]:
            if Marker in Rest:
                Desired[Marker] = Desired[Target] @ Rest[Target].inverted() @ Rest[Marker]
    if 'ik_hand_gun' in Rest:
        Desired['ik_hand_gun'] = Desired['hand_r'] @ Rest['hand_r'].inverted() @ Rest['ik_hand_gun']
    for Bone in Bones:
        Inherited(Bone.name, Desired)
    for Bone in Bones:
        Parent = Bone.parent
        Arguments = {}
        if Parent is not None:
            Arguments['parent_matrix'] = Desired[Parent.name]
            Arguments['parent_matrix_local'] = Rest[Parent.name]
        Basis = Bone.convert_local_to_pose(Desired[Bone.name], Rest[Bone.name], invert=True, **Arguments)
        PoseBone = Rig.pose.bones[Bone.name]
        Position, Rotation, Scale = Basis.decompose()
        if PoseBone.rotation_quaternion.dot(Rotation) < 0:
            Rotation.negate()
        PoseBone.location = Position
        PoseBone.rotation_quaternion = Rotation
        PoseBone.scale = Scale
        PoseBone.keyframe_insert('location', frame=Frame, group=Bone.name)
        PoseBone.keyframe_insert('rotation_quaternion', frame=Frame, group=Bone.name)
        PoseBone.keyframe_insert('scale', frame=Frame, group=Bone.name)
    return RootPosition, Contacts

Rig.animation_data_create()
for Clip, Last, Travel, Loop in [('Idle', 61, 0.0, True), ('Walk', 33, 0.90, True),
    ('Run', 21, 1.80, True), ('Jump', 43, 0.0, False)]:
    for Bone in Rig.pose.bones:
        Bone.matrix_basis = Matrix.Identity(4)
    Action = bpy.data.actions.new(Clip)
    Action.use_fake_user = True
    Action.use_frame_range = True
    Action.frame_start = 1
    Action.frame_end = Last
    Action.use_cyclic = Loop
    Action['RootMotionBone'] = 'root'
    Action['RootMotionAxes'] = 'Z' if Clip == 'Jump' else ('Y' if Travel else 'none')
    Action['ForwardDistanceMetres'] = Travel
    Action['DurationSeconds'] = (Last - 1) / 30
    Action['AuthoredInBlender'] = True
    Rig.animation_data.action = Action
    RootSamples = []
    ContactSamples = []
    for Frame in range(1, Last + 1):
        Scene.frame_set(Frame)
        Phase = (Frame - 1) / (Last - 1)
        Position, Contacts = Pose(Clip, Phase, Frame, Travel)
        RootSamples.append(list(Position))
        ContactSamples.append(Contacts)
    for Layer in Action.layers:
        for Strip in Layer.strips:
            for Bag in Strip.channelbags:
                for FCurve in Bag.fcurves:
                    for Point in FCurve.keyframe_points:
                        Point.interpolation = 'LINEAR'
    Markers = [('Start', 1), ('LoopEnd', Last)] if Loop else [
        ('Ready', 1), ('Anticipation', 8), ('Takeoff', 11), ('Apex', 21), ('Landing', 30), ('Recovered', 43)]
    for Name, Frame in Markers:
        Action.pose_markers.new(Name).frame = Frame
    Action.asset_mark()
    Actions.append({'name': Clip, 'firstFrame': 1, 'lastFrame': Last,
        'duration': (Last - 1) / 30, 'loop': Loop, 'rootTravel': list(Vector(RootSamples[-1]) - Vector(RootSamples[0])),
        'maximumRootHeight': max(Sample[2] for Sample in RootSamples), 'contacts': ContactSamples})
    print('[UEFNAnimation]', Clip, 'frames', Last, 'root', Actions[-1]['rootTravel'])

if ReachClamps:
    (Output / 'reach-diagnostics.json').write_text(json.dumps(ReachClamps, indent=2), encoding='utf-8')
    raise ValueError(f'{len(ReachClamps)} limb targets exceed the source bone lengths')

Rig.animation_data.action = bpy.data.actions['Idle']
Rig.animation_data.action_slot = bpy.data.actions['Idle'].slots[0]
Scene.frame_start = 1
Scene.frame_end = 61
Scene.frame_set(1)
bpy.context.view_layer.update()
Rig['ActionGuide'] = 'Select Idle / Walk / Run / Jump in the Action Editor; ranges are recorded on each Action.'
Scene['AnimationSourceContract'] = '30 fps; root bone owns travel; Walk/Run forward -Y; Jump vertical +Z; full original weights'
ViewRotation = Vector((2.4, -5.5, 1.2)).to_track_quat('Z', 'Y')
for Screen in bpy.data.screens:
    for Area in Screen.areas:
        if Area.type == 'VIEW_3D':
            Space = Area.spaces.active
            Space.region_3d.view_location = Vector((0, 0, 0.86))
            Space.region_3d.view_distance = 2.7
            Space.region_3d.view_rotation = ViewRotation
            Space.clip_end = 100
        elif Area.type == 'DOPESHEET_EDITOR':
            Area.spaces.active.mode = 'ACTION'
            Area.spaces.active.show_region_ui = False
Guide = bpy.data.texts.new('START_HERE')
Guide.write('UEFN Mannequin animation source\n\n'
    'Actions: Idle (1-61), Walk (1-33), Run (1-21), Jump (1-43), 30 fps.\n'
    'Choose an Action in the Action Editor and set its frame range.\n'
    'Walk/Run contain forward travel on root; Jump contains vertical root motion.\n'
    'All original skin weights survive. The engine currently needs a corrected importer and Root Motion support.\n'
    'Original/ contains the verified Epic FBX. UEFNMannequin_BindPose.blend is the clean authoring baseline.\n')
bpy.ops.wm.save_as_mainfile(filepath=str(BlendPath))
Report = {'schemaVersion': 1, 'blenderVersion': bpy.app.version_string,
    'blend': BlendPath.name, 'fps': 30, 'rootBone': 'root', 'actions': Actions,
    'reachClampCount': len(ReachClamps)}
(Output / 'animations.json').write_text(json.dumps(Report, indent=2) + '\n', encoding='utf-8')
print('[UEFNAnimation] saved', BlendPath)
